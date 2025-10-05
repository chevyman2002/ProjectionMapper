using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProjectionMapper.Rendering
{
    /// <summary>
    /// Simple software renderer used as a compatibility fallback while the D3D11 path is implemented.
    /// It produces a simple animated test pattern into a WriteableBitmap and raises FrameReady.
    /// This lets the UI and RenderHostControl be exercised without native D3D dependencies.
    /// </summary>
    public sealed class SoftwareRenderer : IRenderer
    {
        private int _width;
        private int _height;
        private bool _initialized;
        private int _frameCounter;
        private readonly object _sync = new();
        private readonly PixelFormat _format = PixelFormats.Bgra32;

        public event Action<BitmapSource?>? FrameReady;

        public void Dispose()
        {
            // Nothing to dispose for the managed software renderer
        }

        public Task InitializeAsync(int width, int height, CancellationToken token = default)
        {
            lock (_sync)
            {
                _width = Math.Max(1, width);
                _height = Math.Max(1, height);
                _frameCounter = 0;
                _initialized = true;
            }

            return Task.CompletedTask;
        }

        public Task ResizeAsync(int width, int height, CancellationToken token = default)
        {
            lock (_sync)
            {
                _width = Math.Max(1, width);
                _height = Math.Max(1, height);
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Render a single frame synchronously (but returns a completed Task). Generates a simple animated color grid.
        /// The produced BitmapSource is frozen before being raised via FrameReady so the UI thread may use it directly.
        /// </summary>
        public Task RenderFrameAsync(CancellationToken token = default)
        {
            if (!_initialized) throw new InvalidOperationException("Renderer not initialized.");

            int w, h, frame;
            lock (_sync)
            {
                w = _width;
                h = _height;
                frame = ++_frameCounter;
            }

            try
            {
                // Create a WriteableBitmap and fill pixel buffer
                var wb = new WriteableBitmap(w, h, 96, 96, _format, null);

                // Use a byte array of BGRA32
                int stride = (w * wb.Format.BitsPerPixel + 7) / 8;
                var pixels = new byte[stride * h];

                // Simple animated pattern: moving color bands + checker overlay
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * stride + x * 4;
                        byte b = (byte)((x + frame) % 256);
                        byte g = (byte)((y + frame / 2) % 256);
                        byte r = (byte)(((x + y) / 2 + frame / 3) % 256);

                        // Add a subtle checker
                        bool checker = (((x / 32) + (y / 32)) & 1) == 0;
                        if (!checker)
                        {
                            r = (byte)(r * 0.5);
                            g = (byte)(g * 0.5);
                            b = (byte)(b * 0.5);
                        }

                        pixels[idx + 0] = b;
                        pixels[idx + 1] = g;
                        pixels[idx + 2] = r;
                        pixels[idx + 3] = 255; // alpha
                    }
                }

                // Write pixels into the WriteableBitmap
                wb.Lock();
                try
                {
                    wb.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
                }
                finally
                {
                    wb.Unlock();
                }

                // Freeze to make it cross-thread-safe for WPF consumption
                wb.Freeze();

                // Fire event
                FrameReady?.Invoke(wb);
            }
            catch (OperationCanceledException) { FrameReady?.Invoke(null); }
            catch (Exception)
            {
                // On error, notify with null so the host can react (or ignore)
                FrameReady?.Invoke(null);
            }

            return Task.CompletedTask;
        }
    }
}