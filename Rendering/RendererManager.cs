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

                // If a specific monitor index is requested and a fullscreen host exists for it, prefer that host's current frame
                // because the mapping should use the pixel space of the target display. Otherwise fall back to the main attached host.
                if (monitorIndex.HasValue && _fullscreenWindows.TryGetValue(monitorIndex.Value, out var win) && win != null)
                {
                    try { frame = win.HostControl?.CurrentFrame; }
                    catch (Exception ex) { Debug.WriteLine($"MapNormalizedToRendererPoints: failed reading fullscreen host frame: {ex}"); frame = null; }
                }

                // Fallback to main attached host if fullscreen host not available
                if (frame == null)
                {
                    if (_host == null) return null;
                    frame = _host.CurrentFrame;
                }
                // Prefer using the renderer's canonical output size when available so normalized coordinates
                // map into renderer pixel space deterministically. Fall back to the host frame pixel size
                // only if the renderer output size is not known.
                double w = OutputWidth > 0 ? OutputWidth : (frame?.PixelWidth ?? 0);
                double h = OutputHeight > 0 ? OutputHeight : (frame?.PixelHeight ?? 0);
                if (w <= 0 || h <= 0) return null;
                var pts = new Point[4];
                for (int i = 0; i < 4; ++i)
                {
                    var p = normalized[i];
                    pts[i] = new Point(p.X * w, p.Y * h);
                }
                return pts;
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

            // Mirror the composed frame to any fullscreen windows so users see the same composed output
            if (bmp == null)
            {
                foreach (var win in _fullscreenWindows.Values.ToList())
                {
                    try { InvokeOnUi(() => win?.HostControl?.Clear()); } catch { }
                }
            }
            else
            {
                foreach (var win in _fullscreenWindows.Values.ToList())
                {
                    try
                    {
                        InvokeOnUi(() =>
                        {
                            if (win == null || win.HostControl == null) return;
                            if (!win.IsVisible || win.WindowState == WindowState.Minimized) return;
                            try { win.HostControl.SetFrame(bmp); } catch { }
                        });
                    }
                    catch { }
                }
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
        /// </summary>
        public void AddMeshOverlayForMonitor(int? monitorIndex, Point[]? quadPoints, bool showPoints, string layerId)
        {
            if (!ShowMeshOverlay || quadPoints == null || quadPoints.Length < 4) return;

            try
            {
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
                else
                {
                    // Remove any existing overlay for this layer
                    _mainHostMeshOverlays.RemoveAll(x => x.LayerId == layerId);
                    
                    // Add the new overlay
                    _mainHostMeshOverlays.Add((quadPoints, showPoints, layerId));

                    // Update the display
                    RefreshMeshOverlaysForMainHost();
                }
            }
            catch (Exception ex) { Debug.WriteLine($"AddMeshOverlayForMonitor failed: {ex}"); }
        }

        /// <summary>
        /// Remove a mesh overlay for a specific layer from a monitor.
        /// </summary>
        public void RemoveMeshOverlayForMonitor(int? monitorIndex, string layerId)
        {
            try
            {
                if (monitorIndex.HasValue)
                {
                    if (_monitorMeshOverlays.TryGetValue(monitorIndex.Value, out var overlays))
                    {
                        overlays.RemoveAll(x => x.LayerId == layerId);
                        RefreshMeshOverlaysForMonitor(monitorIndex.Value);
                    }
                }
                else
                {
                    _mainHostMeshOverlays.RemoveAll(x => x.LayerId == layerId);
                    RefreshMeshOverlaysForMainHost();
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
                    Debug.WriteLine($"SetFullScreenHostFrame: clearing frame on monitor {monitorIndex}");
                    InvokeOnUi(() => win.HostControl.Clear());
                    return;
                }

                Debug.WriteLine($"SetFullScreenHostFrame: setting frame on monitor {monitorIndex}");
                InvokeOnUi(() =>
                {
                    try
                    {
                        win.HostControl.SetFrame(frame);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"SetFullScreenHostFrame: failed to set frame on monitor {monitorIndex}: {ex}");
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetFullScreenHostFrame: outer exception for monitor {monitorIndex}: {ex}");
            }
        }

        public void ShowFullScreenWindow(int monitorIndex, FullScreenOutputWindow window)
        {
            try
            {
                // Show and activate on UI thread, then register the window
                InvokeOnUi(() =>
                {
                    try
                    {
                        // Window may have been positioned via native APIs before; ensure we show it but keep it in Normal state
                        window.Show();
                        window.Activate();
                        try { window.WindowState = WindowState.Normal; } catch { }
                        try { window.Topmost = true; } catch { }
                        try { window.Focus(); } catch { }

                        // Force layout/update so the hosted control can initialize its visual tree on the UI thread
                        try { window.UpdateLayout(); } catch { }

                        _fullscreenWindows[monitorIndex] = window;
                        try
                        {
                            var hasHost = window.HostControl != null;
                            Debug.WriteLine($"ShowFullScreenWindow (UI): monitor {monitorIndex} window registered, HostControl present: {hasHost}, bounds={window.Left},{window.Top},{window.Width}x{window.Height}");
                        }
                        catch (Exception exInner) { Debug.WriteLine($"ShowFullScreenWindow (UI): error inspecting HostControl for monitor {monitorIndex}: {exInner}"); }
                    }
                    catch (Exception exUi) { Debug.WriteLine($"ShowFullScreenWindow (UI) failed: {exUi}"); }
                });
            }
            catch (Exception ex) { Debug.WriteLine($"ShowFullScreenWindow failed: {ex}"); }
        }

        public void HideFullScreenWindow(int monitorIndex)
        {
            if (!_fullscreenWindows.TryGetValue(monitorIndex, out var win)) return;
            try { InvokeOnUi(() => win.Close()); } catch (Exception ex) { Debug.WriteLine($"HideFullScreenWindow failed: {ex}"); }
            _fullscreenWindows.Remove(monitorIndex);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _renderer.FrameReady -= OnFrameReady;

            try
            {
                // Stop the render loop synchronously to ensure no further RenderFrameAsync calls
                // are made against the renderer while we are disposing it. Use GetAwaiter().GetResult()
                // to synchronously wait for StopAsync to complete on dispose.
                try { StopAsync().GetAwaiter().GetResult(); } catch (Exception exStop) { Debug.WriteLine($"RendererManager.Dispose: StopAsync failed: {exStop}"); }

                // Dispose the render loop and renderer after stopping the loop.
                try { _renderLoop?.Dispose(); } catch (Exception exLoop) { Debug.WriteLine($"RendererManager.Dispose: renderLoop.Dispose failed: {exLoop}"); }
                try { _renderer.Dispose(); } catch (Exception exR) { Debug.WriteLine($"RendererManager.Dispose: renderer.Dispose failed: {exR}"); }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RendererManager.Dispose: unexpected error while stopping renderer: {ex}");
            }

            foreach (var win in _fullscreenWindows.Values.ToList())
            {
                try { InvokeOnUi(() => win.Close()); } catch (Exception ex) { Debug.WriteLine($"Dispose closing fullscreen window failed: {ex}"); }
            }
            _fullscreenWindows.Clear();
        }
    }
}