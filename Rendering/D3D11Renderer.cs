using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProjectionMapper.Rendering
{
    /// <summary>
    /// D3D11 renderer stub that implements the IRenderer contract including SubmitLayerFrame.
    /// GPU implementation will be added later. For now, methods are no-op and SubmitLayerFrame is a placeholder.
    /// </summary>
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
            if (!_initialized) throw new InvalidOperationException("Renderer not initialized.");
            // GPU path would present the current backbuffer here.
            FrameReady?.Invoke(null);
            return Task.CompletedTask;
        }

        public Task ResizeAsync(int width, int height, CancellationToken token = default)
        {
            if (!_initialized) throw new InvalidOperationException("Renderer not initialized.");
            return Task.CompletedTask;
        }

        // Accept layer frames (placeholder). GPU renderer will handle texture uploads and composition.
        public void SubmitLayerFrame(string layerId, BitmapSource? frame, Rect destRect, double opacity, Geometry? clip = null)
        {
            // no-op in the stub
        }

        public void Dispose()
        {
            _initialized = false;
        }
    }
}