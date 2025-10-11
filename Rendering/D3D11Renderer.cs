// Rendering/D3D11Renderer.cs
// Updated SubmitLayerFrame signature to accept destQuad. GPU implementation is still a placeholder.

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ProjectionMapper.Rendering
{
    public sealed class D3D11Renderer : IRenderer
    {
        private bool _initialized;

        public event Action<BitmapSource?>? FrameReady;

        public D3D11Renderer()
        {
        }

        public Task InitializeAsync(int width, int height, CancellationToken token = default)
        {
            _initialized = true;
            return Task.CompletedTask;
        }

        public Task RenderFrameAsync(CancellationToken token = default)
        {
            // GPU implementation pending; emit no frames for now.
            FrameReady?.Invoke(null);
            return Task.CompletedTask;
        }

        public Task ResizeAsync(int width, int height, CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        // Accept layer frames (placeholder). GPU renderer will handle texture uploads and composition.
        public void SubmitLayerFrame(string layerId, BitmapSource? frame, Rect destRect, Point[]? destQuad, double opacity)
        {
            // no-op in the stub. GPU path will handle destQuad mapping when implemented.
        }

        public void Dispose()
        {
            _initialized = false;
        }
    }
}