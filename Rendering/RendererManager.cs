using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ProjectionMapper.Models;
using ProjectionMapper.Views;

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

        public RendererManager(IRenderer renderer)
        {
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _renderer.FrameReady += OnFrameReady;
        }

        public void AttachHost(RenderHostControl host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
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

        private void OnFrameReady(BitmapSource? bmp)
        {
            if (_host == null) return;

            if (bmp == null)
            {
                _host.Clear();
                return;
            }

            _host.SetFrame(bmp);
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
            }
            catch (Exception)
            {
                // swallow - renderer may not support layering (no-op)
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