using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ProjectionMapper.Rendering
{
    /// <summary>
    /// Renderer abstraction used by the application.
    /// Implementations will provide rendering and emit frames as BitmapSource for the UI host.
    /// </summary>
    public interface IRenderer : IDisposable
    {
        /// <summary>
        /// Initialize the renderer with a given output size or device index as needed.
        /// </summary>
        Task InitializeAsync(int width, int height, CancellationToken token = default);

        /// <summary>
        /// Render a frame (composite layers, wireframe, etc).
        /// </summary>
        Task RenderFrameAsync(CancellationToken token = default);

        /// <summary>
        /// Resize the render target.
        /// </summary>
        Task ResizeAsync(int width, int height, CancellationToken token = default);

        /// <summary>
        /// Event fired when a new frame is available for display. The BitmapSource will be frozen and safe to use on the UI thread.
        /// </summary>
        event Action<BitmapSource?>? FrameReady;
    }
}