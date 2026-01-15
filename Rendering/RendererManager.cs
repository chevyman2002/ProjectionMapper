// Rendering/RendererManager.cs
// Forwarding updated SubmitLayerFrame signature to the underlying renderer.
// Minor logging added to catch blocks per project guidelines.

using ProjectionMapper.Views;
using System;
using System.Numerics;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ProjectionMapper.Rendering
{
    public sealed class RendererManager : IDisposable
    {
        private readonly IRenderer _renderer;
        private RenderLoop? _renderLoop;
        private bool _started;
        private bool _disposed;
        private RenderHostControl? _host;

        // full-screen hosts per monitor index
        private readonly Dictionary<int, FullScreenOutputWindow> _fullscreenWindows = new();

        // Per-monitor renderers for separate composition
        private readonly Dictionary<int, IRenderer> _monitorRenderers = new();
        private readonly Dictionary<int, RenderLoop> _monitorRenderLoops = new();
        // Track monitor renderer pixel sizes (width, height)
        private readonly Dictionary<int, (int Width, int Height)> _monitorRendererSizes = new();

        public RendererManager(IRenderer renderer)
        {
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _renderer.FrameReady += OnFrameReady;
            ShowMeshOverlay = true; // enabled by default
        }

        public async Task ResizeAsync(int width, int height, CancellationToken token = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RendererManager));
            await _renderer.ResizeAsync(width, height, token).ConfigureAwait(false);
            OutputWidth = width; OutputHeight = height;
        }

        // Current renderer output size in pixels (set when StartAsync is called)
        public int OutputWidth { get; private set; }
        public int OutputHeight { get; private set; }

        /// <summary>
        /// When true, mesh overlay (quad outline and points) will be shown on output hosts when provided.
        /// </summary>
        public bool ShowMeshOverlay { get; set; }

        /// <summary>
        /// Map normalized output coordinates (0..1) to renderer pixel coordinates using the attached host's current frame
        /// or, if a monitorIndex is provided and a fullscreen host exists for that monitor, using that host's current frame.
        /// Returns null if mapping is not possible (no host or no current frame).
        /// </summary>
        public Point[]? MapNormalizedToRendererPoints(Vector2[]? normalized, int? monitorIndex = null)
        {
            if (normalized == null || normalized.Length < 4) return null;
            try
            {
                BitmapSource? frame = null;

                // Prefer monitor renderer size if available
                if (monitorIndex.HasValue && _monitorRendererSizes.TryGetValue(monitorIndex.Value, out var monSize))
                {
                    double w = monSize.Width;
                    double h = monSize.Height;
                    if (w > 0 && h > 0)
                    {
                        var pts = new Point[4];
                        for (int i = 0; i < 4; ++i)
                        {
                            var p = normalized[i];
                            pts[i] = new Point(p.X * w, p.Y * h);
                        }
                        return pts;
                    }
                }

                // If a specific monitor index is requested and a fullscreen host exists for that monitor, prefer that host's current frame
                if (monitorIndex.HasValue && _fullscreenWindows.TryGetValue(monitorIndex.Value, out var win) && win != null)
                {
                    try { frame = win.HostControl?.CurrentFrame; }
                    catch (Exception ex) { Debug.WriteLine($"MapNormalizedToRendererPoints: failed reading fullscreen host frame: {ex}"); frame = null; }

                    if (frame == null)
                    {
                        // Use the canonical output size for the main renderer
                        double w = OutputWidth > 0 ? OutputWidth : 0;
                        double h = OutputHeight > 0 ? OutputHeight : 0;
                        if (w > 0 && h > 0)
                        {
                            var pts = new Point[4];
                            for (int i = 0; i < 4; ++i)
                            {
                                var p = normalized[i];
                                pts[i] = new Point(p.X * w, p.Y * h);
                            }
                            return pts;
                        }
                        return null;
                    }
                }

                // Fallback to main attached host if fullscreen host not available or no monitor specified
                if (frame == null)
                {
                    if (_host == null) return null;
                    frame = _host.CurrentFrame;
                }
                // Prefer using the renderer's canonical output size when available so normalized coordinates
                // map into renderer pixel space deterministically. Fall back to the host frame pixel size
                // only if the renderer output size is not known.
                double w2 = OutputWidth > 0 ? OutputWidth : (frame?.PixelWidth ?? 0);
                double h2 = OutputHeight > 0 ? OutputHeight : (frame?.PixelHeight ?? 0);
                if (w2 <= 0 || h2 <= 0) return null;
                var pts2 = new Point[4];
                for (int i = 0; i < 4; ++i)
                {
                    var p = normalized[i];
                    pts2[i] = new Point(p.X * w2, p.Y * h2);
                }
                return pts2;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MapNormalizedToRendererPoints failed: {ex}");
                return null;
            }
        }

        public void AttachHost(RenderHostControl host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        // Attach a fullscreen host for a specific monitor index
        public void AttachHost(int monitorIndex, FullScreenOutputWindow window)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            _fullscreenWindows[monitorIndex] = window;
        }

        public async Task StartAsync(int width, int height, CancellationToken token = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RendererManager));
            if (_started) return;

            await _renderer.InitializeAsync(width, height, token).ConfigureAwait(false);

            _renderLoop = new RenderLoop(async ct =>
            {
                await _renderer.RenderFrameAsync(ct).ConfigureAwait(false);
            }, targetFps: 30.0);

            _renderLoop.Start();
            _started = true;
            OutputWidth = width; OutputHeight = height;
        }

        public async Task StopAsync()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RendererManager));
            if (!_started) return;

            if (_renderLoop != null)
            {
                await _renderLoop.StopAsync().ConfigureAwait(false);
                _renderLoop = null;
            }

            // Stop monitor render loops
            foreach (var kv in _monitorRenderLoops.ToArray())
            {
                try { kv.Value.StopAsync().GetAwaiter().GetResult(); } catch { }
                try { kv.Value.Dispose(); } catch { }
            }
            _monitorRenderLoops.Clear();

            // Dispose monitor renderers
            foreach (var kv in _monitorRenderers.ToArray())
            {
                try { kv.Value.Dispose(); } catch { }
            }
            _monitorRenderers.Clear();

            _started = false;
        }

        private void InvokeOnUi(Action action)
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app == null || app.Dispatcher == null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
                {
                    try { action(); } catch (Exception ex) { Debug.WriteLine($"InvokeOnUi immediate action failed: {ex}"); }
                }
                else
                {
                    app.Dispatcher.BeginInvoke((Action)(() => { try { action(); } catch (Exception ex) { Debug.WriteLine($"InvokeOnUi dispatched action failed: {ex}"); } }));
                }
            }
            catch (Exception ex) { try { action(); } catch (Exception ex2) { Debug.WriteLine($"InvokeOnUi outer catch: {ex}; inner: {ex2}"); } }
        }

        private void OnFrameReady(BitmapSource? bmp)
        {
            // Always update the main host if attached
            if (_host != null)
            {
                if (bmp == null)
                {
                    InvokeOnUi(() => _host.Clear());
                }
                else
                {
                    InvokeOnUi(() => _host.SetFrame(bmp));
                }
            }

            // Fullscreen windows now have their own renderers, so no mirroring needed
        }

        private void OnMonitorFrameReady(int monitorIndex, BitmapSource? bmp)
        {
            try
            {
                if (_fullscreenWindows.TryGetValue(monitorIndex, out var win) && win != null)
                {
                    if (bmp == null)
                    {
                        InvokeOnUi(() => { try { win.HostControl?.Clear(); } catch { } });
                    }
                    else
                    {
                        InvokeOnUi(() => { try { win.HostControl?.SetFrame(bmp); } catch { } });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OnMonitorFrameReady failed for monitor {monitorIndex}: {ex}");
            }
        }

        // Track multiple mesh overlays per monitor (or main host)
        private readonly Dictionary<int, List<(Point[] QuadPoints, bool ShowPoints, string LayerId)>> _monitorMeshOverlays = new();
        private readonly List<(Point[] QuadPoints, bool ShowPoints, string LayerId)> _mainHostMeshOverlays = new();

        /// <summary>
        /// Set mesh overlay (quad outline and optional points) on either the main attached host or a fullscreen host for a monitor.
        /// If monitorIndex is null, the main attached host will be used. If quadPoints is null the overlay will be cleared.
        /// This method only sets a single overlay and clears others - use SetMultipleMeshOverlaysForMonitor for multiple overlays.
        /// </summary>
        public void SetMeshOverlayForMonitor(int? monitorIndex, Point[]? quadPoints, bool showPoints)
        {
            if (!ShowMeshOverlay)
            {
                // If overlays disabled globally, ensure cleared
                ClearAllOverlays();
                return;
            }

            try
            {
                if (monitorIndex.HasValue)
                {
                    if (_fullscreenWindows.TryGetValue(monitorIndex.Value, out var win) && win != null)
                    {
                        InvokeOnUi(() => win.HostControl.SetMeshOverlay(quadPoints, showPoints));
                        return;
                    }
                }

                // fallback to main host
                if (_host != null)
                {
                    InvokeOnUi(() => _host.SetMeshOverlay(quadPoints, showPoints));
                }
            }
            catch (Exception ex) { Debug.WriteLine($"SetMeshOverlayForMonitor failed: {ex}"); }
        }

        /// <summary>
        /// Add a mesh overlay for a specific layer to a monitor. Multiple overlays can be added and all will be displayed.
        /// When a specific monitorIndex is provided, the overlay is added to BOTH the main preview host AND the target monitor.
        /// </summary>
        public void AddMeshOverlayForMonitor(int? monitorIndex, Point[]? quadPoints, bool showPoints, string layerId)
        {
            if (!ShowMeshOverlay || quadPoints == null || quadPoints.Length < 4) return;

            try
            {
                // ALWAYS add to main host for the output preview pane
                // Remove any existing overlay for this layer from main host
                _mainHostMeshOverlays.RemoveAll(x => x.LayerId == layerId);
                
                // Add the new overlay to main host
                _mainHostMeshOverlays.Add((quadPoints, showPoints, layerId));
                
                // Update the main host display
                RefreshMeshOverlaysForMainHost();

                // ALSO add to the specific monitor if one is specified
                if (monitorIndex.HasValue)
                {
                    if (!_monitorMeshOverlays.ContainsKey(monitorIndex.Value))
                        _monitorMeshOverlays[monitorIndex.Value] = new List<(Point[], bool, string)>();

                    // Remove any existing overlay for this layer
                    _monitorMeshOverlays[monitorIndex.Value].RemoveAll(x => x.LayerId == layerId);
                    
                    // Add the new overlay
                    _monitorMeshOverlays[monitorIndex.Value].Add((quadPoints, showPoints, layerId));

                    // Update the display
                    RefreshMeshOverlaysForMonitor(monitorIndex.Value);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"AddMeshOverlayForMonitor failed: {ex}"); }
        }

        /// <summary>
        /// Remove a mesh overlay for a specific layer from a monitor.
        /// Removes from BOTH the main preview host AND the specified target monitor.
        /// </summary>
        public void RemoveMeshOverlayForMonitor(int? monitorIndex, string layerId)
        {
            try
            {
                // ALWAYS remove from main host for the output preview pane
                _mainHostMeshOverlays.RemoveAll(x => x.LayerId == layerId);
                RefreshMeshOverlaysForMainHost();

                // ALSO remove from the specific monitor if one is specified
                if (monitorIndex.HasValue)
                {
                    if (_monitorMeshOverlays.TryGetValue(monitorIndex.Value, out var overlays))
                    {
                        overlays.RemoveAll(x => x.LayerId == layerId);
                        RefreshMeshOverlaysForMonitor(monitorIndex.Value);
                    }
                }
                
                // Also try to remove from ALL monitors in case the target monitor changed
                foreach (var kv in _monitorMeshOverlays.ToArray())
                {
                    if (kv.Value != null)
                    {
                        int countBefore = kv.Value.Count;
                        kv.Value.RemoveAll(x => x.LayerId == layerId);
                        if (kv.Value.Count != countBefore)
                        {
                            RefreshMeshOverlaysForMonitor(kv.Key);
                        }
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"RemoveMeshOverlayForMonitor failed: {ex}"); }
        }

        private void RefreshMeshOverlaysForMonitor(int monitorIndex)
        {
            try
            {
                if (_fullscreenWindows.TryGetValue(monitorIndex, out var win) && win != null)
                {
                    InvokeOnUi(() =>
                    {
                        try
                        {
                            // Clear existing mesh overlays first
                            win.HostControl.ClearMeshOverlay();

                            // Draw all overlays for this monitor using AddMeshOverlay to show multiple
                            if (_monitorMeshOverlays.TryGetValue(monitorIndex, out var overlays))
                            {
                                foreach (var (quadPoints, showPoints, layerId) in overlays)
                                {
                                    win.HostControl.AddMeshOverlay(quadPoints, showPoints);
                                }
                            }
                        }
                        catch (Exception ex) { Debug.WriteLine($"RefreshMeshOverlaysForMonitor inner failed: {ex}"); }
                    });
                }
            }
            catch (Exception ex) { Debug.WriteLine($"RefreshMeshOverlaysForMonitor failed: {ex}"); }
        }

        private void RefreshMeshOverlaysForMainHost()
        {
            try
            {
                if (_host != null)
                {
                    InvokeOnUi(() =>
                    {
                        try
                        {
                            // Clear existing mesh overlays first
                            _host.ClearMeshOverlay();

                            // Draw all overlays for main host using AddMeshOverlay to show multiple
                            foreach (var (quadPoints, showPoints, layerId) in _mainHostMeshOverlays)
                            {
                                _host.AddMeshOverlay(quadPoints, showPoints);
                            }
                        }
                        catch (Exception ex) { Debug.WriteLine($"RefreshMeshOverlaysForMainHost inner failed: {ex}"); }
                    });
                }
            }
            catch (Exception ex) { Debug.WriteLine($"RefreshMeshOverlaysForMainHost failed: {ex}"); }
        }

        /// <summary>
        /// Clear overlays on all hosts (main and fullscreen).
        /// </summary>
        public void ClearAllOverlays()
        {
            try
            {
                // Clear the tracking collections
                _monitorMeshOverlays.Clear();
                _mainHostMeshOverlays.Clear();

                // Clear visual overlays on hosts
                if (_host != null) InvokeOnUi(() => _host.ClearOverlay());
                foreach (var win in _fullscreenWindows.Values.ToList())
                {
                    if (win?.HostControl != null) InvokeOnUi(() => win.HostControl.ClearOverlay());
                }
            }
            catch { }
        }

        /// <summary>
        /// Submit a per-layer frame to the underlying renderer for composition.
        /// destRect is in renderer output coordinates (pixels).
        /// destQuad: optional quad in renderer coordinates (TopLeft, TopRight, BottomLeft, BottomRight).
        /// </summary>
        public void SubmitLayerFrame(string layerId, BitmapSource? frame, Rect destRect, Point[]? destQuad, double opacity)
        {
            try
            {
                _renderer.SubmitLayerFrame(layerId, frame, destRect, destQuad, opacity);

                // If frame target monitor index is present in metadata (we use layerId conventions),
                // we additionally set the frame on the associated fullscreen window host(s).
                // Renderer will still compose into main output; fullscreen windows get direct frames for layer
                // ids mapped to their monitor index by layer models handled elsewhere.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RendererManager.SubmitLayerFrame failed: {ex}");
                // swallow - renderer may not support layering (no-op)
            }
        }

        /// <summary>
        /// Submit a per-layer frame to the appropriate renderer for composition based on target monitor.
        /// destRect is in renderer output coordinates (pixels).
        /// destQuad: optional quad in renderer coordinates (TopLeft, TopRight, BottomLeft, BottomRight).
        /// Submits to both the target monitor renderer AND the main renderer so output preview works.
        /// </summary>
        public void SubmitLayerFrameForMonitor(string layerId, BitmapSource? frame, Rect destRect, Point[]? destQuad, double opacity, int targetMonitorIndex)
        {
            try
            {
                // Always submit to main renderer first so the output preview displays the content
                _renderer.SubmitLayerFrame(layerId, frame, destRect, destQuad, opacity);

                // If target monitor is unspecified (-1), we're done - only show in main preview
                if (targetMonitorIndex == -1)
                {
                    return;
                }

                // Also submit to the target monitor renderer if available for fullscreen output
                if (_monitorRenderers.TryGetValue(targetMonitorIndex, out var monitorRenderer))
                {
                    monitorRenderer.SubmitLayerFrame(layerId, frame, destRect, destQuad, opacity);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RendererManager.SubmitLayerFrameForMonitor failed: {ex}");
                // swallow - renderer may not support layering (no-op)
            }
        }

        /// <summary>
        /// Remove a layer from the underlying renderer. Call this when a layer should no longer be rendered.
        /// </summary>
        public void RemoveLayer(string? layerId)
        {
            if (string.IsNullOrEmpty(layerId)) return;
            
            try
            {
                if (_renderer is SoftwareRenderer sr)
                {
                    sr.RemoveLayer(layerId);
                }
                
                // Also remove from all monitor renderers
                foreach (var kv in _monitorRenderers.ToArray())
                {
                    if (kv.Value is SoftwareRenderer monitorSr)
                    {
                        try { monitorSr.RemoveLayer(layerId); } catch { }
                    }
                }
                
                Debug.WriteLine($"RendererManager.RemoveLayer: Removed layer {layerId} from renderers");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RendererManager.RemoveLayer failed: {ex}");
            }
        }

        /// <summary>
        /// Set a frame directly to a specific fullscreen host (used by VideoService to mirror frames to displays).
        /// </summary>
        public void SetFullScreenHostFrame(int monitorIndex, BitmapSource? frame)
        {
            try
            {
                if (!_fullscreenWindows.TryGetValue(monitorIndex, out var win))
                {
                    Debug.WriteLine($"SetFullScreenHostFrame: no fullscreen window registered for monitor {monitorIndex}");
                    return;
                }
                if (win == null)
                {
                    Debug.WriteLine($"SetFullScreenHostFrame: fullscreen window entry is null for monitor {monitorIndex}");
                    return;
                }
                if (frame == null)
                {
                    InvokeOnUi(() => { try { win.HostControl?.Clear(); } catch { } });
                }
                else
                {
                    InvokeOnUi(() => { try { win.HostControl?.SetFrame(frame); } catch { } });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetFullScreenHostFrame failed: {ex}");
            }
        }

        /// <summary>
        /// Show a fullscreen window on the specified monitor.
        /// Creates a dedicated renderer and render loop for this monitor.
        /// </summary>
        public void ShowFullScreenWindow(int monitorIndex, FullScreenOutputWindow window, int monitorWidth = 0, int monitorHeight = 0)
        {
            try
            {
                if (window == null) throw new ArgumentNullException(nameof(window));

                // Clean up existing resources for this monitor if any
                if (_monitorRenderLoops.TryGetValue(monitorIndex, out var oldLoop))
                {
                    try { oldLoop.StopAsync().GetAwaiter().GetResult(); } catch { }
                    try { oldLoop.Dispose(); } catch { }
                    _monitorRenderLoops.Remove(monitorIndex);
                }
                if (_monitorRenderers.TryGetValue(monitorIndex, out var oldRenderer))
                {
                    try { oldRenderer.Dispose(); } catch { }
                    _monitorRenderers.Remove(monitorIndex);
                }
                if (_fullscreenWindows.TryGetValue(monitorIndex, out var oldWin) && oldWin != window)
                {
                    try { InvokeOnUi(() => oldWin.Close()); } catch { }
                    _fullscreenWindows.Remove(monitorIndex);
                }

                // Register the window
                _fullscreenWindows[monitorIndex] = window;

                // Ensure hosted control stretches to fill in fullscreen
                InvokeOnUi(() =>
                {
                    try { window.HostControl?.SetFullscreenStretch(true); } catch { }
                });

                // Create a dedicated renderer for this monitor
                try
                {
                    var monitorRenderer = new SoftwareRenderer();

                    int renderW = (monitorWidth > 0) ? monitorWidth : (OutputWidth > 0 ? OutputWidth : 1920);
                    int renderH = (monitorHeight > 0) ? monitorHeight : (OutputHeight > 0 ? OutputHeight : 1080);

                    _monitorRendererSizes[monitorIndex] = (renderW, renderH);

                    // Initialize monitor renderer
                    monitorRenderer.InitializeAsync(renderW, renderH, CancellationToken.None).GetAwaiter().GetResult();

                    // Subscribe to its FrameReady event to update the fullscreen host
                    monitorRenderer.FrameReady += (bmp) => OnMonitorFrameReady(monitorIndex, bmp);

                    // Store renderer
                    _monitorRenderers[monitorIndex] = monitorRenderer;

                    // Start a render loop for the monitor renderer
                    var loop = new RenderLoop(async ct => await monitorRenderer.RenderFrameAsync(ct).ConfigureAwait(false), targetFps: 30.0);
                    loop.Start();
                    _monitorRenderLoops[monitorIndex] = loop;

                    Debug.WriteLine($"ShowFullScreenWindow: Created renderer for monitor {monitorIndex} at {renderW}x{renderH}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ShowFullScreenWindow: failed to initialize monitor renderer: {ex}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ShowFullScreenWindow: exception: {ex}");
            }
        }

        public void HideFullScreenWindow(int monitorIndex)
        {
            try
            {
                if (!_fullscreenWindows.TryGetValue(monitorIndex, out var win) || win == null)
                {
                    Debug.WriteLine($"HideFullScreenWindow: no fullscreen window for monitor {monitorIndex}");
                    return;
                }

                // Revert host stretch and close window on UI thread
                try
                {
                    InvokeOnUi(() =>
                    {
                        try { win.HostControl?.SetFullscreenStretch(false); } catch { }
                        try { win.Close(); } catch (Exception exClose) { Debug.WriteLine($"HideFullScreenWindow: window close failed: {exClose}"); }
                    });
                }
                catch (Exception exUi)
                {
                    Debug.WriteLine($"HideFullScreenWindow: failed to invoke UI close for monitor {monitorIndex}: {exUi}");
                }

                _fullscreenWindows.Remove(monitorIndex);

                // Dispose associated monitor renderer and stop loop
                if (_monitorRenderLoops.TryGetValue(monitorIndex, out var loop))
                {
                    try { loop.StopAsync().GetAwaiter().GetResult(); } catch (Exception exR) { Debug.WriteLine($"HideFullScreenWindow: monitor render loop stop failed for {monitorIndex}: {exR}"); }
                    _monitorRenderLoops.Remove(monitorIndex);
                }

                if (_monitorRenderers.TryGetValue(monitorIndex, out var renderer))
                {
                    try { renderer.Dispose(); } catch (Exception exR) { Debug.WriteLine($"HideFullScreenWindow: renderer dispose failed for monitor {monitorIndex}: {exR}"); }
                    _monitorRenderers.Remove(monitorIndex);
                }

                // Remove stored size
                if (_monitorRendererSizes.ContainsKey(monitorIndex)) _monitorRendererSizes.Remove(monitorIndex);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HideFullScreenWindow: unexpected error for monitor {monitorIndex}: {ex}");
            }
        }

        public void Dispose()
        {
            try
            {
                // Stop the main loop without waiting
                if (_renderLoop != null)
                {
                    _renderLoop.StopAsync();
                }

                // Stop monitor loops without waiting
                foreach (var kv in _monitorRenderLoops.ToArray())
                {
                    kv.Value.StopAsync();
                }
            }
            catch { }

            try
            {
                _renderer.Dispose();
            }
            catch { }

            // Dispose all monitor renderers
            foreach (var kv in _monitorRenderers.ToArray())
            {
                try { kv.Value.Dispose(); } catch { }
            }
            _monitorRenderers.Clear();

            // Dispose monitor loops
            foreach (var kv in _monitorRenderLoops.ToArray())
            {
                try { kv.Value.Dispose(); } catch { }
            }
            _monitorRenderLoops.Clear();

            // Close all fullscreen windows
            foreach (var win in _fullscreenWindows.Values.ToList())
            {
                try { InvokeOnUi(() => win.Close()); } catch { }
            }
            _fullscreenWindows.Clear();

            _disposed = true;
        }
    }
}