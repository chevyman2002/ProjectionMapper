using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectionMapper.Models;
using ProjectionMapper.Rendering;
using System.Numerics;

namespace ProjectionMapper.Services
{
 /// <summary>
 /// VideoService manages FFmpegUnifiedDecoder instances for synchronized video and audio playback per layer.
 /// Implements frame forwarding to the renderer with unified audio/video synchronization.
 /// </summary>
 public sealed class VideoService : IVideoService, IDisposable
 {
 private readonly RendererManager _rendererManager;

 // LayerId -> (decoder, cts, model, lastFrame, isPaused, savedPosition)
 private readonly ConcurrentDictionary<string, (FFmpegUnifiedDecoder? decoder, CancellationTokenSource? cts, LayerModel model, BitmapSource? lastFrame, bool isPaused, TimeSpan savedPosition)> _decoders
 = new();

 // mesh layers that reference host sources: meshId -> LayerModel
 private readonly ConcurrentDictionary<string, LayerModel> _meshLayers = new();

 // last forwarded frame timestamp per layer used for throttling/coalescing FrameDecoded events
 private readonly ConcurrentDictionary<string, DateTime> _lastFrameSent = new();

 // minimum interval between FrameDecoded events forwarded to subscribers (throttle)
 private readonly TimeSpan _minFrameInterval = TimeSpan.FromMilliseconds(33); // ~30 fps by default

 private readonly string? _ffmpegPath;

 public VideoService(RendererManager rendererManager, string? ffmpegPath = null)
 {
 _rendererManager = rendererManager ?? throw new ArgumentNullException(nameof(rendererManager));
 _ffmpegPath = ffmpegPath;
 }

 /// <summary>
 /// Event raised when a decoder produces a new frame for a layer. Parameters: layerId, frozen BitmapSource (may be null).
 /// Note: events are throttled per-layer to reduce UI spam; consumers can call TryGetLastFrame to read the latest cached frame.
 /// </summary>
 public event Action<string, BitmapSource?>? FrameDecoded;

