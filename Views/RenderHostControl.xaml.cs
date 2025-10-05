using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ProjectionMapper.Views
{
    /// <summary>
    /// Simple host control that exposes a D3DImage-backed Image surface.
    /// This class intentionally remains lightweight: it provides the D3DImage instance and a small surface-invalidation API.
    ///
    /// Note: actual Direct3D texture sharing/upload will be implemented in the D3D11InteropHelper and renderer classes.
    /// D3DImage is retained for broad compatibility with WPF. If we later migrate to WinUI/SwapChainPanel, this control is a good integration point.
    /// </summary>
    public partial class RenderHostControl : UserControl, IDisposable
    {
        private readonly D3DImage _d3dImage;
        private bool _disposed;

        public RenderHostControl()
        {
            InitializeComponent();

            // Create the D3DImage instance that will host shared D3D textures.
            _d3dImage = new D3DImage();
            PART_Backbuffer.Source = _d3dImage;

            // Optionally hook CompositionTarget.Rendering to drive a simple render loop if the app desires that approach.
            // We prefer a dedicated RenderLoop class (separate thread) for timing/latency control; this hook is optional.
        }

        /// <summary>
        /// Sets the backbuffer handle (shared surface) on the D3DImage.
        /// The renderer will call this when a new IDXGISurface/texture is available for presentation.
        /// The handle is a pointer (IntPtr) obtained from the native interop layer.
        /// </summary>
        public void SetBackBuffer(IntPtr nativeDxSurfacePtr)
        {
            if (_disposed) return;

            if (nativeDxSurfacePtr == IntPtr.Zero)
            {
                // Clear backbuffer
                _d3dImage.Dispatcher.Invoke(() => _d3dImage.Lock());
                try
                {
                    _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
                }
                finally
                {
                    _d3dImage.Unlock();
                }

                return;
            }

            // IMPORTANT: the native pointer must be a pointer to a D3D9-compatible surface when using D3DImage.
            // In modern code paths we'll use shared textures + D3D9-on-D3D11 interop or a proper WPF swapchain host.
            _d3dImage.Dispatcher.Invoke(() =>
            {
                _d3dImage.Lock();
                try
                {
                    _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, nativeDxSurfacePtr);
                    _d3dImage.AddDirtyRect(new Int32Rect(0, 0, _d3dImage.PixelWidth, _d3dImage.PixelHeight));
                }
                finally
                {
                    _d3dImage.Unlock();
                }
            });
        }

        /// <summary>
        /// Invalidate the host so WPF repaints the current backbuffer contents.
        /// Call this after updating the D3DImage content (from the UI thread or via Dispatcher).
        /// </summary>
        public void InvalidateBackBuffer()
        {
            if (_disposed) return;
            _d3dImage.Dispatcher.Invoke(() =>
            {
                _d3dImage.Lock();
                try
                {
                    _d3dImage.AddDirtyRect(new Int32Rect(0, 0, Math.Max(1, _d3dImage.PixelWidth), Math.Max(1, _d3dImage.PixelHeight)));
                }
                finally
                {
                    _d3dImage.Unlock();
                }
            }, DispatcherPriority.Render);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _d3dImage.Dispatcher.Invoke(() =>
            {
                _d3dImage.Lock();
                try
                {
                    _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
                    PART_Backbuffer.Source = null;
                }
                finally
                {
                    _d3dImage.Unlock();
                }
            });
        }
    }
}