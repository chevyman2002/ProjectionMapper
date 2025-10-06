using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ProjectionMapper.Rendering
{
    /// <summary>
    /// D3D11 renderer stub that implements the IRenderer contract including the FrameReady event.
    /// This is a stub so the project builds; later this class will be implemented with Vortice.Windows
    /// (or another Direct3D binding) to create the device, upload textures and present frames.
    /// </summary>
    public sealed class D3D11Renderer : IRenderer
    {
        private bool _initialized;

        // Event from IRenderer to notify when a new frame (BitmapSource) is available for display.
        // GPU-backed renderers will either provide a BitmapSource (software fallback) or notify with null
        // and use a different presentation path (D3DImage / shared handle). Keeping this event here
        // keeps the same integration pattern as SoftwareRenderer.
        public event Action<BitmapSource?>? FrameReady;

        public D3D11Renderer()
        {
        }

        public Task InitializeAsync(int width, int height, CancellationToken token = default)
        {
            // TODO: create D3D11 device, context, swapchain/backbuffer/shared texture on a dedicated renderer thread.
            _initialized = true;
            return Task.CompletedTask;
        }

        public Task RenderFrameAsync(CancellationToken token = default)
        {
            if (!_initialized) throw new InvalidOperationException("Renderer not initialized.");

            // TODO: composite layers, upload textures, render into the backbuffer, and present.
            // For now we raise FrameReady with null so the host knows a frame cycle occurred.
            // The RendererManager/RenderHostControl will handle nulls gracefully (clearing or ignoring as needed).
            FrameReady?.Invoke(null);

            return Task.CompletedTask;
        }

        public Task ResizeAsync(int width, int height, CancellationToken token = default)
        {
            if (!_initialized) throw new InvalidOperationException("Renderer not initialized.");

            // TODO: recreate render targets/backbuffers as necessary for the new size.
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (!_initialized) return;

            // TODO: release D3D resources (device, context, textures, shared handles).
            _initialized = false;
        }
    }
}