 private void InvokeOnUi(Action action)
 {
 try
 {
 var app = Application.Current;
 if (app == null || app.Dispatcher == null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
 {
 // fallback: run inline
 try { action(); } catch { }
 }
 else
 {
 app.Dispatcher.BeginInvoke((Action)(() => { try { action(); } catch { } }));
 }
 }
 catch { try { action(); } catch { } }
 }

 public Task<bool> RegisterLayerAsync(LayerModel layer) => RegisterLayerAsync(layer, playAudio: false);

 public async Task<bool> RegisterLayerAsync(LayerModel layer, bool playAudio = false)
 {
 if (layer == null) throw new ArgumentNullException(nameof(layer));
 if (string.IsNullOrWhiteSpace(layer.SourcePath)) return false;
 if (!File.Exists(layer.SourcePath)) throw new FileNotFoundException("Source video not found", layer.SourcePath);

 string ffmpegExecutable = ResolveFfmpegExecutable();

 var w = Math.Max(1, layer.Width >0 ? layer.Width :640);
 var h = Math.Max(1, layer.Height >0 ? layer.Height :480);

 // Ensure a stable key and keep model updated
 var layerKey = layer.Id ?? Guid.NewGuid().ToString("N");

 Debug.WriteLine($"VideoService.RegisterLayerAsync: Registering layer {layerKey} (playAudio: {playAudio})");

 // If a decoder already exists for this layer, update model and audio settings if active
 if (_decoders.TryGetValue(layerKey, out var existing) && existing.decoder != null && existing.cts != null && !existing.isPaused)
 {
 try
 {
 Debug.WriteLine($"VideoService.RegisterLayerAsync: Updating existing active decoder for layer {layerKey}");
 _decoders[layerKey] = (existing.decoder, existing.cts, layer, existing.lastFrame, existing.isPaused, existing.savedPosition);

 // Update audio settings on existing decoder
 existing.decoder.AudioEnabled = playAudio || layer.PlayAudio;
 existing.decoder.Volume =1.0f; // Fixed volume at100%
 }
 catch (Exception ex)
 {
 Debug.WriteLine($"VideoService.RegisterLayerAsync: Error updating existing decoder for {layerKey}: {ex}");
 return false;
 }

 return true;
 }

 Debug.WriteLine($"VideoService.RegisterLayerAsync: Creating new decoder for layer {layerKey}");

 var decoder = new FFmpegUnifiedDecoder(layer.SourcePath, w, h, ffmpegExecutable)
 {
 Loop = true,
 AudioEnabled = playAudio || layer.PlayAudio,
 Volume =1.0f // Fixed volume at100%
 };

 var cts = new CancellationTokenSource();

 // Subscribe to timestamped frames to allow AV sync work
 decoder.FrameDecodedWithTimestamp += (bmp, pts) => { };

 // Process frames on UI thread to avoid WPF cross-thread exceptions
 decoder.FrameDecoded += bmp =>
 {
 if (bmp == null) return;
 InvokeOnUi(() =>
 {
 try
 {
 ProcessDecodedFrameOnUi(layer, layerKey, decoder, cts, bmp, w, h);
 }
 catch (Exception ex)
 {
 Debug.WriteLine($"VideoService.RegisterLayerAsync: Error processing frame for {layerKey}: {ex}");
 }
 });
 };

 // Preserve existing last frame if any
 BitmapSource? existingLast = null;
 if (_decoders.TryGetValue(layerKey, out var existingTuple)) existingLast = existingTuple.lastFrame;
 _decoders.AddOrUpdate(layerKey, (decoder, cts, layer, existingLast, false, TimeSpan.Zero), (k, old) => (decoder, cts, layer, old.lastFrame, false, old.savedPosition));

 Debug.WriteLine($"VideoService.RegisterLayerAsync: Starting decoder task for layer {layerKey}");

 // Start decoder
 _ = Task.Run(async () =>
 {
 try
 {
 await decoder.StartAsync(cts.Token).ConfigureAwait(false);
 Debug.WriteLine($"VideoService.RegisterLayerAsync: Decoder started successfully for layer {layerKey}");
 }
 catch (OperationCanceledException)
 {
 Debug.WriteLine($"VideoService.RegisterLayerAsync: Decoder start cancelled for layer {layerKey} - This is normal during pause");
 }
 catch (Exception ex)
 {
 Debug.WriteLine($"VideoService.RegisterLayerAsync: Decoder start failed for layer {layerKey}: {ex}");
 await UnregisterLayerAsync(layerKey).ConfigureAwait(false);
 }
 }, cts.Token);

 return true;
 }

 private void ProcessDecodedFrameOnUi(LayerModel layer, string layerKey, FFmpegUnifiedDecoder decoder, CancellationTokenSource cts, BitmapSource bmp, int defaultW, int defaultH)
 {
 if (bmp == null) return;

 BitmapSource sourceFrozen = bmp;
 try
 {
 if (!sourceFrozen.IsFrozen)
 {
 var clone = sourceFrozen.Clone();
 clone.Freeze();
 sourceFrozen = clone;
 }
 }
 catch { }

 BitmapSource bmpToSubmit = sourceFrozen;

 try
 {
 if (Math.Abs(layer.RotationDegrees) >1e-6 && sourceFrozen != null)
 {
 bmpToSubmit = RotateBitmap(sourceFrozen, layer.RotationDegrees);
 }
 }
 catch
 {
 bmpToSubmit = sourceFrozen;
 }

 try { if (!bmpToSubmit.IsFrozen) try { bmpToSubmit.Freeze(); } catch { } } catch { }

 try
 {
 if (_decoders.TryGetValue(layerKey, out var current) &&
 current.decoder == decoder &&
 current.cts == cts)
 {
 _decoders.AddOrUpdate(layerKey,
 (decoder, cts, layer, bmpToSubmit, false, TimeSpan.Zero),
 (k, old) => (old.decoder ?? decoder, old.cts ?? cts, old.model, bmpToSubmit, old.isPaused, old.savedPosition));
 }
 else
 {
 Debug.WriteLine($"VideoService: Ignoring frame for {layerKey} from obsolete decoder");
 return;
 }
 }
 catch { }

 var destRect = new Rect(layer.X, layer.Y, layer.Width >0 ? layer.Width : defaultW, layer.Height >0 ? layer.Height : defaultH);

 if (!layer.PreviewOnly && layer.Visible)
 {
 try
 {
 _rendererManager.SubmitLayerFrameForMonitor(layer.Id, bmpToSubmit, destRect, null, layer.Opacity, layer.TargetMonitorIndex);
 }
 catch (Exception ex) { Debug.WriteLine($"VideoService: SubmitLayerFrame failed: {ex}"); }
 }

 try
 {
 foreach (var kv in _meshLayers.ToArray())
 {
 var mesh = kv.Value;
 if (mesh == null) continue;
 if (string.IsNullOrEmpty(mesh.SourceId)) continue;
 if (mesh.SourceId != layer.Id) continue;
 if (!mesh.Visible) continue;

 BitmapSource frameForMesh = bmpToSubmit;
 try
 {
 float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
 foreach (var p in mesh.MeshPoints)
 {
 minX = Math.Min(minX, p.X);
 minY = Math.Min(minY, p.Y);
 maxX = Math.Max(maxX, p.X);
 maxY = Math.Max(maxY, p.Y);
 }

 minX = Math.Max(0f, Math.Min(1f, minX));
 minY = Math.Max(0f, Math.Min(1f, minY));
 maxX = Math.Max(0f, Math.Min(1f, maxX));
 maxY = Math.Max(0f, Math.Min(1f, maxY));

 int srcW = frameForMesh.PixelWidth; int srcH = frameForMesh.PixelHeight;
 int srcX = (int)Math.Floor(minX * srcW);
 int srcY = (int)Math.Floor(minY * srcH);
 int cropW = Math.Max(1, (int)Math.Ceiling((maxX - minX) * srcW));
 int cropH = Math.Max(1, (int)Math.Ceiling((maxY - minY) * srcH));

 if (srcX <0) srcX =0; if (srcY <0) srcY =0;
 if (srcX + cropW > srcW) cropW = srcW - srcX;
 if (srcY + cropH > srcH) cropH = srcH - srcY;

 if (cropW >0 && cropH >0)
 {
 var cb = new CroppedBitmap(frameForMesh, new Int32Rect(srcX, srcY, cropW, cropH));
 try { cb.Freeze(); } catch { }
 frameForMesh = cb;
 }
 }
 catch { frameForMesh = bmpToSubmit; }

 var meshDest = new Rect(mesh.X, mesh.Y, Math.Max(1, mesh.Width), Math.Max(1, mesh.Height));
 try
 {
 Point[]? destQuad = null;
 try
 {
 destQuad = _rendererManager.MapNormalizedToRendererPoints(mesh.OutputMeshPoints, mesh.TargetMonitorIndex >=0 ? mesh.TargetMonitorIndex : null);
 }
 catch (Exception exMap)
 {
 Debug.WriteLine($"VideoService: MapNormalizedToRendererPoints threw: {exMap}");
 destQuad = null;
 }

 if (destQuad == null)
 {
 var pts = mesh.OutputMeshPoints;
 if (pts != null && pts.Length >=4)
 {
 destQuad = new Point[4]
 {
 new Point(mesh.X + pts[0].X * mesh.Width, mesh.Y + pts[0].Y * mesh.Height),
 new Point(mesh.X + pts[1].X * mesh.Width, mesh.Y + pts[1].Y * mesh.Height),
 new Point(mesh.X + pts[2].X * mesh.Width, mesh.Y + pts[2].Y * mesh.Height),
 new Point(mesh.X + pts[3].X * mesh.Width, mesh.Y + pts[3].Y * mesh.Height)
 };
 }
 else
 {
 Debug.WriteLine("VideoService: OutputMeshPoints not available for mesh, destQuad remains null");
 }
 }

 InvokeOnUi(() => { try { _rendererManager.SubmitLayerFrame(mesh.Id ?? Guid.NewGuid().ToString("N"), frameForMesh, meshDest, destQuad, mesh.Opacity); } catch { } });
 }
 catch (Exception ex) { Debug.WriteLine($"VideoService: SubmitLayerFrame for mesh failed: {ex}"); }
 }
 }
 catch (Exception ex) { Debug.WriteLine($"VideoService: Post-frame handling failed: {ex}"); }

 try
 {
 var now = DateTime.UtcNow;
 var last = _lastFrameSent.GetOrAdd(layerKey, DateTime.MinValue);
 if (now - last >= _minFrameInterval)
 {
 _lastFrameSent[layerKey] = now;
 try { FrameDecoded?.Invoke(layer.Id ?? string.Empty, bmpToSubmit); } catch { }
 }
 }
 catch { }
 }

 public Task RegisterMeshLayerAsync(LayerModel mesh)
 {
 if (mesh == null) throw new ArgumentNullException(nameof(mesh));
 if (string.IsNullOrEmpty(mesh.Id)) mesh.Id = Guid.NewGuid().ToString("N");
 _meshLayers[mesh.Id] = mesh;

 // Set mesh dimensions to renderer output size for proper full-screen mapping
 mesh.Width = _rendererManager.OutputWidth;
 mesh.Height = _rendererManager.OutputHeight;

 try
 {
 if (!string.IsNullOrEmpty(mesh.SourceId) && _decoders.TryGetValue(mesh.SourceId, out var tup) && tup.model != null)
 {
 try { tup.model.PreviewOnly = true; } catch { }
 }
 }
 catch { }

 try
 {
 if (!string.IsNullOrEmpty(mesh.SourceId) && _decoders.TryGetValue(mesh.SourceId, out var tuple) && tuple.lastFrame != null)
 {
 var sourceFrame = tuple.lastFrame;
 BitmapSource frameForMesh = sourceFrame;
 try { /* intentionally left blank: no cropping */ } catch { frameForMesh = sourceFrame; }

 var meshDest = new Rect(mesh.X, mesh.Y, Math.Max(1, mesh.Width), Math.Max(1, mesh.Height));
 try
 {
 Point[]? destQuad = null;
 try { destQuad = _renderer_manager_safe_map(mesh.OutputMeshPoints); } catch { destQuad = null; }
 InvokeOnUi(() => { try { _rendererManager.SubmitLayerFrame(mesh.Id ?? Guid.NewGuid().ToString("N"), frameForMesh, meshDest, destQuad, mesh.Opacity); } catch { } });
 }
 catch (Exception ex) { Debug.WriteLine($"VideoService.RegisterMeshLayerAsync: SubmitLayerFrame failed: {ex}"); }
 }
 }
 catch { }

 return Task.CompletedTask;
 }

 // small helper to call MapNormalizedToRendererPoints and swallow exceptions
 private Point[]? _renderer_manager_safe_map(Vector2[]? pts)
 {
 try { return _rendererManager.MapNormalizedToRendererPoints(pts); } catch { return null; }
 }

 public Task UnregisterMeshLayerAsync(string meshId)
 {
 if (string.IsNullOrEmpty(meshId)) return Task.CompletedTask;
 if (_meshLayers.TryRemove(meshId, out var removed))
 {
 try
 {
 if (removed != null && !string.IsNullOrEmpty(removed.SourceId))
 {
 bool any = _meshLayers.Values.Any(m => m != null && m.SourceId == removed.SourceId);
 if (!any)
 {
 // No more meshes for this source, clear the output for this source
 InvokeOnUi(() => { try { _rendererManager.SubmitLayerFrame(removed.SourceId, null, new Rect(), null,0.0); } catch { } });
 }
 }
 }
 catch { }
 }
 return Task.CompletedTask;
 }

 // helper to snapshot mesh layers to avoid enumerating concurrent dictionary directly in some places
 private LayerModel[] _meshLayersSnapshot()
 {
 try { return _meshLayers.Values.ToArray(); } catch { return Array.Empty<LayerModel>(); }
 }

 private static BitmapSource RotateBitmap(BitmapSource src, double degrees)
 {
 if (src == null) return src;
 var deg = degrees %360.0;
 if (deg <0) deg +=360.0;
 if (Math.Abs(deg) <1e-6) return src;

 try
 {
 var rt = new RotateTransform(deg);
 var tb = new TransformedBitmap(src, rt);
 try { tb.Freeze(); } catch { }
 return tb;
 }
 catch
 {
 return src;
 }
 }

 public Task UnregisterLayerAsync(string layerId)
 {
 if (string.IsNullOrEmpty(layerId)) return Task.CompletedTask;
 if (_decoders.TryRemove(layerId, out var tuple))
 {
 try
 {
 try { tuple.cts?.Cancel(); } catch (Exception ex) { Debug.WriteLine($"VideoService.UnregisterLayerAsync: Error canceling token for {layerId}: {ex}"); }
 try { Task.Delay(300).Wait(); } catch { }
 try { tuple.decoder?.Dispose(); } catch (Exception ex) { Debug.WriteLine($"VideoService.UnregisterLayerAsync: Error disposing decoder for {layerId}: {ex}"); }
 try { tuple.cts?.Dispose(); } catch (Exception ex) { Debug.WriteLine($"VideoService.UnregisterLayerAsync: Error disposing cts for {layerId}: {ex}"); }
 }
 catch (Exception ex)
 {
 Debug.WriteLine($"VideoService.UnregisterLayerAsync: Error for {layerId}: {ex}");
 }
 }

 _lastFrameSent.TryRemove(layerId, out _);

 return Task.CompletedTask;
 }

 public Task PauseLayerAsync(string layerId)
 {
 if (string.IsNullOrEmpty(layerId)) return Task.CompletedTask;

 Debug.WriteLine($"VideoService.PauseLayerAsync: Attempting to pause layer {layerId}");

 if (!_decoders.TryGetValue(layerId, out var t))
 {
 Debug.WriteLine($"VideoService.PauseLayerAsync: Layer {layerId} not found in decoders");
 return Task.CompletedTask;
 }

 try
 {
 Debug.WriteLine($"VideoService.PauseLayerAsync: Pausing layer {layerId}");

 TimeSpan currentPosition = TimeSpan.Zero;
 try
 {
 if (t.decoder != null)
 {
 currentPosition = t.decoder.CurrentPosition;
 t.decoder.SaveCurrentPosition(); // This stops the timer and saves position
 Debug.WriteLine($"VideoService.PauseLayerAsync: Saved position {currentPosition.TotalSeconds:F2}s for layer {layerId}");
 }
 }
 catch (Exception ex)
 {
 Debug.WriteLine($"VideoService.PauseLayerAsync: Error saving position for {layerId}: {ex}");
 }

 try { t.cts?.Cancel(); } catch (Exception ex) { Debug.WriteLine($"VideoService.PauseLayerAsync: Error canceling token for {layerId}: {ex}"); }

 try { Task.Delay(700).Wait(); } catch { }

 try { t.decoder?.Dispose(); } catch (Exception ex) { Debug.WriteLine($"VideoService.PauseLayerAsync: Error disposing decoder for {layerId}: {ex}"); }

 _decoders[layerId] = (null, t.cts, t.model, t.lastFrame, true, currentPosition);

 Debug.WriteLine($"VideoService.PauseLayerAsync: Layer {layerId} paused successfully with position {currentPosition.TotalSeconds:F2}s");
 }
 catch (Exception ex)
 {
 Debug.WriteLine($"VideoService.PauseLayerAsync failed for {layerId}: {ex}");
 }

 return Task.CompletedTask;
 }

 public async Task ResumeLayerAsync(string layerId)
 {
 if (string.IsNullOrEmpty(layerId)) return;

 Debug.WriteLine($"VideoService.ResumeLayerAsync: Attempting to resume layer {layerId}");

 if (!_decoders.TryGetValue(layerId, out var t))
 {
 Debug.WriteLine($"VideoService.ResumeLayerAsync: Layer {layerId} not found in decoders");
 return;
 }

 if (t.model == null)
 {
 Debug.WriteLine($"VideoService.ResumeLayerAsync: Layer {layerId} has no model");
 return;
 }

 if (!t.isPaused)
 {
 Debug.WriteLine($"VideoService.ResumeLayerAsync: Layer {layerId} is not paused, skipping resume");
 return;
 }

 try
 {
 Debug.WriteLine($"VideoService.ResumeLayerAsync: Resuming layer {layerId} from position {t.savedPosition.TotalSeconds:F2}s");

 var layer = t.model;
 var playAudio = layer.PlayAudio;
 var savedPosition = t.savedPosition;

 _decoders.TryRemove(layerId, out _);

 var decoder = new FFmpegUnifiedDecoder(layer.SourcePath,
 Math.Max(1, layer.Width >0 ? layer.Width :640),
 Math.Max(1, layer.Height >0 ? layer.Height :480),
 ResolveFfmpegExecutable())
 {
 Loop = true,
 AudioEnabled = false, // enable audio after a short delay to avoid overlap
 Volume =1.0f
 };

 decoder.SetResumePosition(savedPosition);

 var cts = new CancellationTokenSource();

 decoder.FrameDecodedWithTimestamp += (bmp, pts) => { };
 decoder.FrameDecoded += bmp =>
 {
 if (bmp == null) return;
 InvokeOnUi(() =>
 {
 try
 {
 ProcessDecodedFrameOnUi(layer, layerId, decoder, cts, bmp,
 Math.Max(1, layer.Width >0 ? layer.Width :640),
 Math.Max(1, layer.Height >0 ? layer.Height :480));
 }
 catch (Exception ex)
 {
 Debug.WriteLine($"VideoService.ResumeLayerAsync: Error processing frame for {layerId}: {ex}");
 }
 });
 };

 _decoders.AddOrUpdate(layerId, (decoder, cts, layer, t.lastFrame, false, savedPosition), (k, old) => (decoder, cts, layer, old.lastFrame, false, savedPosition));

 _ = Task.Run(async () =>
 {
 try
 {
 await decoder.StartAsync(cts.Token).ConfigureAwait(false);
 Debug.WriteLine($"VideoService.ResumeLayerAsync: Decoder started successfully for layer {layerId}");
 }
 catch (OperationCanceledException)
 {
 Debug.WriteLine($"VideoService.ResumeLayerAsync: Decoder start cancelled for layer {layerId}");
 }
 catch (Exception ex)
 {
 Debug.WriteLine($"VideoService.ResumeLayerAsync: Decoder start failed for layer {layerId}: {ex}");
 await UnregisterLayerAsync(layerId).ConfigureAwait(false);
 }
 }, cts.Token);

 Debug.WriteLine($"VideoService.ResumeLayerAsync: Layer {layerId} resumed successfully");

 // Delay enabling audio briefly to allow previous decoder/audio process to fully stop and avoid overlapping audio
 _ = Task.Run(async () =>
 {
 try
 {
 await Task.Delay(300).ConfigureAwait(false);
 try
 {
 if (_decoders.TryGetValue(layerId, out var cur) && cur.decoder == decoder)
 {
 // Wait a bit longer to ensure the buffer has some data before enabling
 await Task.Delay(200).ConfigureAwait(false);
 
 decoder.AudioEnabled = playAudio || layer.PlayAudio;
 Debug.WriteLine($"VideoService.ResumeLayerAsync: Audio enabled for {layerId} (playAudio={playAudio}, layer.PlayAudio={layer.PlayAudio})");
 }
 }
 catch (Exception ex)
 {
 Debug.WriteLine($"VideoService.ResumeLayerAsync: Enabling audio failed for {layerId}: {ex}");
 }
 }
 catch (Exception ex)
 {
 Debug.WriteLine($"VideoService.ResumeLayerAsync: Audio enable task failed for {layerId}: {ex}");
 }
 });
 }
 catch (Exception ex)
 {
 Debug.WriteLine($"VideoService.ResumeLayerAsync failed for {layerId}: {ex}");
 }
 }

 public void Dispose()
 {
 // Cancel and dispose all decoders cleanly
 foreach (var kv in _decoders.ToArray())
 {
 try
 {
 var tup = kv.Value;
 try { tup.cts?.Cancel(); } catch { }
 try { tup.decoder?.Dispose(); } catch { }
 try { tup.cts?.Dispose(); } catch { }
 }
 catch { }
 }
 _decoders.Clear();
 try { _meshLayers.Clear(); } catch { }
 _lastFrameSent.Clear();
 }

 private string ResolveFfmpegExecutable()
 {
 var exe = string.IsNullOrWhiteSpace(_ffmpegPath) ? "ffmpeg" : _ffmpegPath;
 try
 {
 var psi = new ProcessStartInfo
 {
 FileName = exe,
 Arguments = "-version",
 UseShellExecute = false,
 RedirectStandardOutput = true,
 RedirectStandardError = true,
 CreateNoWindow = true
 };
 using var proc = Process.Start(psi);
 if (proc == null) throw new InvalidOperationException("Failed to start ffmpeg process.");
 if (!proc.WaitForExit(1500)) { try { proc.Kill(true); } catch { } throw new InvalidOperationException("ffmpeg did not respond in time."); }
 return exe;
 }
 catch (Exception ex)
 {
 throw new InvalidOperationException($"ffmpeg executable not found or failed to run. Ensure ffmpeg is installed and on PATH, or provide a valid ffmpeg path. Underlying error: {ex.Message}", ex);
 }
 }

 public Task PauseAllAsync() => Task.WhenAll(_decoders.Keys.Select(id => PauseLayerAsync(id)).ToArray());
 public Task ResumeAllAsync() => Task.WhenAll(_decoders.Where(kv => kv.Value.isPaused).Select(kv => ResumeLayerAsync(kv.Key)).ToArray());
 public Task RestartAllAsync() => Task.WhenAll(_decoders.Keys.Select(id => RestartLayerAsync(id)).ToArray());

 public async Task RestartLayerAsync(string layerId)
 {
 if (string.IsNullOrEmpty(layerId)) return;
 if (!_decoders.TryGetValue(layerId, out var t) || t.model == null) return;
 var model = t.model;
 
 // CRITICAL: Get the playAudio state from the model which reflects the checkbox state
 var playAudio = model.PlayAudio;
 Debug.WriteLine($"VideoService.RestartLayerAsync: Restarting layer {layerId} with playAudio={playAudio}");
 
 try { await UnregisterLayerAsync(layerId).ConfigureAwait(false); } catch { }
 try { await RegisterLayerAsync(model, playAudio).ConfigureAwait(false); } catch (Exception ex) { Debug.WriteLine($"VideoService.RestartLayerAsync: Register failed for {layerId}: {ex}"); }
 }

 public void StartAudioForLayer(string layerId)
 {
 if (string.IsNullOrEmpty(layerId)) return;
 if (_decoders.TryGetValue(layerId, out var tup) && tup.decoder != null)
 {
 try { tup.decoder.AudioEnabled = true; } catch (Exception ex) { Debug.WriteLine($"StartAudioForLayer failed: {ex}"); }
 }
 }

 public void StopAudioForLayer(string layerId)
 {
 if (string.IsNullOrEmpty(layerId)) return;
 if (_decoders.TryGetValue(layerId, out var tup) && tup.decoder != null)
 {
 try { tup.decoder.AudioEnabled = false; } catch (Exception ex) { Debug.WriteLine($"StopAudioForLayer failed: {ex}"); }
 }
 }

 public bool TryGetLastFrame(string layerId, out BitmapSource? frame)
 {
 frame = null;
 if (string.IsNullOrEmpty(layerId)) return false;
 if (_decoders.TryGetValue(layerId, out var tup) && tup.lastFrame != null)
 {
 frame = tup.lastFrame;
 return true;
 }
 return false;
 }

 public async Task HideSourceOutputAndMeshesAsync(string sourceId)
 {
 if (string.IsNullOrEmpty(sourceId)) return;
 try
 {
 foreach (var kv in _decoders.ToArray())
 {
 var (decoder, cts, model, lastFrame, isPaused, savedPosition) = kv.Value;
 if (model != null && model.Id == sourceId)
 {
 try
 {
 model.Visible = false;
 await PauseLayerAsync(model.Id ?? string.Empty).ConfigureAwait(false);
 InvokeOnUi(() => { try { _rendererManager.SubmitLayerFrame(model.Id ?? string.Empty, null, new Rect(model.X, model.Y, Math.Max(1, model.Width), Math.Max(1, model.Height)), null, model.Opacity); } catch { } });
 }
 catch { }
 }
 }

 foreach (var kv in _meshLayers.ToArray())
 {
 var mesh = kv.Value; if (mesh == null) continue;
 if (mesh.SourceId == sourceId)
 {
 try { mesh.Visible = false; InvokeOnUi(() => { try { _rendererManager.SubmitLayerFrame(mesh.Id ?? string.Empty, null, new Rect(mesh.X, mesh.Y, Math.Max(1, mesh.Width), Math.Max(1, mesh.Height)), null, mesh.Opacity); } catch { } }); } catch { }
 }
 }
 }
 catch { }
 }

 public async Task ShowSourceOutputAndMeshesAsync(string sourceId)
 {
 if (string.IsNullOrEmpty(sourceId)) return;
 try
 {
 foreach (var kv in _decoders.ToArray())
 {
 var (decoder, cts, model, lastFrame, isPaused, savedPosition) = kv.Value;
 if (model != null && model.Id == sourceId)
 {
 try { model.Visible = true; await ResumeLayerAsync(model.Id ?? string.Empty).ConfigureAwait(false); } catch { }
 }
 }

 foreach (var mesh in _meshLayersSnapshot())
 {
 if (mesh == null) continue;
 if (mesh.SourceId == sourceId)
 {
 try
 {
 mesh.Visible = true;
 if (!string.IsNullOrEmpty(mesh.SourceId) && _decoders.TryGetValue(mesh.SourceId, out var tup) && tup.lastFrame != null)
 {
 var frameForMesh = tup.lastFrame;
 var meshDest = new Rect(mesh.X, mesh.Y, Math.Max(1, mesh.Width), Math.Max(1, mesh.Height));
 InvokeOnUi(() => { try { _rendererManager.SubmitLayerFrame(mesh.Id ?? Guid.NewGuid().ToString("N"), frameForMesh, meshDest, null, mesh.Opacity); } catch { } });
 }
 }
 catch { }
 }
 }
 }
 catch { }
 }
 }
}