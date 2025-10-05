using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ProjectionMapper.Views;

namespace ProjectionMapper.Rendering
{
    /// <summary>
    /// Coordinates the renderer, a render loop, and a host control.
    /// This version subscribes to IRenderer.FrameReady and forwards BitmapSource frames to the RenderHostControl.
    /// It uses RenderLoop to call RenderFrameAsync at a target FPS.
    /// </summary>
    public sealed class RendererManager : IDisposable
    {
        private readonly IRenderer _renderer;
        private RenderLoop? _renderLoop;
        private bool _started;
        private bool _disposed;
        private RenderHostControl? _host;

        public RendererManager(IRenderer renderer)
        {
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _renderer.FrameReady += OnFrameReady;
        }

        /// <summary>
        /// Attach a RenderHostControl which will receive frames.
        /// Attaching can be done before StartAsync.
        /// </summary>
        public void AttachHost(RenderHostControl host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>
        /// Start rendering with the provided viewport size.
        /// </summary>
        public async Task StartAsync(int width, int height, CancellationToken token = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RendererManager));
            if (_started) return;

            await _renderer.InitializeAsync(width, height, token).ConfigureAwait(false);

            // Create simple render loop that calls into the renderer
            _renderLoop = new RenderLoop(async ct =>
            {
                await _renderer.RenderFrameAsync(ct).ConfigureAwait(false);
            }, targetFps: 30.0); // default 30 FPS for software renderer usage

            _renderLoop.Start();
            _started = true;
        }

        /// <summary>
        /// Stop rendering and release resources.
        /// </summary>
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

        private void OnFrameReady(System.Windows.Media.Imaging.BitmapSource? bmp)
        {
            if (_host == null) return;

            if (bmp == null)
            {
                // Optionally clear host
                Application.Current?.Dispatcher?.Invoke(() => _host.Clear());
                return;
            }

            // Ensure UI thread update
            if (Application.Current?.Dispatcher?.CheckAccess() == true)
            {
                _host.SetFrame(bmp);
            }
            else
            {
                Application.Current?.Dispatcher?.Invoke(() => _host.SetFrame(bmp));
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _renderer.FrameReady -= OnFrameReady;
            _ = StopAsync();
            _renderLoop?.Dispose();
            _renderer.Dispose();
        }
    }
}