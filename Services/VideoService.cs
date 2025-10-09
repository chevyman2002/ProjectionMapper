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
using NAudio.Wave;
using ProjectionMapper.Models;
using ProjectionMapper.Rendering;

namespace ProjectionMapper.Services
{
    /// <summary>
    /// VideoService manages FFmpegVideoDecoder instances and optional ffmpeg->NAudio audio playback per layer.
    /// Implements frame forwarding to the renderer and provides per-layer audio controls and hooks for timestamped frames.
    /// </summary>
    public sealed class VideoService : IVideoService, IDisposable
    {
        private readonly RendererManager _rendererManager;

        // LayerId -> (decoder, cts, model, lastFrame)
        private readonly ConcurrentDictionary<string, (FFmpegVideoDecoder? decoder, CancellationTokenSource? cts, LayerModel model, BitmapSource? lastFrame)> _decoders
            = new();

        // mesh layers that reference host sources: meshId -> LayerModel
        private readonly ConcurrentDictionary<string, LayerModel> _meshLayers = new();

        // audio players per layer using ffmpeg -> NAudio pipeline
        // Tuple: (IWavePlayer player, BufferedWaveProvider provider, Process ffmpegProcess)
        private readonly ConcurrentDictionary<string, (IWavePlayer player, BufferedWaveProvider provider, Process ffmpeg)> _audioPlayersN = new();

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

        public Task<bool> RegisterLayerAsync(LayerModel layer)
        {
            return RegisterLayerAsync(layer, playAudio: false);
        }

        public async Task<bool> RegisterLayerAsync(LayerModel layer, bool playAudio = false)
        {
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            if (string.IsNullOrWhiteSpace(layer.SourcePath)) return false;
            if (!File.Exists(layer.SourcePath)) throw new FileNotFoundException("Source video not found", layer.SourcePath);

            string ffmpegExecutable = ResolveFfmpegExecutable();

            var w = Math.Max(1, layer.Width > 0 ? layer.Width : 640);
            var h = Math.Max(1, layer.Height > 0 ? layer.Height : 480);

            var decoder = new FFmpegVideoDecoder(layer.SourcePath, w, h, ffmpegExecutable)
            {
                Loop = true
            };

            var cts = new CancellationTokenSource();
            var layerKey = layer.Id ?? Guid.NewGuid().ToString("N");

            // Subscribe to timestamped frames to allow AV sync work
            decoder.FrameDecodedWithTimestamp += (bmp, pts) =>
            {
                // Currently we don't implement sample scheduling here; this event allows us to record PTS for future sync.
                // Could store _latestVideoPts[layerKey] = pts for scheduler.
            };

            // Process frames on UI thread to avoid WPF cross-thread exceptions
            decoder.FrameDecoded += bmp =>
            {
                if (bmp == null) return;
                // Dispatch full WPF processing to UI thread
                InvokeOnUi(() =>
                {
                    try
                    {
                        ProcessDecodedFrameOnUi(layer, layerKey, decoder, cts, bmp, w, h);
                    }
                    catch { }
                });
            };

            // Preserve existing last frame
            BitmapSource? existingLast = null;
            if (_decoders.TryGetValue(layerKey, out var existing)) existingLast = existing.lastFrame;
            _decoders.AddOrUpdate(layerKey, (decoder, cts, layer, existingLast), (k, old) => (decoder, cts, layer, old.lastFrame));

            // Optionally start audio playback using ffmpeg piping into NAudio
            if (playAudio)
            {
                StartAudioForLayer(layerKey, layer.SourcePath);
            }

            // Start decoder
            _ = Task.Run(async () =>
            {
                try
                {
                    await decoder.StartAsync(cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    await UnregisterLayerAsync(layerKey).ConfigureAwait(false);
                }
            }, cts.Token);

            return true;
        }

        private void ProcessDecodedFrameOnUi(LayerModel layer, string layerKey, FFmpegVideoDecoder decoder, CancellationTokenSource cts, BitmapSource bmp, int defaultW, int defaultH)
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
                if (Math.Abs(layer.RotationDegrees) > 1e-6 && sourceFrozen != null)
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
                _decoders.AddOrUpdate(layerKey,
                    (decoder, cts, layer, bmpToSubmit),
                    (k, old) => (old.decoder ?? decoder, old.cts ?? cts, old.model, bmpToSubmit));
            }
            catch { }

