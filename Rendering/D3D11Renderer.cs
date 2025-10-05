using System;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectionMapper.Rendering
{
    /// <summary>
    /// D3D11 renderer stub. In later steps this will be implemented using Vortice.Windows to create a D3D11 device,
    /// create textures for decoded frames, and present to a WPF host surface (D3DImage/SwapChainPanel).
    /// For now the methods are no-op but present a correct surface for integration and tests.
    /// </summary>
    public class D3D11Renderer : IRenderer
    {
        private bool _initialized;

        public D3D11Renderer()
        {
        }

        public Task InitializeAsync(int width, int height, CancellationToken token = default)
        {
            // TODO: create D3D11 device and resources on a renderer thread; use Vortice.Windows.
            _initialized = true;
            return Task.CompletedTask;
        }

        public Task RenderFrameAsync(CancellationToken token = default)
        {
            if (!_initialized) throw new InvalidOperationException("Renderer not initialized.");
            // TODO: composite layers and present
            return Task.CompletedTask;
        }

        public Task ResizeAsync(int width, int height, CancellationToken token = default)
        {
            // TODO: recreate backbuffer/resourcs
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            // TODO: cleanup D3D resources and device
        }
    }
}