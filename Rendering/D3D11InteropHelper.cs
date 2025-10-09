using System;

namespace ProjectionMapper.Rendering
{
    /// <summary>
    /// D3D11InteropHelper is a focused place for D3D device and shared texture creation.
    ///
    /// This helper is intentionally minimal and only defines the API surface we will implement with Vortice.Windows (or another D3D interop library).
    /// Implementation notes (for later):
    /// - Create a ID3D11Device/ID3D11DeviceContext with BGRA support and DXGI swapchain support.
    /// - To feed WPF via D3DImage we historically need a D3D9 surface backed by a shared D3D11 texture (use D3D9On12 or D3D9Ex interop).
    /// - Alternatively, create a Shared Handle (IDXGIResource->GetSharedHandle) and use a D3D9 surface created from that handle.
    /// - Vortice.Windows provides the necessary wrappers; keep all native resources on a dedicated renderer thread.
    /// </summary>
    public sealed class D3D11InteropHelper : IDisposable
    {
        // Placeholder device/context handles (to be implemented with Vortice)
        // Example fields (commented out to avoid compile-time dependency until implemented):
        // private ID3D11Device? _device;
        // private ID3D11DeviceContext? _context;

        public D3D11InteropHelper()
        {
            // TODO: create D3D11 device with BGRA support and appropriate flags for shared resources.
        }

        /// <summary>
        /// Create a render target texture of the requested size and return a native handle suitable for WPF/D3DImage or swapchain presentation.
        /// The returned IntPtr is a native handle (e.g., a D3D9 surface pointer or shared handle) that the host control can use.
        /// </summary>
        public IntPtr CreateSharedRenderTarget(int width, int height)
        {
            // TODO: allocate GPU texture, create shared handle or D3D9 wrapper surface and return pointer.
            return IntPtr.Zero;
        }

        /// <summary>
        /// Release a previously created render target handle.
        /// </summary>
        public void ReleaseSharedRenderTarget(IntPtr handle)
        {
            // TODO: free resources associated with the handle
        }

        public void Dispose()
        {
            // TODO: release device/context
        }
    }
}