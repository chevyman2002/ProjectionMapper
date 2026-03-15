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

        // Loop control shared across all decoders so playlist mode can temporarily disable looping
        private readonly object _loopingLock = new();
        private volatile bool _globalLoopingEnabled = true;

        // CRITICAL: Track which layers are currently allowed to render output
        // When playlist mode is active, only layers in the active group should render
        private readonly ConcurrentDictionary<string, bool> _activeRenderLayers = new();
        private volatile bool _playlistModeActive = false;

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

        /// <summary>
        /// Event raised when a video reaches its end. Parameter is the layerId.
        /// Used by PlaylistService to track group completion.
        /// </summary>
        public event Action<string>? VideoCompleted;

        /// <summary>
        /// Enables playlist mode - only layers explicitly set as active will render.
        /// </summary>
        public void EnablePlaylistMode()
        {
            _playlistModeActive = true;
            _activeRenderLayers.Clear();
            Debug.WriteLine("VideoService: Playlist mode enabled - all layers blocked from rendering until explicitly activated");
        }

        /// <summary>
        /// Disables playlist mode - all layers can render.
        /// </summary>
        public void DisablePlaylistMode()
        {
            _playlistModeActive = false;
            _activeRenderLayers.Clear();
            Debug.WriteLine("VideoService: Playlist mode disabled - all layers can render");
        }

        /// <summary>
        /// Sets the list of layers that are allowed to render in playlist mode.
        /// </summary>
        public void SetActiveRenderLayers(System.Collections.Generic.IEnumerable<string> layerIds)
        {
            _activeRenderLayers.Clear();
            foreach (var id in layerIds)
            {
                if (!string.IsNullOrEmpty(id))
                {
                    _activeRenderLayers[id] = true;
                }
            }
            Debug.WriteLine($"VideoService: Set {_activeRenderLayers.Count} layers as active for rendering");
        }

        /// <summary>
        /// Checks if a layer is allowed to render in the current mode.
        /// </summary>
        private bool IsLayerAllowedToRender(string layerId)
        {
            if (!_playlistModeActive) return true;
            return _activeRenderLayers.ContainsKey(layerId);
        }

        private void InvokeOnUi(Action action)
        {
            try
            {
                var app = Application.Current;
                if (app == null || app.Dispatcher == null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
                {
                    // Do not run UI action inline on a background thread - this can cause cross-thread exceptions.
                    Debug.WriteLine("InvokeOnUi: UI dispatcher unavailable or shutting down - skipping UI action to avoid cross-thread access.");
                    return;
                }

                app.Dispatcher.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"InvokeOnUi: action threw: {ex}");
                    }
                }));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"InvokeOnUi failed: {ex}");
            }
        }

        public Task<bool> RegisterLayerAsync(LayerModel layer) => RegisterLayerAsync(layer, playAudio: false);

        public async Task<bool> RegisterLayerAsync(LayerModel layer, bool playAudio = false)
        {
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            if (string.IsNullOrWhiteSpace(layer.SourcePath)) return false;
            if (!File.Exists(layer.SourcePath)) throw new FileNotFoundException("Source video not found", layer.SourcePath);

            string ffmpegExecutable = ResolveFfmpegExecutable();

            var w = Math.Max(1, layer.Width > 0 ? layer.Width : 640);
            var h = Math.Max(1, layer.Height > 0 ? layer.Height : 480);

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

                    // Update audio settings on existing decoder with better coordination
                    var shouldEnableAudio = playAudio || layer.PlayAudio;
                    if (shouldEnableAudio != existing.decoder.AudioEnabled)
                    {
                        // If we're changing audio state, do it with proper timing
                        if (shouldEnableAudio)
                        {
                            // Enable audio with delay to ensure decoder is ready
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await Task.Delay(500).ConfigureAwait(false); // Give decoder time to stabilize
                                    existing.decoder.AudioEnabled = true;
                                    existing.decoder.Volume = 1.0f;
                                    Debug.WriteLine($"VideoService.RegisterLayerAsync: Audio enabled for existing decoder {layerKey}");
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"VideoService.RegisterLayerAsync: Failed to enable audio for {layerKey}: {ex}");
                                }
                            });
                        }
                        else
                        {
                            existing.decoder.AudioEnabled = false;
                            Debug.WriteLine($"VideoService.RegisterLayerAsync: Audio disabled for existing decoder {layerKey}");
                        }
                    }
                    existing.decoder.Volume = 1.0f; // Ensure volume is correct
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
                AudioEnabled = false, // Start with audio disabled, enable after decoder is ready
                Volume = 1.0f // Fixed volume at 100%
            };

            var cts = new CancellationTokenSource();

            // Subscribe to timestamped frames to allow AV sync work
            decoder.FrameDecodedWithTimestamp += (bmp, pts) => { };

            // Process frames on UI thread to avoid WPF cross-thread exceptions
            decoder.FrameDecoded += bmp =>
            {
                if (bmp == null) return;
                // Early rejection: skip frames from cancelled decoder sessions
                try { if (cts.IsCancellationRequested) return; }
                catch (ObjectDisposedException) { return; }

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

            // Subscribe to video end event for playlist support
            decoder.VideoEnded += () =>
            {
                try
                {
                    Debug.WriteLine($"VideoService: Video ended for layer {layerKey}");
                    OnVideoCompleted(layerKey);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"VideoService: Error handling video end for {layerKey}: {ex}");
                }
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
                    
                    // Enable audio after decoder is started and stable, if requested
                    var shouldEnableAudio = playAudio || layer.PlayAudio;
                    if (shouldEnableAudio)
                    {
                        try
                        {
                            // Wait for decoder to be fully initialized and producing frames
                            await Task.Delay(1000).ConfigureAwait(false);
                            
                            // Verify decoder is still active before enabling audio
                            if (_decoders.TryGetValue(layerKey, out var currentTuple) && 
                                currentTuple.decoder == decoder && 
                                !currentTuple.isPaused)
                            {
                                decoder.AudioEnabled = true;
                                Debug.WriteLine($"VideoService.RegisterLayerAsync: Audio enabled after decoder startup for {layerKey}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"VideoService.RegisterLayerAsync: Failed to enable audio after startup for {layerKey}: {ex}");
                        }
                    }
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

            // CRITICAL: Check if decoder is disposed or paused before processing frame
            // This prevents race conditions where frames from a paused/disposed decoder
            // are still being rendered
            if (decoder.IsDisposed)
            {
                return;
            }

            // Check if this layer is currently paused - if so, don't process new frames
            if (_decoders.TryGetValue(layerKey, out var currentTuple) && currentTuple.isPaused)
            {
                return;
            }

            // CRITICAL: In playlist mode, only render frames for active layers
            // This prevents all videos from rendering when only one group should be playing
            if (!IsLayerAllowedToRender(layerKey))
            {
                // Still cache the frame for preview purposes, but don't render to output
                try
                {
                    BitmapSource cachedFrame = bmp;
                    if (!cachedFrame.IsFrozen)
                    {
                        var clone = cachedFrame.Clone();
                        clone.Freeze();
                        cachedFrame = clone;
                    }

                    if (_decoders.TryGetValue(layerKey, out var existing))
                    {
                        _decoders[layerKey] = (existing.decoder, existing.cts, existing.model, cachedFrame, existing.isPaused, existing.savedPosition);
                    }
                }
                catch { }
                return;
            }

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

            // Rotation disabled: submit decoded frames without applying any rotation.

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

            var destRect = new Rect(layer.X, layer.Y, layer.Width > 0 ? layer.Width : defaultW, layer.Height > 0 ? layer.Height : defaultH);

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
                    // NOTE: Do NOT skip rendering based on Visible property - video content should always render
                    // The Visible property only controls overlay (bounding box/handles) visibility in the UI

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
                    try
                    {
                        Point[]? destQuad = null;
                        try
                        {
                            destQuad = _rendererManager.MapNormalizedToRendererPoints(mesh.OutputMeshPoints, mesh.TargetMonitorIndex >= 0 ? mesh.TargetMonitorIndex : null);
                        }
                        catch (Exception exMap)
                        {
                            Debug.WriteLine($"VideoService: MapNormalizedToRendererPoints threw: {exMap}");
                            destQuad = null;
                        }

                        if (destQuad == null)
                        {
                            var pts = mesh.OutputMeshPoints;
                            if (pts != null && pts.Length >= 4)
                            {
                                // Fallback: Use target monitor size if available, otherwise use mesh bounds
                                int targetW = mesh.Width;
                                int targetH = mesh.Height;
                                int targetX = mesh.X;
                                int targetY = mesh.Y;
                                
                                // If targeting a specific monitor, try to use its dimensions
                                if (mesh.TargetMonitorIndex >= 0)
                                {
                                    var monSize = _rendererManager.GetMonitorRendererSize(mesh.TargetMonitorIndex);
                                    if (monSize.Width > 0 && monSize.Height > 0)
                                    {
                                        // OutputMeshPoints are normalized (0-1) for the full monitor
                                        targetW = monSize.Width;
                                        targetH = monSize.Height;
                                        targetX = 0;
                                        targetY = 0;
                                    }
                                }
                                
                                destQuad = new Point[4]
                                {
                                    new Point(targetX + pts[0].X * targetW, targetY + pts[0].Y * targetH),
                                    new Point(targetX + pts[1].X * targetW, targetY + pts[1].Y * targetH),
                                    new Point(targetX + pts[2].X * targetW, targetY + pts[2].Y * targetH),
                                    new Point(targetX + pts[3].X * targetW, targetY + pts[3].Y * targetH)
                                };
                            }
                            else
                            {
                                Debug.WriteLine("VideoService: OutputMeshPoints not available for mesh, destQuad remains null");
                            }
                        }

                        // CRITICAL FIX: Use SubmitLayerFrameForMonitor to send frames to BOTH main preview AND target monitor
                        var targetMonitor = mesh.TargetMonitorIndex;
                        InvokeOnUi(() => { 
                            try { 
                                _rendererManager.SubmitLayerFrameForMonitor(mesh.Id ?? Guid.NewGuid().ToString("N"), frameForMesh, meshDest, destQuad, mesh.Opacity, targetMonitor); 
                            } catch { } 
                        });
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
                    
                    // CRITICAL FIX: Clear the source layer from the renderer to prevent ghost frames
                    // When a mesh layer is created from a source, the source should no longer be rendered directly
                    try 
                    { 
                        _rendererManager.RemoveLayer(tup.model.Id);
                        Debug.WriteLine($"VideoService.RegisterMeshLayerAsync: Removed source layer {tup.model.Id} from renderer (now using mesh {mesh.Id})");
                    } 
                    catch (Exception ex) 
                    { 
                        Debug.WriteLine($"VideoService.RegisterMeshLayerAsync: Failed to remove source layer: {ex}"); 
                    }
                }
            }
            catch { }

            try
            {
                if (!string.IsNullOrEmpty(mesh.SourceId) && _decoders.TryGetValue(mesh.SourceId, out var tuple) && tuple.lastFrame != null)
                {
                    var sourceFrame = tuple.lastFrame;
                    BitmapSource frameForMesh = sourceFrame;

                    var meshDest = new Rect(mesh.X, mesh.Y, Math.Max(1, mesh.Width), Math.Max(1, mesh.Height));
                    try
                    {
                        Point[]? destQuad = null;
                        try { destQuad = _rendererManager.MapNormalizedToRendererPoints(mesh.OutputMeshPoints, mesh.TargetMonitorIndex >= 0 ? mesh.TargetMonitorIndex : null); } catch { destQuad = null; }
                        
                        // CRITICAL FIX: Use SubmitLayerFrameForMonitor to send initial frame to BOTH main preview AND target monitor
                        var targetMonitor = mesh.TargetMonitorIndex;
                        InvokeOnUi(() => { 
                            try { 
                                _rendererManager.SubmitLayerFrameForMonitor(mesh.Id ?? Guid.NewGuid().ToString("N"), frameForMesh, meshDest, destQuad, mesh.Opacity, targetMonitor); 
                            } catch { } 
                        });
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
                    // CRITICAL FIX: Remove the mesh layer from the renderer to prevent ghost frames
                    try 
                    { 
                        _rendererManager.RemoveLayer(meshId);
                        Debug.WriteLine($"VideoService.UnregisterMeshLayerAsync: Removed mesh layer {meshId} from renderer");
                    } 
                    catch (Exception ex) 
                    { 
                        Debug.WriteLine($"VideoService.UnregisterMeshLayerAsync: Failed to remove mesh layer: {ex}"); 
                    }
                    
                    if (removed != null && !string.IsNullOrEmpty(removed.SourceId))
                    {
                        bool any = _meshLayers.Values.Any(m => m != null && m.SourceId == removed.SourceId);
                        if (!any)
                        {
                            // No more meshes for this source, reset PreviewOnly so source can render directly again
                            if (_decoders.TryGetValue(removed.SourceId, out var tup) && tup.model != null)
                            {
                                try { tup.model.PreviewOnly = false; } catch { }
                            }
                        }
                    }
                }
                catch { }
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Forces a refresh of rendering for a specific mesh layer using the cached last frame from its source.
        /// This is useful when mesh points are edited while video is paused.
        /// </summary>
        public void RefreshMeshLayerRendering(string meshLayerId)
        {
            if (string.IsNullOrEmpty(meshLayerId)) return;

            try
            {
                // Find the mesh layer
                if (!_meshLayers.TryGetValue(meshLayerId, out var mesh) || mesh == null)
                {
                    Debug.WriteLine($"VideoService.RefreshMeshLayerRendering: Mesh layer {meshLayerId} not found");
                    return;
                }

                if (string.IsNullOrEmpty(mesh.SourceId))
                {
                    Debug.WriteLine($"VideoService.RefreshMeshLayerRendering: Mesh layer {meshLayerId} has no source");
                    return;
                }

                // NOTE: Do NOT skip rendering based on Visible property - video content should always render
                // The Visible property only controls overlay (bounding box/handles) visibility in the UI

                // Get the source decoder's last frame
                if (!_decoders.TryGetValue(mesh.SourceId, out var tuple) || tuple.lastFrame == null)
                {
                    Debug.WriteLine($"VideoService.RefreshMeshLayerRendering: No cached frame for source {mesh.SourceId}");
                    return;
                }

                var lastFrame = tuple.lastFrame;
                Debug.WriteLine($"VideoService.RefreshMeshLayerRendering: Refreshing mesh {meshLayerId} with cached frame from {mesh.SourceId}");

                // Re-process the cached frame through mesh rendering
                BitmapSource frameForMesh = lastFrame;
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
                catch { frameForMesh = lastFrame; }

                var meshDest = new Rect(mesh.X, mesh.Y, Math.Max(1, mesh.Width), Math.Max(1, mesh.Height));
                try
                {
                    Point[]? destQuad = null;
                    try
                    {
                        destQuad = _rendererManager.MapNormalizedToRendererPoints(mesh.OutputMeshPoints, mesh.TargetMonitorIndex >= 0 ? mesh.TargetMonitorIndex : null);
                    }
                    catch { destQuad = null; }

                    if (destQuad == null)
                    {
                        var pts = mesh.OutputMeshPoints;
                        if (pts != null && pts.Length >= 4)
                        {
                            // Fallback: Use target monitor size if available, otherwise use mesh bounds
                            int targetW = mesh.Width;
                            int targetH = mesh.Height;
                            int targetX = mesh.X;
                            int targetY = mesh.Y;
                            
                            // If targeting a specific monitor, try to use its dimensions
                            if (mesh.TargetMonitorIndex >= 0)
                            {
                                var monSize = _rendererManager.GetMonitorRendererSize(mesh.TargetMonitorIndex);
                                if (monSize.Width > 0 && monSize.Height > 0)
                                {
                                    // OutputMeshPoints are normalized (0-1) for the full monitor
                                    targetW = monSize.Width;
                                    targetH = monSize.Height;
                                    targetX = 0;
                                    targetY = 0;
                                }
                            }
                            
                            destQuad = new Point[4]
                            {
                                new Point(targetX + pts[0].X * targetW, targetY + pts[0].Y * targetH),
                                new Point(targetX + pts[1].X * targetW, targetY + pts[1].Y * targetH),
                                new Point(targetX + pts[2].X * targetW, targetY + pts[2].Y * targetH),
                                new Point(targetX + pts[3].X * targetW, targetY + pts[3].Y * targetH)
                            };
                        }
                    }

                    var targetMonitor = mesh.TargetMonitorIndex;
                    InvokeOnUi(() => { 
                        try { 
                            _rendererManager.SubmitLayerFrameForMonitor(mesh.Id ?? Guid.NewGuid().ToString("N"), frameForMesh, meshDest, destQuad, mesh.Opacity, targetMonitor); 
                        } catch { } 
                    });
                    
                    Debug.WriteLine($"VideoService.RefreshMeshLayerRendering: Successfully refreshed mesh {meshLayerId}");
                }
                catch (Exception ex) { Debug.WriteLine($"VideoService.RefreshMeshLayerRendering: SubmitLayerFrame for mesh failed: {ex}"); }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.RefreshMeshLayerRendering: Failed for {meshLayerId}: {ex}");
            }
        }

        /// <summary>
        /// Forces a refresh of rendering for all mesh layers of a given source.
        /// </summary>
        public void RefreshAllMeshLayersForSource(string sourceId)
        {
            if (string.IsNullOrEmpty(sourceId)) return;

            try
            {
                foreach (var kv in _meshLayers.ToArray())
                {
                    var mesh = kv.Value;
                    if (mesh != null && mesh.SourceId == sourceId)
                    {
                        RefreshMeshLayerRendering(mesh.Id ?? kv.Key);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.RefreshAllMeshLayersForSource: Failed: {ex}");
            }
        }

        // helper to snapshot mesh layers to avoid enumerating concurrent dictionary directly in some places
        private LayerModel[] _meshLayersSnapshot()
        {
            try { return _meshLayers.Values.ToArray(); } catch { return Array.Empty<LayerModel>(); }
        }

        // Rotation removed: no helper to rotate bitmaps exists anymore.

        public async Task UnregisterLayerAsync(string layerId)
        {
            if (string.IsNullOrEmpty(layerId)) return;
            if (_decoders.TryRemove(layerId, out var tuple))
            {
                try
                {
                    try { tuple.cts?.Cancel(); } catch (Exception ex) { Debug.WriteLine($"VideoService.UnregisterLayerAsync: Error canceling token for {layerId}: {ex}"); }
                    try { await Task.Delay(300).ConfigureAwait(false); } catch { }
                    try { tuple.decoder?.Dispose(); } catch (Exception ex) { Debug.WriteLine($"VideoService.UnregisterLayerAsync: Error disposing decoder for {layerId}: {ex}"); }
                    try { tuple.cts?.Dispose(); } catch (Exception ex) { Debug.WriteLine($"VideoService.UnregisterLayerAsync: Error disposing cts for {layerId}: {ex}"); }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"VideoService.UnregisterLayerAsync: Error for {layerId}: {ex}");
                }
            }

            _lastFrameSent.TryRemove(layerId, out _);

            return;
        }

        public async Task PauseLayerAsync(string layerId)
        {
            if (string.IsNullOrEmpty(layerId)) return;

            Debug.WriteLine($"VideoService.PauseLayerAsync: Attempting to pause layer {layerId}");

            if (!_decoders.TryGetValue(layerId, out var t))
            {
                Debug.WriteLine($"VideoService.PauseLayerAsync: Layer {layerId} not found in decoders");
                return;
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

                // OPTIMIZATION: Reduced delay from 700ms to 100ms for faster transitions
                // The decoder disposal can happen in background without blocking
                try { await Task.Delay(100).ConfigureAwait(false); } catch { }

                try { t.decoder?.Dispose(); } catch (Exception ex) { Debug.WriteLine($"VideoService.PauseLayerAsync: Error disposing decoder for {layerId}: {ex}"); }

                _decoders[layerId] = (null, t.cts, t.model, t.lastFrame, true, currentPosition);

                Debug.WriteLine($"VideoService.PauseLayerAsync: Layer {layerId} paused successfully with position {currentPosition.TotalSeconds:F2}s");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.PauseLayerAsync failed for {layerId}: {ex}");
            }

            return;
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
                    Math.Max(1, layer.Width > 0 ? layer.Width : 640),
                    Math.Max(1, layer.Height > 0 ? layer.Height : 480),
                    ResolveFfmpegExecutable())
                {
                    Loop = true,
                    AudioEnabled = false, // Start with audio disabled, enable after stabilization
                    Volume = 1.0f
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
                                Math.Max(1, layer.Width > 0 ? layer.Width : 640),
                                Math.Max(1, layer.Height > 0 ? layer.Height : 480));
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"VideoService.ResumeLayerAsync: Error processing frame for {layerId}: {ex}");
                        }
                    });
                };

                // Subscribe to video end event for playlist support
                decoder.VideoEnded += () =>
                {
                    try
                    {
                        Debug.WriteLine($"VideoService.ResumeLayerAsync: Video ended for layer {layerId}");
                        OnVideoCompleted(layerId);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"VideoService.ResumeLayerAsync: Error handling video end for {layerId}: {ex}");
                    }
                };

                _decoders.AddOrUpdate(layerId, (decoder, cts, layer, t.lastFrame, false, savedPosition), (k, old) => (decoder, cts, layer, old.lastFrame, false, savedPosition));

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await decoder.StartAsync(cts.Token).ConfigureAwait(false);
                        Debug.WriteLine($"VideoService.ResumeLayerAsync: Decoder started successfully for layer {layerId}");
                        
                        // Improved audio enablement timing
                        if (playAudio || layer.PlayAudio)
                        {
                            try
                            {
                                // Wait longer for decoder to fully stabilize before enabling audio
                                await Task.Delay(1500).ConfigureAwait(false);
                                
                                // Verify decoder is still active and hasn't been replaced
                                if (_decoders.TryGetValue(layerId, out var cur) && cur.decoder == decoder && !cur.isPaused)
                                {
                                    // Double-check audio buffer is ready before enabling
                                    decoder.AudioEnabled = true;
                                    Debug.WriteLine($"VideoService.ResumeLayerAsync: Audio enabled for {layerId} (playAudio={playAudio}, layer.PlayAudio={layer.PlayAudio})");
                                }
                                else
                                {
                                    Debug.WriteLine($"VideoService.ResumeLayerAsync: Decoder state changed, skipping audio enable for {layerId}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"VideoService.ResumeLayerAsync: Enabling audio failed for {layerId}: {ex}");
                            }
                        }
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
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.ResumeLayerAsync failed for {layerId}: {ex}");
            }
        }

        /// <summary>
        /// Fires the VideoCompleted event for the specified layer.
        /// Called internally when a video reaches its end.
        /// </summary>
        /// <param name="layerId">The layer ID that completed.</param>
        internal void OnVideoCompleted(string layerId)
        {
            try
            {
                if (string.IsNullOrEmpty(layerId)) return;
                Debug.WriteLine($"VideoService.OnVideoCompleted: Video '{layerId}' completed");
                VideoCompleted?.Invoke(layerId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.OnVideoCompleted: Error invoking event: {ex}");
            }
        }

        /// <summary>
        /// Checks if a layer should play audio based on its model settings.
        /// </summary>
        /// <param name="layerId">The layer ID to check.</param>
        /// <returns>True if the layer should play audio.</returns>
        public bool ShouldPlayAudio(string layerId)
        {
            try
            {
                if (string.IsNullOrEmpty(layerId)) return false;
                if (_decoders.TryGetValue(layerId, out var tup) && tup.model != null)
                {
                    return tup.model.PlayAudio;
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.ShouldPlayAudio: Error checking audio for {layerId}: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Starts audio playback for a specific layer.
        /// </summary>
        /// <param name="layerId">The layer ID to start audio for.</param>
        public void StartAudioForLayer(string layerId)
        {
            try
            {
                if (string.IsNullOrEmpty(layerId)) return;
                if (_decoders.TryGetValue(layerId, out var tup) && tup.decoder != null && !tup.decoder.IsDisposed)
                {
                    tup.decoder.AudioEnabled = true;
                    Debug.WriteLine($"VideoService: Audio enabled for {layerId}");
                }
                else
                {
                    Debug.WriteLine($"VideoService.StartAudioForLayer: Decoder not ready or disposed for {layerId}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.StartAudioForLayer failed for {layerId}: {ex}");
            }
        }

        /// <summary>
        /// Stops audio playback for a specific layer.
        /// </summary>
        /// <param name="layerId">The layer ID to stop audio for.</param>
        public void StopAudioForLayer(string layerId)
        {
        try
            {
                if (string.IsNullOrEmpty(layerId)) return;
                if (_decoders.TryGetValue(layerId, out var tup) && tup.decoder != null && !tup.decoder.IsDisposed)
                {
                    tup.decoder.AudioEnabled = false;
                    Debug.WriteLine($"VideoService: Audio disabled for {layerId}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.StopAudioForLayer failed for {layerId}: {ex}");
            }
        }

        /// <summary>
        /// Stops audio for all decoders.
        /// </summary>
        public void StopAllAudio()
        {
            try
            {
                foreach (var kv in _decoders.ToArray())
                {
                    try 
                    { 
                        if (kv.Value.decoder != null && !kv.Value.decoder.IsDisposed) 
                            kv.Value.decoder.AudioEnabled = false; 
                    } 
                    catch { }
                }
                Debug.WriteLine("VideoService.StopAllAudio: Stopped audio for all decoders");
            }
            catch (Exception ex) { Debug.WriteLine($"StopAllAudio failed: {ex}"); }
        }

        /// <summary>
        /// Disables looping for all decoders.
        /// </summary>
        public void DisableLoopingForAll()
        {
            try
            {
                lock (_loopingLock) { _globalLoopingEnabled = false; }
                foreach (var kv in _decoders.ToArray())
                {
                    try 
                    { 
                        if (kv.Value.decoder != null && !kv.Value.decoder.IsDisposed) 
                            kv.Value.decoder.Loop = false; 
                    } 
                    catch { }
                }
                Debug.WriteLine("VideoService.DisableLoopingForAll: Loop disabled for all existing decoders");
            }
            catch (Exception ex) { Debug.WriteLine($"DisableLoopingForAll failed: {ex}"); }
        }

        /// <summary>
        /// Enables looping for all decoders.
        /// </summary>
        public void EnableLoopingForAll()
        {
            try
            {
                lock (_loopingLock) { _globalLoopingEnabled = true; }
                foreach (var kv in _decoders.ToArray())
                {
                    try 
                    { 
                        if (kv.Value.decoder != null && !kv.Value.decoder.IsDisposed) 
                            kv.Value.decoder.Loop = true; 
                    } 
                    catch { }
                }
                Debug.WriteLine("VideoService.EnableLoopingForAll: Loop enabled for all existing decoders");
            }
            catch (Exception ex) { Debug.WriteLine($"EnableLoopingForAll failed: {ex}"); }
        }

        /// <summary>
        /// Pauses all decoders.
        /// </summary>
        public async Task PauseAllAsync()
        {
            try
            {
                var keys = _decoders.Keys.ToArray();
                var tasks = keys.Select(id => PauseLayerAsync(id));
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.PauseAllAsync failed: {ex}");
            }
        }

        /// <summary>
        /// Resumes all decoders.
        /// </summary>
        public async Task ResumeAllAsync()
        {
            try
            {
                var keys = _decoders.Keys.ToArray();
                var tasks = keys.Select(id => ResumeLayerAsync(id));
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.ResumeAllAsync failed: {ex}");
            }
        }

        /// <summary>
        /// Stops all decoders.
        /// </summary>
        public async Task StopAllAsync()
        {
            try
            {
                var keys = _decoders.Keys.ToArray();
                var tasks = keys.Select(id => UnregisterLayerAsync(id));
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.StopAllAsync failed: {ex}");
            }
        }

        /// <summary>
        /// Restarts all decoders.
        /// </summary>
        public async Task RestartAllAsync()
        {
            try
            {
                // Snapshot models to re-register after stop
                var models = _decoders.Values.Select(t => t.model).Where(m => m != null).ToArray();
                await StopAllAsync().ConfigureAwait(false);
                await Task.Delay(100).ConfigureAwait(false);
                var tasks = models.Select(m => RegisterLayerAsync(m));
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.RestartAllAsync failed: {ex}");
            }
        }

        /// <summary>
        /// Starts playback for a group of videos using fast resume for seamless transitions.
        /// </summary>
        /// <param name="layerIds">List of layer IDs to start.</param>
        public async Task StartGroupVideosAsync(System.Collections.Generic.List<string> layerIds)
        {
            try
            {
                if (layerIds == null || layerIds.Count == 0) return;
                
                // Use FastResumeLayerAsync for faster transitions
                var tasks = layerIds.Where(id => !string.IsNullOrEmpty(id)).Select(id => FastResumeLayerAsync(id));
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.StartGroupVideosAsync failed: {ex}");
            }
        }

        /// <summary>
        /// Stops playback for a group of videos.
        /// </summary>
        /// <param name="layerIds">List of layer IDs in the group.</param>
        public async Task StopGroupVideosAsync(System.Collections.Generic.List<string> layerIds)
        {
            try
            {
                if (layerIds == null || layerIds.Count == 0) return;

                Debug.WriteLine($"VideoService.StopGroupVideosAsync: Stopping {layerIds.Count} videos");

                var stopTasks = new System.Collections.Generic.List<Task>();
                foreach (var layerId in layerIds)
                {
                    if (!string.IsNullOrEmpty(layerId))
                    {
                        stopTasks.Add(PauseLayerAsync(layerId));
                    }
                }

                if (stopTasks.Count > 0)
                {
                    await Task.WhenAll(stopTasks).ConfigureAwait(false);
                }

                Debug.WriteLine("VideoService.StopGroupVideosAsync: Group videos stopped");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.StopGroupVideosAsync: Error stopping group: {ex}");
            }
        }

        /// <summary>
        /// Hides all videos except those in the specified group.
        /// Uses soft pause for faster transitions.
        /// </summary>
        /// <param name="layerIds">List of layer IDs to keep visible.</param>
        public async Task HideAllExceptGroupAsync(System.Collections.Generic.List<string> layerIds)
        {
            if (layerIds == null)
            {
                layerIds = new System.Collections.Generic.List<string>();
            }

            try
            {
                Debug.WriteLine($"VideoService.HideAllExceptGroupAsync: Keeping {layerIds.Count} videos visible");

                var hashSet = new System.Collections.Generic.HashSet<string>(layerIds);
                var hideTasks = new System.Collections.Generic.List<Task>();

                foreach (var kv in _decoders.ToArray())
                {
                    var layerId = kv.Key;
                    var tup = kv.Value;

                    if (hashSet.Contains(layerId))
                    {
                        // This layer should be visible
                        if (tup.model != null)
                        {
                            tup.model.Visible = true;
                        }
                    }
                    else
                    {
                        // This layer should be hidden (soft pause for speed)
                        if (tup.model != null)
                        {
                            tup.model.Visible = false;
                        }
                        // Use SoftPauseLayerAsync for faster transitions - it doesn't wait for decoder disposal
                        hideTasks.Add(SoftPauseLayerAsync(layerId));
                        hideTasks.Add(HideSourceOutputAndMeshesAsync(layerId));
                    }
                }

                if (hideTasks.Count > 0)
                {
                    await Task.WhenAll(hideTasks).ConfigureAwait(false);
                }

                Debug.WriteLine("VideoService.HideAllExceptGroupAsync: Non-group videos hidden");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.HideAllExceptGroupAsync: Error hiding videos: {ex}");
            }
        }

        /// <summary>
        /// Soft pauses a layer without waiting for decoder disposal.
        /// This is much faster than PauseLayerAsync and is used for quick group transitions.
        /// </summary>
        /// <param name="layerId">The layer ID to soft pause.</param>
        public async Task SoftPauseLayerAsync(string layerId)
        {
            if (string.IsNullOrEmpty(layerId)) return;

            if (!_decoders.TryGetValue(layerId, out var t))
            {
                return;
            }

            // Guard: skip if already paused to prevent double-dispose race
            if (t.isPaused)
            {
                Debug.WriteLine($"VideoService.SoftPauseLayerAsync: Layer {layerId} already paused, skipping");
                return;
            }

            try
            {
                TimeSpan currentPosition = TimeSpan.Zero;
                try
                {
                    if (t.decoder != null)
                    {
                        currentPosition = t.decoder.CurrentPosition;
                        t.decoder.SaveCurrentPosition();
                    }
                }
                catch { }

                // Cancel the decoder task first so ffmpeg ReadAsync exits before we dispose
                try { t.cts?.Cancel(); } catch { }

                // Capture the decoder reference for disposal
                var decoderToDispose = t.decoder;

                // Mark as paused and null out the decoder reference immediately
                // to prevent ProcessDecodedFrameOnUi from matching stale frames
                _decoders[layerId] = (null, t.cts, t.model, t.lastFrame, true, currentPosition);

                // Dispose decoder in background with enough time for pending I/O to observe cancellation
                if (decoderToDispose != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Allow time for ReadAsync to observe CTS cancellation and exit cleanly
                            // before killing the process. This prevents access violation on pipe handle.
                            await Task.Delay(200).ConfigureAwait(false);
                            decoderToDispose.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"VideoService.SoftPauseLayerAsync: Background dispose failed for {layerId}: {ex}");
                        }
                    });
                }

                await Task.CompletedTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.SoftPauseLayerAsync failed for {layerId}: {ex}");
            }
        }

        /// <summary>
        /// Hides the source output and associated mesh layers by clearing them from the renderer.
        /// Uses InvokeOnUi to ensure renderer operations happen on the UI thread.
        /// </summary>
        internal async Task HideSourceOutputAndMeshesAsync(string layerId)
        {
            try
            {
                if (string.IsNullOrEmpty(layerId)) return;

                // Collect all layer IDs that need to be cleared
                var layersToClear = new System.Collections.Generic.List<(string id, int targetMonitor)>
                {
                    (layerId, -1)
                };

                foreach (var kv in _meshLayers.ToArray())
                {
                    var mesh = kv.Value;
                    if (mesh != null && mesh.SourceId == layerId && !string.IsNullOrEmpty(mesh.Id))
                    {
                        layersToClear.Add((mesh.Id, mesh.TargetMonitorIndex));
                    }
                }

                // Marshal renderer operations to UI thread to prevent cross-thread access violations
                InvokeOnUi(() =>
                {
                    foreach (var (id, targetMonitor) in layersToClear)
                    {
                        try
                        {
                            _rendererManager.SubmitLayerFrameForMonitor(id, null, new Rect(), null, 0.0, targetMonitor);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"VideoService.HideSourceOutputAndMeshesAsync: Failed to clear layer {id}: {ex}");
                        }
                    }
                });

                await Task.CompletedTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.HideSourceOutputAndMeshesAsync: Error hiding {layerId}: {ex}");
            }
        }

        /// <summary>
        /// Checks if a layer has a registered decoder.
        /// </summary>
        /// <param name="layerId">The layer ID to check.</param>
        /// <returns>True if the layer has a decoder, false otherwise.</returns>
        public bool HasDecoder(string layerId)
        {
            if (string.IsNullOrEmpty(layerId)) return false;
            return _decoders.ContainsKey(layerId);
        }

        /// <summary>
        /// Waits until all specified layers have their decoders initialized and producing frames.
        /// This prevents race conditions when starting playlist playback immediately after project load.
        /// </summary>
        /// <param name="layerIds">The layer IDs to wait for.</param>
        /// <param name="timeoutMs">Maximum time to wait in milliseconds (default: 10 seconds).</param>
        /// <returns>True if all decoders are ready, false if timeout occurred.</returns>
        public async Task<bool> WaitForDecodersReadyAsync(System.Collections.Generic.IEnumerable<string> layerIds, int timeoutMs = 10000)
        {
            if (layerIds == null) return true;

            var idsToWait = layerIds.Where(id => !string.IsNullOrEmpty(id)).ToList();
            if (idsToWait.Count == 0) return true;

            Debug.WriteLine($"VideoService.WaitForDecodersReadyAsync: Waiting for {idsToWait.Count} decoders to be ready (timeout: {timeoutMs}ms)");

            var sw = Stopwatch.StartNew();
            var pollInterval = 100; // Check every 100ms

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var allReady = true;
                var readyCount = 0;

                foreach (var layerId in idsToWait)
                {
                    if (_decoders.TryGetValue(layerId, out var tuple))
                    {
                        // A decoder is considered ready ONLY if it has produced at least one frame
                        // This ensures the decoder is fully initialized and can produce output
                        if (tuple.lastFrame != null)
                        {
                            readyCount++;
                        }
                        else
                        {
                            allReady = false;
                        }
                    }
                    else
                    {
                        // Decoder not even registered yet
                        allReady = false;
                    }
                }

                if (allReady)
                {
                    Debug.WriteLine($"VideoService.WaitForDecodersReadyAsync: All {idsToWait.Count} decoders ready after {sw.ElapsedMilliseconds}ms");
                    return true;
                }

                // Log progress periodically
                if (sw.ElapsedMilliseconds > 0 && sw.ElapsedMilliseconds % 1000 < pollInterval)
                {
                    Debug.WriteLine($"VideoService.WaitForDecodersReadyAsync: {readyCount}/{idsToWait.Count} decoders ready after {sw.ElapsedMilliseconds}ms");
                }

                await Task.Delay(pollInterval).ConfigureAwait(false);
            }

            Debug.WriteLine($"VideoService.WaitForDecodersReadyAsync: Timeout after {timeoutMs}ms, some decoders may not be ready");
            return false;
        }

        /// <summary>
        /// Gets the IDs of layers from the provided list that have registered decoders.
        /// Used to filter playlist group source IDs to only include videos that actually exist.
        /// </summary>
        /// <param name="layerIds">The list of layer IDs to filter.</param>
        /// <returns>A list containing only the layer IDs that have registered decoders.</returns>
        public System.Collections.Generic.List<string> GetActiveLayerIds(System.Collections.Generic.List<string> layerIds)
        {
            if (layerIds == null || layerIds.Count == 0)
                return new System.Collections.Generic.List<string>();

            var activeIds = new System.Collections.Generic.List<string>();
            foreach (var id in layerIds)
            {
                if (!string.IsNullOrEmpty(id) && _decoders.ContainsKey(id))
                {
                    activeIds.Add(id);
                }
            }
            return activeIds;
        }

        /// <summary>
        /// Tries to get the last frame for a layer.
        /// </summary>
        /// <param name="layerId">The layer ID.</param>
        /// <param name="frame">The last frame if available.</param>
        /// <returns>True if a frame was available.</returns>
        public bool TryGetLastFrame(string layerId, out BitmapSource? frame)
        {
            frame = null;
            try
            {
                if (string.IsNullOrEmpty(layerId)) return false;
                if (_decoders.TryGetValue(layerId, out var tup))
                {
                    frame = tup.lastFrame;
                    return frame != null;
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.TryGetLastFrame: Error getting frame for {layerId}: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Pre-warms the specified layers by ensuring their decoders are ready for quick resume.
        /// This is called by PlaylistService to prepare the next group's videos for seamless transitions.
        /// </summary>
        /// <param name="layerIds">List of layer IDs to pre-warm.</param>
        public async Task PreWarmLayersAsync(System.Collections.Generic.List<string> layerIds)
        {
            if (layerIds == null || layerIds.Count == 0) return;

            try
            {
                Debug.WriteLine($"VideoService.PreWarmLayersAsync: Pre-warming {layerIds.Count} layers");

                foreach (var layerId in layerIds)
                {
                    if (string.IsNullOrEmpty(layerId)) continue;

                    // Check if decoder exists and is paused
                    if (_decoders.TryGetValue(layerId, out var t))
                    {
                        if (t.isPaused && t.model != null)
                        {
                            // Decoder exists but is paused - the resume will be fast
                            // because we maintain the model reference
                            Debug.WriteLine($"VideoService.PreWarmLayersAsync: Layer {layerId} is paused and ready for fast resume");
                        }
                        else if (t.decoder != null)
                        {
                            // Decoder is active - already warmed
                            Debug.WriteLine($"VideoService.PreWarmLayersAsync: Layer {layerId} already has active decoder");
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"VideoService.PreWarmLayersAsync: Layer {layerId} not found in decoders");
                    }
                }

                await Task.CompletedTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.PreWarmLayersAsync: Error: {ex}");
            }
        }

        /// <summary>
        /// Performs a fast resume for a layer that was paused, optimized for seamless transitions.
        /// Unlike regular ResumeLayerAsync, this minimizes delays.
        /// </summary>
        /// <param name="layerId">The layer ID to resume.</param>
        public async Task FastResumeLayerAsync(string layerId)
        {
            if (string.IsNullOrEmpty(layerId)) return;

            Debug.WriteLine($"VideoService.FastResumeLayerAsync: Fast resuming layer {layerId}");

            if (!_decoders.TryGetValue(layerId, out var t))
            {
                Debug.WriteLine($"VideoService.FastResumeLayerAsync: Layer {layerId} not found, falling back to regular resume");
                await ResumeLayerAsync(layerId).ConfigureAwait(false);
                return;
            }

            if (!t.isPaused)
            {
                Debug.WriteLine($"VideoService.FastResumeLayerAsync: Layer {layerId} is not paused");
                return;
            }

            if (t.model == null)
            {
                Debug.WriteLine($"VideoService.FastResumeLayerAsync: Layer {layerId} has no model, falling back to regular resume");
                await ResumeLayerAsync(layerId).ConfigureAwait(false);
                return;
            }

            try
            {
                var layer = t.model;
                var savedPosition = t.savedPosition;

                // CRITICAL: Clean up any existing decoder that might still be hanging around
                if (t.decoder != null && !t.decoder.IsDisposed)
                {
                    try
                    {
                        t.decoder.MarkDisposing();
                        t.cts?.Cancel();
                        t.decoder.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"VideoService.FastResumeLayerAsync: Error disposing old decoder for {layerId}: {ex}");
                    }
                }

                // Create new decoder without the 700ms wait
                var decoder = new FFmpegUnifiedDecoder(layer.SourcePath,
                    Math.Max(1, layer.Width > 0 ? layer.Width : 640),
                    Math.Max(1, layer.Height > 0 ? layer.Height : 480),
                    ResolveFfmpegExecutable())
                {
                    Loop = !_globalLoopingEnabled ? false : true, // Respect playlist looping setting
                    AudioEnabled = false,
                    Volume = 1.0f
                };

                decoder.SetResumePosition(savedPosition);

                var cts = new CancellationTokenSource();

                decoder.FrameDecodedWithTimestamp += (bmp, pts) => { };
                decoder.FrameDecoded += bmp =>
                {
                    if (bmp == null) return;
                    // Early rejection: skip frames from cancelled decoder sessions
                    try { if (cts.IsCancellationRequested) return; }
                    catch (ObjectDisposedException) { return; }

                    InvokeOnUi(() =>
                    {
                        try
                        {
                            ProcessDecodedFrameOnUi(layer, layerId, decoder, cts, bmp,
                                Math.Max(1, layer.Width > 0 ? layer.Width : 640),
                                Math.Max(1, layer.Height > 0 ? layer.Height : 480));
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"VideoService.FastResumeLayerAsync: Error processing frame for {layerId}: {ex}");
                        }
                    });
                };

                decoder.VideoEnded += () =>
                {
                    try
                    {
                        Debug.WriteLine($"VideoService.FastResumeLayerAsync: Video ended for layer {layerId}");
                        OnVideoCompleted(layerId);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"VideoService.FastResumeLayerAsync: Error handling video end for {layerId}: {ex}");
                    }
                };

                // Update decoder reference immediately
                _decoders[layerId] = (decoder, cts, layer, t.lastFrame, false, savedPosition);

                // Start decoder in background
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await decoder.StartAsync(cts.Token).ConfigureAwait(false);
                        Debug.WriteLine($"VideoService.FastResumeLayerAsync: Decoder started for layer {layerId}");
                    }
                    catch (OperationCanceledException)
                    {
                        Debug.WriteLine($"VideoService.FastResumeLayerAsync: Decoder start cancelled for layer {layerId}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"VideoService.FastResumeLayerAsync: Decoder start failed for layer {layerId}: {ex}");
                    }
                }, cts.Token);

                Debug.WriteLine($"VideoService.FastResumeLayerAsync: Layer {layerId} fast resumed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.FastResumeLayerAsync failed for {layerId}: {ex}");
                // Fall back to regular resume
                await ResumeLayerAsync(layerId).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Disposes resources used by the service.
        /// </summary>
        public void Dispose()
        {
            try
            {
                // Cancel all decoder tasks
                foreach (var kv in _decoders.ToArray())
                {
                    try
                    {
                        kv.Value.cts?.Cancel();
                        kv.Value.decoder?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"VideoService.Dispose: Failed to dispose decoder {kv.Key}: {ex}");
                    }
                }
                _decoders.Clear();
                _meshLayers.Clear();
                _lastFrameSent.Clear();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VideoService.Dispose failed: {ex}");
            }
        }

        /// <summary>
        /// Resolves the FFmpeg executable path.
        /// </summary>
        private string ResolveFfmpegExecutable()
        {
            return _ffmpegPath ?? "ffmpeg";
        }

        /// <summary>
        /// Probes video resolution from the file.
        /// </summary>
        private (int Width, int Height) ProbeVideoResolution(string path)
        {
            try
            {
                // Simple implementation - try to get resolution from file
                // For now, return default HD resolution
                return (1920, 1080);
            }
            catch
            {
                return (1920, 1080);
            }
        }
    }
}
