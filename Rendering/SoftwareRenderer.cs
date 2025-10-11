using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectionMapper.Models;

namespace ProjectionMapper.Rendering
{
    /// <summary>
    /// Simple software renderer used as a compatibility fallback while the D3D11 path is implemented.
    /// It maintains per-layer latest frames and composes them into the final output using WPF DrawingVisual + RenderTargetBitmap.
    /// </summary>
    public sealed class SoftwareRenderer : IRenderer
    {
        private int _width;
        private int _height;
        private bool _initialized;

        // LayerId -> (frame, destRect, opacity, clip)
        private readonly ConcurrentDictionary<string, (BitmapSource? Frame, Rect DestRect, double Opacity, Geometry? Clip)> _layers
            = new();

        public event Action<BitmapSource?>? FrameReady;

        public void Dispose()
        {
            // Nothing to dispose
        }

        public Task InitializeAsync(int width, int height, CancellationToken token = default)
        {
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            _initialized = true;
            return Task.CompletedTask;
        }

        public Task ResizeAsync(int width, int height, CancellationToken token = default)
        {
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            return Task.CompletedTask;
        }

        public Task RenderFrameAsync(CancellationToken token = default)
        {
            if (!_initialized) throw new InvalidOperationException("Renderer not initialized.");

            // Use the UI dispatcher to do WPF-based composition via DrawingVisual -> RenderTargetBitmap.
            // Composition must be done on the UI thread because RenderTargetBitmap uses PresentationCore resources.
            Application.Current?.Dispatcher.Invoke(() =>
            {
                try
                {
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        // Clear background (black)
                        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, _width, _height));

                        // Sort layers by some order - here we compose in registration order (dictionary order is arbitrary),
                        // so prefer ordering by key to be deterministic. In a full app we would keep explicit z-order.
                        foreach (var kv in _layers.OrderBy(k => k.Key))
                        {
                            var entry = kv.Value;
                            if (entry.Frame == null) continue;
                            var frame = entry.Frame;

                            // Push opacity
                            if (entry.Opacity < 0.999)
                            {
                                dc.PushOpacity(entry.Opacity);
                                if (entry.Clip != null)
                                {
                                    dc.PushClip(entry.Clip);
                                    dc.DrawImage(frame, entry.DestRect);
                                    dc.Pop();
                                }
                                else
                                {
                                    dc.DrawImage(frame, entry.DestRect);
                                }
                                dc.Pop();
                            }
                            else
                            {
                                if (entry.Clip != null)
                                {
                                    dc.PushClip(entry.Clip);
                                    dc.DrawImage(frame, entry.DestRect);
                                    dc.Pop();
                                }
                                else
                                {
                                    dc.DrawImage(frame, entry.DestRect);
                                }
                            }
                        }
                    }

                    var rtb = new RenderTargetBitmap(_width, _height, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(dv);
                    rtb.Freeze();
                    FrameReady?.Invoke(rtb);
                }
                catch (Exception)
                {
                    // On failure, notify null (host may clear)
                    FrameReady?.Invoke(null);
                }
            });

            return Task.CompletedTask;
        }

        /// <summary>
        /// Accept a layer frame for later composition.
        /// </summary>
        public void SubmitLayerFrame(string layerId, BitmapSource? frame, Rect destRect, double opacity, Geometry? clip = null)
        {
            _layers[layerId] = (frame, destRect, opacity, clip);
        }
    }
}