            try
            {
                if (layer.TargetMonitorIndex >= 0)
                {
                    var clone = bmpToSubmit;
                    try { clone.Freeze(); } catch { }
                    try { _rendererManager.SetFullScreenHostFrame(layer.TargetMonitorIndex, clone); } catch { }
                }
            }
            catch { }

            var destRect = new Rect(layer.X, layer.Y, layer.Width > 0 ? layer.Width : defaultW, layer.Height > 0 ? layer.Height : defaultH);

            if (!layer.PreviewOnly && layer.Visible)
            {
                try { _rendererManager.SubmitLayerFrame(layer.Id ?? Guid.NewGuid().ToString("N"), bmpToSubmit, destRect, layer.Opacity); } catch { }
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

                        if (srcX < 0) srcX = 0; if (srcY < 0) srcY = 0;
                        if (srcX + cropW > srcW) cropW = srcW - srcX;
                        if (srcY + cropH > srcH) cropH = srcH - srcY;

                        if (cropW > 0 && cropH > 0)
                        {
                            var cb = new CroppedBitmap(frameForMesh, new Int32Rect(srcX, srcY, cropW, cropH));
                            try { cb.Freeze(); } catch { }
                            frameForMesh = cb;
                        }
                    }
                    catch { frameForMesh = bmpToSubmit; }

                    var meshDest = new Rect(mesh.X, mesh.Y, Math.Max(1, mesh.Width), Math.Max(1, mesh.Height));
                    try { _rendererManager.SubmitLayerFrame(mesh.Id ?? Guid.NewGuid().ToString("N"), frameForMesh, meshDest, mesh.Opacity); } catch { }

