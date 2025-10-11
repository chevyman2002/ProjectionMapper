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
        }

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

                // If a specific monitor index is requested, prefer that fullscreen host's current frame
                if (monitorIndex.HasValue && _fullscreenWindows.TryGetValue(monitorIndex.Value, out var win) && win != null)
                {
                    try { frame = win.HostControl?.CurrentFrame; }
                    catch (Exception ex) { Debug.WriteLine($"MapNormalizedToRendererPoints: failed reading fullscreen host frame: {ex}"); frame = null; }
                }

                // Fallback to main attached host
                if (frame == null)
                {
                    if (_host == null) return null;
                    frame = _host.CurrentFrame;
                }
                if (frame == null) return null;
                double w = frame.PixelWidth;
                double h = frame.PixelHeight;
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
                    try { InvokeOnUi(() => win?.HostControl?.Clear()); } catch (Exception ex) { Debug.WriteLine($"OnFrameReady clear fullscreen failed: {ex}"); }
                }
            }
            else
            {
                foreach (var win in _fullscreenWindows.Values.ToList())
                {
                try
                {
                    // All accesses to Window/Control properties must be done on UI thread.
                    InvokeOnUi(() =>
                    {
                        try
                        {
                            if (win == null)
                            {
                                Debug.WriteLine("OnFrameReady: encountered null fullscreen window in collection (UI thread)");
                                return;
                            }
                            if (win.HostControl == null)
                            {
                                Debug.WriteLine("OnFrameReady: fullscreen window has null HostControl (UI thread)");
                                return;
                            }
                            try
                            {
                                Debug.WriteLine($"OnFrameReady: preparing to mirror frame to fullscreen host for window '{win.Title}' (UI thread). Visible={win.IsVisible}, State={win.WindowState}");
                                if (!win.IsVisible || win.WindowState == WindowState.Minimized)
                                {
                                    Debug.WriteLine($"OnFrameReady: skipping SetFrame because window not visible or minimized for monitor host '{win.Title}'");
                                    return;
                                }

                                try
                                {
                                    win.HostControl.SetFrame(bmp);

                                    // Log diagnostic about the host control's current frame
                                    try
                                    {
                                        var cf = win.HostControl.CurrentFrame;
                                        if (cf != null)
                                        {
                                            Debug.WriteLine($"OnFrameReady: SetFrame succeeded; HostControl.CurrentFrame size={cf.PixelWidth}x{cf.PixelHeight}");
                                        }
                                        else
                                        {
                                            Debug.WriteLine($"OnFrameReady: SetFrame completed but HostControl.CurrentFrame is null");
                                        }
                                    }
                                    catch (Exception exInner2)
                                    {
                                        Debug.WriteLine($"OnFrameReady: failed to read HostControl.CurrentFrame (UI thread): {exInner2}");
                                    }

                                    try
                                    {
                                        Debug.WriteLine($"OnFrameReady: fullscreen window bounds L,T,W,H = {win.Left},{win.Top},{win.Width},{win.Height}");
                                    }
                                    catch { }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"OnFrameReady set fullscreen failed (UI thread): {ex}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"OnFrameReady inner UI action failed: {ex}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"OnFrameReady inner UI action failed: {ex}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"OnFrameReady: exception while scheduling mirror to fullscreen window: {ex}");
                }
                }
            }
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
            _ = StopAsync();
            _renderLoop?.Dispose();
            _renderer.Dispose();

            foreach (var win in _fullscreenWindows.Values.ToList())
            {
                try { InvokeOnUi(() => win.Close()); } catch (Exception ex) { Debug.WriteLine($"Dispose closing fullscreen window failed: {ex}"); }
            }
            _fullscreenWindows.Clear();
        }
    }
}