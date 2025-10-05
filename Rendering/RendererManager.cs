using System;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectionMapper.Rendering
{
    /// <summary>
    /// Coordinates the renderer, a render loop, and the host surface.
    /// This manager is the place to orchestrate decoded frames (from FFmpegService) being uploaded to GPU textures
    /// and presented to the RenderHostControl via D3D11InteropHelper.
    ///
    /// Current implementation is a lightweight orchestration layer with start/stop semantics.
    /// </summary>
    public sealed class RendererManager : IDisposable
    {
        private readonly IRenderer _renderer;
        private RenderLoop? _renderLoop;
        private readonly D3D11InteropHelper _interopHelper = new();
        private bool _disposed;

        public RendererManager(IRenderer renderer)
        {
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        }

        /// <summary>
        /// Start rendering with the provided viewport size.
        /// </summary>
        public async Task StartAsync(int width, int height, CancellationToken token = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RendererManager));

            await _renderer.InitializeAsync(width, height, token).ConfigureAwait(false);

            // Create simple render loop that calls into the renderer
            _renderLoop = new RenderLoop(async ct =>
            {
                await _renderer.RenderFrameAsync(ct).ConfigureAwait(false);
            }, targetFps: 60.0);

            _renderLoop.Start();
        }

        /// <summary>
        /// Stop rendering and release resources.
        /// </summary>
        public async Task StopAsync()
        {
            if (_renderLoop != null)
            {
                await _renderLoop.StopAsync().ConfigureAwait(false);
                _renderLoop = null;
            }

            _renderer.Dispose();
            _interopHelper.Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _renderLoop?.StopAsync().GetAwaiter().GetResult();
            _interopHelper.Dispose();
            _renderer.Dispose();
        }
    }
}