                    try { if (mesh.TargetMonitorIndex >= 0) _rendererManager.SetFullScreenHostFrame(mesh.TargetMonitorIndex, frameForMesh); } catch { }
                }
            }
            catch { }

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

                        if (srcX < 0) srcX = 0; if (srcY < 0) srcY = 0;
                        if (srcX + cropW > srcW) cropW = srcW - srcX;
                        if (srcY + cropH > srcH) cropH = srcH - srcY;

                        if (cropW > 0 && cropH > 0)
                        {
                            var cb = new CroppedBitmap(frameForMesh, new Int32Rect(srcX, srcY, cropW, cropH));
                            try { cb.Freeze(); } catch { }
                            frameForMesh = cb;
                        }
                    }
                    catch { frameForMesh = sourceFrame; }

                    var meshDest = new Rect(mesh.X, mesh.Y, Math.Max(1, mesh.Width), Math.Max(1, mesh.Height));
                    try { _rendererManager.SubmitLayerFrame(mesh.Id ?? Guid.NewGuid().ToString("N"), frameForMesh, meshDest, mesh.Opacity); } catch { }
                }
            }
            catch { }

            return Task.CompletedTask;
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
                        bool any = false;
                        foreach (var kv in _meshLayers)
                        {
                            var m = kv.Value;
                            if (m != null && m.SourceId == removed.SourceId)
                            {
                                any = true; break;
                            }
                        }

                        if (!any && _decoders.TryGetValue(removed.SourceId, out var tup) && tup.model != null)
                        {
                            try { tup.model.PreviewOnly = false; } catch { }
                        }
                    }
                }
                catch { }
            }
            return Task.CompletedTask;
        }

        private static BitmapSource RotateBitmap(BitmapSource src, double degrees)
        {
            if (src == null) return src;
            var deg = degrees % 360.0;
            if (deg < 0) deg += 360.0;
            if (Math.Abs(deg) < 1e-6) return src;

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
                    tuple.cts?.Cancel();
                    tuple.decoder?.Dispose();
                    tuple.cts?.Dispose();
                }
                catch { }
            }

            StopAndDisposeAudio(layerId);
            _lastFrameSent.TryRemove(layerId, out _);

            return Task.CompletedTask;
        }

        public Task PauseLayerAsync(string layerId)
        {
            if (string.IsNullOrEmpty(layerId)) return Task.CompletedTask;
            if (_decoders.TryGetValue(layerId, out var t))
            {
                try
                {
                    t.cts?.Cancel();
                    t.decoder?.Dispose();
                    _decoders[layerId] = (null, null, t.model, t.lastFrame);
                }
                catch { }
            }

            if (_audioPlayersN.TryGetValue(layerId, out var audio))
            {
                try { audio.player.Pause(); } catch { }
            }

            return Task.CompletedTask;
        }

        public async Task ResumeLayerAsync(string layerId)
        {
            if (string.IsNullOrEmpty(layerId)) return;
            if (_decoders.TryGetValue(layerId, out var t) && t.model != null && (t.decoder == null))
            {
                var layer = t.model;
                var playAudio = layer.PlayAudio;
                await RegisterLayerAsync(layer, playAudio).ConfigureAwait(false);
                if (_audioPlayersN.TryGetValue(layerId, out var audio))
                {
                    try { audio.player.Play(); } catch { }
                }
            }
        }

        public Task PauseAllAsync() { foreach (var id in _decoders.Keys) _ = PauseLayerAsync(id); return Task.CompletedTask; }
        public Task ResumeAllAsync() { foreach (var kv in _decoders) if (kv.Value.model != null && kv.Value.decoder == null) _ = ResumeLayerAsync(kv.Key); return Task.CompletedTask; }
        public Task RestartAllAsync() { foreach (var id in _decoders.Keys) _ = RestartLayerAsync(id); return Task.CompletedTask; }

        public async Task RestartLayerAsync(string layerId)
        {
            if (string.IsNullOrEmpty(layerId)) return;
            if (_decoders.TryGetValue(layerId, out var t) && t.model != null)
            {
                await UnregisterLayerAsync(layerId).ConfigureAwait(false);
                await RegisterLayerAsync(t.model).ConfigureAwait(false);
            }
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

        private void StartAudioForLayer(string layerKey, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

                var ffmpegExe = ResolveFfmpegExecutable();
                var args = $"-hide_banner -loglevel error -stream_loop -1 -i \"{path}\" -f s16le -acodec pcm_s16le -ac 2 -ar 44100 -";

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegExe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                var ff = Process.Start(psi);
                if (ff == null) return;

                var waveFormat = new WaveFormat(44100, 16, 2);
                var provider = new BufferedWaveProvider(waveFormat) { BufferDuration = TimeSpan.FromSeconds(5), DiscardOnBufferOverflow = true };

                var output = new WaveOutEvent();
                output.Init(provider);
                output.Play();

                _audioPlayersN[layerKey] = (output, provider, ff);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var stdout = ff.StandardOutput.BaseStream;
                        var buffer = new byte[4096];
                        while (!ff.HasExited)
                        {
                            int read = await stdout.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
                            if (read > 0) provider.AddSamples(buffer, 0, read);
                            else await Task.Delay(5).ConfigureAwait(false);
                        }
                    }
                    catch { }
                });

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var err = ff.StandardError;
                        while (!ff.HasExited)
                        {
                            var line = await err.ReadLineAsync().ConfigureAwait(false);
                            if (line == null) break;
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        private void StopAndDisposeAudio(string layerKey)
        {
            if (_audioPlayersN.TryRemove(layerKey, out var tup))
            {
                try { tup.player?.Stop(); } catch { }
                try { tup.player?.Dispose(); } catch { }
                try { if (!tup.ffmpeg.HasExited) try { tup.ffmpeg.Kill(true); } catch { } } catch { }
                try { tup.ffmpeg.Dispose(); } catch { }
            }
        }

        // Stop audio playback for a given layer id
        public void StopAudioForLayer(string layerId)
        {
            if (string.IsNullOrEmpty(layerId)) return;
            StopAndDisposeAudio(layerId);
        }

        // Set per-layer volume (0.0 - 1.0). We apply this by adjusting playback device volume if supported
        public void SetLayerVolume(string layerId, float volume)
        {
            if (string.IsNullOrEmpty(layerId)) return;
            if (_audioPlayersN.TryGetValue(layerId, out var tup))
            {
                try
                {
                    // NAudio's WaveOutEvent exposes Volume property on IWavePlayer? -> cast to WaveOutEvent
                    if (tup.player is WaveOutEvent woe)
                    {
                        woe.Volume = Math.Max(0f, Math.Min(1f, volume));
                    }
                    else
                    {
                        // As fallback try to set volume on provider (not directly supported), so no-op
                    }
                }
                catch { }
            }
        }

        public void SetLayerMute(string layerId, bool muted)
        {
            if (string.IsNullOrEmpty(layerId)) return;
            if (_audioPlayersN.TryGetValue(layerId, out var tup))
            {
                try
                {
                    if (tup.player is WaveOutEvent woe)
                    {
                        woe.Volume = muted ? 0f : 1f;
                    }
                }
                catch { }
            }
        }

        public void Dispose()
        {
            foreach (var kv in _decoders)
            {
                try { kv.Value.cts?.Cancel(); kv.Value.decoder?.Dispose(); kv.Value.cts?.Dispose(); } catch { }
            }
            _decoders.Clear(); _meshLayers.Clear();

            foreach (var kv in _audioPlayersN)
            {
                try { kv.Value.player.Stop(); kv.Value.player.Dispose(); } catch { }
                try { if (!kv.Value.ffmpeg.HasExited) try { kv.Value.ffmpeg.Kill(true); } catch { } } catch { }
                try { kv.Value.ffmpeg.Dispose(); } catch { }
            }
            _audioPlayersN.Clear(); _lastFrameSent.Clear();
        }

        public async Task HideSourceOutputAndMeshesAsync(string sourceId)
        {
            if (string.IsNullOrEmpty(sourceId)) return;
            foreach (var kv in _decoders)
            {
                var (decoder, cts, model, lastFrame) = kv.Value;
                if (model != null && model.Id == sourceId)
                {
                    try { model.Visible = false; await PauseLayerAsync(model.Id ?? string.Empty).ConfigureAwait(false); InvokeOnUi(() => { try { _rendererManager.SubmitLayerFrame(model.Id ?? string.Empty, null, new Rect(model.X, model.Y, Math.Max(1, model.Width), Math.Max(1, model.Height)), model.Opacity); } catch { } }); } catch { }
                }
            }

            foreach (var kv in _meshLayers)
            {
                var mesh = kv.Value; if (mesh == null) continue;
                if (mesh.SourceId == sourceId)
                {
                    try { mesh.Visible = false; InvokeOnUi(() => { try { _rendererManager.SubmitLayerFrame(mesh.Id ?? string.Empty, null, new Rect(mesh.X, mesh.Y, Math.Max(1, mesh.Width), Math.Max(1, mesh.Height)), mesh.Opacity); } catch { } }); } catch { }
                }
            }
        }

        public async Task ShowSourceOutputAndMeshesAsync(string sourceId)
        {
            if (string.IsNullOrEmpty(sourceId)) return;
            foreach (var kv in _decoders)
            {
                var (decoder, cts, model, lastFrame) = kv.Value;
                if (model != null && model.Id == sourceId)
                {
                    try { model.Visible = true; await ResumeLayerAsync(model.Id ?? string.Empty).ConfigureAwait(false); } catch { }
                }
            }

            foreach (var kv in _mesh_layers_snapshot())
            {
                var mesh = kv; if (mesh == null) continue;
                if (mesh.SourceId == sourceId)
                {
                    try
                    {
                        mesh.Visible = true;
                        if (!string.IsNullOrEmpty(mesh.SourceId) && _decoders.TryGetValue(mesh.SourceId, out var tup) && tup.lastFrame != null)
                        {
                            var frameForMesh = tup.lastFrame;
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

                                if (srcX < 0) srcX = 0; if (srcY < 0) srcY = 0;
                                if (srcX + cropW > srcW) cropW = srcW - srcX;
                                if (srcY + cropH > srcH) cropH = srcH - srcY;

                                if (cropW > 0 && cropH > 0)
                                {
                                    var cb = new CroppedBitmap(frameForMesh, new Int32Rect(srcX, srcY, cropW, cropH));
                                    try { cb.Freeze(); } catch { }
                                    frameForMesh = cb;
                                }
                            }
                            catch { }

                            var meshDest = new Rect(mesh.X, mesh.Y, Math.Max(1, mesh.Width), Math.Max(1, mesh.Height));
                            InvokeOnUi(() => { try { _rendererManager.SubmitLayerFrame(mesh.Id ?? Guid.NewGuid().ToString("N"), frameForMesh, meshDest, mesh.Opacity); } catch { } });
                        }
                    }
                    catch { }
                }
            }
        }

        // helper to snapshot mesh layers to avoid enumerating concurrent dictionary directly in some places
        private LayerModel[] _mesh_layers_snapshot()
        {
            try { return _meshLayers.Values.ToArray(); } catch { return Array.Empty<LayerModel>(); }
        }

        public bool TryGetLastFrame(string layerId, out BitmapSource? frame)
        {
            frame = null;
            if (string.IsNullOrEmpty(layerId)) return false;
            if (_decoders.TryGetValue(layerId, out var tuple) && tuple.lastFrame != null)
            {
                frame = tuple.lastFrame;
                return true;
            }
            return false;
        }
    }
}