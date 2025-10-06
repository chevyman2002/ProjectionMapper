using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ProjectionMapper.Rendering
{
    /// <summary>
    /// Renderer abstraction used by the application.
    /// Implementations will provide rendering and emit frames as BitmapSource for the UI host.
    /// Also accepts per-layer frames (submitted by decoders).
    /// </summary>
    public interface IRenderer : IDisposable
    {
        Task InitializeAsync(int width, int height, CancellationToken token = default);
        Task RenderFrameAsync(CancellationToken token = default);
        Task ResizeAsync(int width, int height, CancellationToken token = default);

        /// <summary>
        /// Event fired when a new composed frame is available for display. The BitmapSource will be frozen and safe to use on the UI thread.
        /// </summary>
        event Action<BitmapSource?>? FrameReady;

        /// <summary>
        /// Submit a per-layer frame to the renderer for composition.
        /// layerId: identifier for the layer.
        /// frame: frozen BitmapSource containing the decoded source frame (may be null to indicate no frame).
        /// destRect: destination rectangle (in renderer output coordinates) where the frame should be drawn.
        /// opacity: layer opacity (0..1).
        /// </summary>
        void SubmitLayerFrame(string layerId, BitmapSource? frame, Rect destRect, double opacity);
    }
}