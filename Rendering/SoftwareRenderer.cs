// Rendering/SoftwareRenderer.cs
// Implement quad-warp support: SubmitLayerFrame accepts an optional destQuad (4 points).
// If present, the renderer will warp the provided frame to the provided quadrilateral using an inverse-homography
// and bilinear sampling. The implementation targets correctness and maintainability.
// Error handling and logging added.

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ProjectionMapper.Rendering
{
    public sealed class SoftwareRenderer : IRenderer
    {
        private int _width;
        private int _height;
        private bool _initialized;

        // LayerId -> (frame, destRect, destQuad, opacity)
        private readonly ConcurrentDictionary<string, (BitmapSource? Frame, Rect DestRect, Point[]? DestQuad, double Opacity)> _layers
            = new();

        public event Action<BitmapSource?>? FrameReady;

        public void Dispose()
        {
            _initialized = false;
            _layers.Clear();
        }

        public Task InitializeAsync(int width, int height, CancellationToken token = default)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            _width = width; _height = height; _initialized = true;
            return Task.CompletedTask;
        }

        public Task ResizeAsync(int width, int height, CancellationToken token = default)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            _width = width; _height = height; return Task.CompletedTask;
        }

        /// <summary>
        /// Accept a layer frame for later composition. Optional destQuad warps frame to a quad on output.
        /// </summary>
        public void SubmitLayerFrame(string layerId, BitmapSource? frame, Rect destRect, Point[]? destQuad, double opacity)
        {
            if (string.IsNullOrEmpty(layerId)) return;
            _layers[layerId] = (frame, destRect, destQuad, opacity);
        }

        public Task RenderFrameAsync(CancellationToken token = default)
        {
            if (!_initialized) return Task.CompletedTask;

            try
            {
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    // clear background - transparent by default
                    dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, _width, _height));

                    // Compose layers by key order for determinism
                    foreach (var kv in _layers.OrderBy(k => k.Key))
                    {
                        var entry = kv.Value;
                        if (entry.Frame == null) continue;
                        var frame = entry.Frame;

                        if (entry.DestQuad != null && entry.DestQuad.Length >= 4)
                        {
                            try
                            {
                                // Warp into bounding box and draw
                                var quad = entry.DestQuad;
                                // compute integer bounding box
                                int minX = (int)Math.Floor(Math.Max(0.0, Math.Min(Math.Min(quad[0].X, quad[1].X), Math.Min(quad[2].X, quad[3].X))));
                                int minY = (int)Math.Floor(Math.Max(0.0, Math.Min(Math.Min(quad[0].Y, quad[1].Y), Math.Min(quad[2].Y, quad[3].Y))));
                                int maxX = (int)Math.Ceiling(Math.Min(_width, Math.Max(Math.Max(quad[0].X, quad[1].X), Math.Max(quad[2].X, quad[3].X))));
                                int maxY = (int)Math.Ceiling(Math.Min(_height, Math.Max(Math.Max(quad[0].Y, quad[1].Y), Math.Max(quad[2].Y, quad[3].Y))));

                                int w = Math.Max(1, maxX - minX);
                                int h = Math.Max(1, maxY - minY);

                                var warped = WarpBitmapToQuad(frame, quad, minX, minY, w, h);
                                if (warped != null)
                                {
                                    var dest = new Rect(minX, minY, w, h);
                                    // apply opacity
                                    if (entry.Opacity < 0.999)
                                    {
                                        dc.PushOpacity(entry.Opacity);
                                        dc.DrawImage(warped, dest);
                                        dc.Pop();
                                    }
                                    else
                                    {
                                        dc.DrawImage(warped, dest);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"SoftwareRenderer: warp draw failed for layer {kv.Key}: {ex}");
                                // fallback to rect drawing below
                                if (entry.Opacity < 0.999)
                                {
                                    dc.PushOpacity(entry.Opacity);
                                    dc.DrawImage(frame, entry.DestRect);
                                    dc.Pop();
                                }
                                else
                                {
                                    dc.DrawImage(frame, entry.DestRect);
                                }
                            }
                        }
                        else
                        {
                            // default path: draw as rectangle
                            if (entry.Opacity < 0.999)
                            {
                                dc.PushOpacity(entry.Opacity);
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

                // Render to bitmap and notify
                var rtb = new RenderTargetBitmap(_width, _height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                try { rtb.Freeze(); } catch { }
                FrameReady?.Invoke(rtb);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SoftwareRenderer.RenderFrameAsync failed: {ex}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Warp the given source BitmapSource to the provided quad (in renderer coordinates).
        /// We compute a homography mapping (u,v) in [0,1]x[0,1] of source texture to quad and perform inverse mapping.
        /// Returns a BitmapSource sized to width*height (the bounding box size).
        /// top-left of returned bitmap corresponds to (bboxX,bboxY) in renderer coordinates.
        /// </summary>
        private BitmapSource? WarpBitmapToQuad(BitmapSource src, Point[] quad, int bboxX, int bboxY, int bboxW, int bboxH)
        {
            if (src == null) return null;
            if (quad == null || quad.Length < 4) return null;
            try
            {
                // Convert source to Bgra32 for sampling
                BitmapSource src32 = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                int srcW = src32.PixelWidth, srcH = src32.PixelHeight;
                int srcStride = srcW * 4;
                var srcPixels = new byte[srcH * srcStride];
                src32.CopyPixels(srcPixels, srcStride, 0);

                // Build homography H that maps (u,v,1) -> (x,y,1), where src (u,v) = (0,0),(1,0),(0,1),(1,1)
                // dst points are quad[0..3] in same order.
                var dst = quad;
                double[,] H = ComputeHomography(new double[,] {
                    {0,0}, {1,0}, {0,1}, {1,1}
                }, new double[,] {
                    {dst[0].X, dst[0].Y}, {dst[1].X, dst[1].Y}, {dst[2].X, dst[2].Y}, {dst[3].X, dst[3].Y}
                });

                if (H == null) return null;

                // invert H
                double[,] Hinv = Invert3x3(H);
                if (Hinv == null) return null;

                // Prepare output pixel buffer
                int outW = bboxW, outH = bboxH;
                var outPixels = new byte[outW * outH * 4];

                // For each destination pixel, compute source uv via inverse homography and sample
                Parallel.For(0, outH, y =>
                {
                    try
                    {
                        int rowStart = y * outW * 4;
                        double py = bboxY + y + 0.5; // center
                        for (int x = 0; x < outW; x++)
                        {
                            double px = bboxX + x + 0.5; // center

                            // compute src homogeneous coord:
                            double sx_num = Hinv[0, 0] * px + Hinv[0, 1] * py + Hinv[0, 2];
                            double sy_num = Hinv[1, 0] * px + Hinv[1, 1] * py + Hinv[1, 2];
                            double sw = Hinv[2, 0] * px + Hinv[2, 1] * py + Hinv[2, 2];
                            if (Math.Abs(sw) < 1e-8) continue;
                            double u = sx_num / sw;
                            double v = sy_num / sw;

                            // u,v should be in [0,1] to sample source
                            if (u < -0.01 || u > 1.01 || v < -0.01 || v > 1.01) continue;

                            // map to source pixel coords
                            double fx = u * (srcW - 1);
                            double fy = v * (srcH - 1);

                            // bilinear sample
                            int x0 = (int)Math.Floor(fx);
                            int y0 = (int)Math.Floor(fy);
                            int x1 = x0 + 1;
                            int y1 = y0 + 1;
                            double tx = fx - x0;
                            double ty = fy - y0;

                            x0 = Math.Max(0, Math.Min(srcW - 1, x0));
                            x1 = Math.Max(0, Math.Min(srcW - 1, x1));
                            y0 = Math.Max(0, Math.Min(srcH - 1, y0));
                            y1 = Math.Max(0, Math.Min(srcH - 1, y1));

                            int idx00 = (y0 * srcStride) + (x0 * 4);
                            int idx10 = (y0 * srcStride) + (x1 * 4);
                            int idx01 = (y1 * srcStride) + (x0 * 4);
                            int idx11 = (y1 * srcStride) + (x1 * 4);

                            // Read BGRA components
                            double b00 = srcPixels[idx00 + 0];
                            double g00 = srcPixels[idx00 + 1];
                            double r00 = srcPixels[idx00 + 2];
                            double a00 = srcPixels[idx00 + 3];

                            double b10 = srcPixels[idx10 + 0];
                            double g10 = srcPixels[idx10 + 1];
                            double r10 = srcPixels[idx10 + 2];
                            double a10 = srcPixels[idx10 + 3];

                            double b01 = srcPixels[idx01 + 0];
                            double g01 = srcPixels[idx01 + 1];
                            double r01 = srcPixels[idx01 + 2];
                            double a01 = srcPixels[idx01 + 3];

                            double b11 = srcPixels[idx11 + 0];
                            double g11 = srcPixels[idx11 + 1];
                            double r11 = srcPixels[idx11 + 2];
                            double a11 = srcPixels[idx11 + 3];

                            // bilinear interp
                            double b0 = b00 * (1 - tx) + b10 * tx;
                            double g0 = g00 * (1 - tx) + g10 * tx;
                            double r0 = r00 * (1 - tx) + r10 * tx;
                            double a0 = a00 * (1 - tx) + a10 * tx;

                            double b1 = b01 * (1 - tx) + b11 * tx;
                            double g1 = g01 * (1 - tx) + g11 * tx;
                            double r1 = r01 * (1 - tx) + r11 * tx;
                            double a1 = a01 * (1 - tx) + a11 * tx;

                            double bf = b0 * (1 - ty) + b1 * ty;
                            double gf = g0 * (1 - ty) + g1 * ty;
                            double rf = r0 * (1 - ty) + r1 * ty;
                            double af = a0 * (1 - ty) + a1 * ty;

                            int outIdx = rowStart + x * 4;
                            outPixels[outIdx + 0] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(bf)));
                            outPixels[outIdx + 1] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(gf)));
                            outPixels[outIdx + 2] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(rf)));
                            outPixels[outIdx + 3] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(af)));
                        }
                    }
                    catch (Exception exRow)
                    {
                        Debug.WriteLine($"WarpBitmapToQuad row processing failed: {exRow}");
                    }
                });

                // Create WriteableBitmap and write pixels
                var wb = new WriteableBitmap(outW, outH, src.DpiX, src.DpiY, PixelFormats.Bgra32, null);
                wb.WritePixels(new Int32Rect(0, 0, outW, outH), outPixels, outW * 4, 0);
                try { wb.Freeze(); } catch { }
                return wb;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WarpBitmapToQuad failed: {ex}");
                return null;
            }
        }

        // Compute homography matrix H (3x3) mapping srcPts (4x2) to dstPts (4x2).
        // srcPts and dstPts are provided as 4x2 double[,] arrays in same order.
        // Returns 3x3 matrix H or null on failure.
        private static double[,]? ComputeHomography(double[,] srcPts, double[,] dstPts)
        {
            try
            {
                // Build 8x8 system A * h = b (h = 8 unknowns, h9 = 1)
                var A = new double[8, 8];
                var b = new double[8];

                for (int i = 0; i < 4; i++)
                {
                    double u = srcPts[i, 0];
                    double v = srcPts[i, 1];
                    double x = dstPts[i, 0];
                    double y = dstPts[i, 1];

                    // Row for x
                    A[2 * i, 0] = u;
                    A[2 * i, 1] = v;
                    A[2 * i, 2] = 1;
                    A[2 * i, 3] = 0;
                    A[2 * i, 4] = 0;
                    A[2 * i, 5] = 0;
                    A[2 * i, 6] = -u * x;
                    A[2 * i, 7] = -v * x;
                    b[2 * i] = x;

                    // Row for y
                    A[2 * i + 1, 0] = 0;
                    A[2 * i + 1, 1] = 0;
                    A[2 * i + 1, 2] = 0;
                    A[2 * i + 1, 3] = u;
                    A[2 * i + 1, 4] = v;
                    A[2 * i + 1, 5] = 1;
                    A[2 * i + 1, 6] = -u * y;
                    A[2 * i + 1, 7] = -v * y;
                    b[2 * i + 1] = y;
                }

                var h = SolveLinearSystem(A, b);
                if (h == null) return null;

                var H = new double[3, 3];
                H[0, 0] = h[0]; H[0, 1] = h[1]; H[0, 2] = h[2];
                H[1, 0] = h[3]; H[1, 1] = h[4]; H[1, 2] = h[5];
                H[2, 0] = h[6]; H[2, 1] = h[7]; H[2, 2] = 1.0;
                return H;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ComputeHomography failed: {ex}");
                return null;
            }
        }

        // Solve linear system A (n x n) * x = b (n) using Gaussian elimination with partial pivoting.
        // Returns x or null on failure.
        private static double[]? SolveLinearSystem(double[,] A, double[] b)
        {
            int n = b.Length;
            if (A.GetLength(0) != n || A.GetLength(1) != n) return null;

            // Create augmented matrix
            var M = new double[n, n + 1];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) M[i, j] = A[i, j];
                M[i, n] = b[i];
            }

            for (int k = 0; k < n; k++)
            {
                // Find pivot
                int maxRow = k;
                double maxVal = Math.Abs(M[k, k]);
                for (int i = k + 1; i < n; i++)
                {
                    double v = Math.Abs(M[i, k]);
                    if (v > maxVal) { maxVal = v; maxRow = i; }
                }

                if (Math.Abs(M[maxRow, k]) < 1e-12) return null;

                // Swap rows
                if (maxRow != k)
                {
                    for (int j = k; j < n + 1; j++)
                    {
                        double tmp = M[k, j];
                        M[k, j] = M[maxRow, j];
                        M[maxRow, j] = tmp;
                    }
                }

                // Normalize pivot row
                double pivot = M[k, k];
                for (int j = k; j < n + 1; j++) M[k, j] /= pivot;

                // Eliminate
                for (int i = 0; i < n; i++)
                {
                    if (i == k) continue;
                    double factor = M[i, k];
                    if (Math.Abs(factor) < 1e-15) continue;
                    for (int j = k; j < n + 1; j++)
                    {
                        M[i, j] -= factor * M[k, j];
                    }
                }
            }

            var x = new double[n];
            for (int i = 0; i < n; i++) x[i] = M[i, n];
            return x;
        }

        // Invert a 3x3 matrix
        private static double[,]? Invert3x3(double[,] m)
        {
            try
            {
                double a = m[0, 0], b = m[0, 1], c = m[0, 2];
                double d = m[1, 0], e = m[1, 1], f = m[1, 2];
                double g = m[2, 0], h = m[2, 1], i = m[2, 2];

                double A = e * i - f * h;
                double B = -(d * i - f * g);
                double C = d * h - e * g;
                double D = -(b * i - c * h);
                double E = a * i - c * g;
                double F = -(a * h - b * g);
                double G = b * f - c * e;
                double H = -(a * f - c * d);
                double I = a * e - b * d;

                double det = a * A + b * B + c * C;
                if (Math.Abs(det) < 1e-12) return null;

                var inv = new double[3, 3];
                inv[0, 0] = A / det; inv[0, 1] = D / det; inv[0, 2] = G / det;
                inv[1, 0] = B / det; inv[1, 1] = E / det; inv[1, 2] = H / det;
                inv[2, 0] = C / det; inv[2, 1] = F / det; inv[2, 2] = I / det;
                return inv;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Invert3x3 failed: {ex}");
                return null;
            }
        }
    }
}