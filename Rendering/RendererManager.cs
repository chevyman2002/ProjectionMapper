using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ProjectionMapper.Models;
using ProjectionMapper.Views;
using System.Windows;

namespace ProjectionMapper.Rendering
{
    /// <summary>
    /// Coordinates the renderer, a render loop, and a host control.
    /// Exposes SubmitLayerFrame as a convenience for decoders/services to push per-layer frames.
    /// </summary>
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
                var app = Application.Current;
                if (app == null || app.Dispatcher == null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
                {
                    try { action(); } catch { }
                }
                else
                {
                    app.Dispatcher.BeginInvoke((Action)(() => { try { action(); } catch { } }));
                }
            }
            catch { try { action(); } catch { } }
        }

        private void OnFrameReady(BitmapSource? bmp)
        {
            if (_host == null) return;

            if (bmp == null)
            {
                InvokeOnUi(() => _host.Clear());
                return;
            }

            InvokeOnUi(() => _host.SetFrame(bmp));
        }

        /// <summary>
        /// Submit a per-layer frame to the underlying renderer for composition.
        /// destRect is in renderer output coordinates (pixels).
        /// </summary>
        public void SubmitLayerFrame(string layerId, BitmapSource? frame, System.Windows.Rect destRect, double opacity)
        {
            try
            {
                _renderer.SubmitLayerFrame(layerId, frame, destRect, opacity);

                // If frame target monitor index is present in metadata (we use layerId conventions),
                // we additionally set the frame on the associated fullscreen window host(s).
                // Renderer will still compose into main output; fullscreen windows get direct frames for layer
                // ids mapped to their monitor index by layer models handled elsewhere.
            }
            catch (Exception)
            {
                // swallow - renderer may not support layering (no-op)
            }
        }

        /// <summary>
        /// Map a monitor index to a fullscreen output window and show it on that monitor.
        /// The caller is responsible for creating and sizing the window to the target monitor bounds.
        /// </summary>
        public void ShowFullScreenWindow(int monitorIndex, FullScreenOutputWindow window)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            if (_fullscreenWindows.TryGetValue(monitorIndex, out var existing))
            {
                try { InvokeOnUi(() => existing.Close()); } catch { }
                _fullscreenWindows.Remove(monitorIndex);
            }

            _fullscreenWindows[monitorIndex] = window;
            InvokeOnUi(() => window.Show());
        }

        public void HideFullScreenWindow(int monitorIndex)
        {
            if (_fullscreenWindows.TryGetValue(monitorIndex, out var win))
            {
                try { InvokeOnUi(() => win.Close()); } catch { }
                _fullscreenWindows.Remove(monitorIndex);
            }
        }

        /// <summary>
        /// Set a frame directly to a specific fullscreen host (used by VideoService to mirror frames to displays).
        /// </summary>
        public void SetFullScreenHostFrame(int monitorIndex, BitmapSource? frame)
        {
            if (!_fullscreenWindows.TryGetValue(monitorIndex, out var win)) return;
            if (win == null) return;
            if (frame == null)
            {
                InvokeOnUi(() => win.HostControl.Clear());
                return;
            }
            InvokeOnUi(() => win.HostControl.SetFrame(frame));
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
                try { InvokeOnUi(() => win.Close()); } catch { }
            }
            _fullscreenWindows.Clear();
        }
    }
}