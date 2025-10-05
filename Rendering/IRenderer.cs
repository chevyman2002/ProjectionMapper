using System;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectionMapper.Rendering
{
    /// <summary>
    /// Renderer abstraction used by the application.
    /// Implementations will provide GPU-backed rendering and texture upload.
    /// </summary>
    public interface IRenderer : IDisposable
    {
        /// <summary>
        /// Initialize the renderer with a given output size or device index as needed.
        /// Must be safe to call on background thread; actual device creation might require dispatcher depending on API.
        /// </summary>
        Task InitializeAsync(int width, int height, CancellationToken token = default);

        /// <summary>
        /// Render a frame (composite layers, wireframe, etc).
        /// The frame source abstraction is intentionally omitted here and will be defined later.
        /// </summary>
        Task RenderFrameAsync(CancellationToken token = default);

        /// <summary>
        /// Resize the render target.
        /// </summary>
        Task ResizeAsync(int width, int height, CancellationToken token = default);
    }
}