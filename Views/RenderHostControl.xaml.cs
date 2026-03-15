using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Diagnostics;

namespace ProjectionMapper.Views
{
    public partial class RenderHostControl : UserControl, IDisposable
    {
        private bool _disposed;
        private WriteableBitmap? _compositeOverlayBitmap;
        private byte[]? _compositeOverlayPixels;

        public RenderHostControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Draw a coordinate grid on the overlay canvas mapped from renderer pixel coordinates.
        /// spacingPixels: spacing between grid lines in renderer pixel coordinates.
        /// Labels are drawn with the renderer pixel coordinate values for X and Y.
        /// </summary>
        public void SetCoordinateGrid(int spacingPixels)
        {
            if (_disposed) return;
            if (spacingPixels <= 0) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetCoordinateGrid(spacingPixels));
                return;
            }

            PART_Overlay.Children.Clear();

            try
            {
                // Need renderer pixel size to map coordinates
                if (CurrentFrame == null || CurrentFrame.PixelWidth <= 0 || CurrentFrame.PixelHeight <= 0) return;

                double overlayW = PART_Overlay.ActualWidth <= 0 ? PART_Backbuffer.ActualWidth : PART_Overlay.ActualWidth;
                double overlayH = PART_Overlay.ActualHeight <= 0 ? PART_Backbuffer.ActualHeight : PART_Overlay.ActualHeight;
                if (overlayW <= 0 || overlayH <= 0) return;

                double scaleX = overlayW / CurrentFrame.PixelWidth;
                double scaleY = overlayH / CurrentFrame.PixelHeight;

                var lineBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
                lineBrush.Freeze();

                var textBrush = new SolidColorBrush(Colors.White);
                textBrush.Freeze();

                // Vertical lines and labels (X)
                for (int x = 0; x <= CurrentFrame.PixelWidth; x += spacingPixels)
                {
                    double sx = x * scaleX;
                    var line = new System.Windows.Shapes.Line
                    {
                        X1 = sx,
                        Y1 = 0,
                        X2 = sx,
                        Y2 = overlayH,
                        Stroke = lineBrush,
                        StrokeThickness = 1,
                        IsHitTestVisible = false
                    };
                    line.Tag = "Grid";
                    PART_Overlay.Children.Add(line);

                    var tb = new TextBlock
                    {
                        Text = x.ToString(),
                        Foreground = textBrush,
                        FontSize = 12,
                        IsHitTestVisible = false
                    };
                    tb.Tag = "Grid";
                    Canvas.SetLeft(tb, Math.Max(0, sx + 2));
                    Canvas.SetTop(tb, 2);
                    PART_Overlay.Children.Add(tb);
                }

                // Horizontal lines and labels (Y)
                for (int y = 0; y <= CurrentFrame.PixelHeight; y += spacingPixels)
                {
                    double sy = y * scaleY;
                    var line = new System.Windows.Shapes.Line
                    {
                        X1 = 0,
                        Y1 = sy,
                        X2 = overlayW,
                        Y2 = sy,
                        Stroke = lineBrush,
                        StrokeThickness = 1,
                        IsHitTestVisible = false
                    };
                    line.Tag = "Grid";
                    PART_Overlay.Children.Add(line);

                    var tb = new TextBlock
                    {
                        Text = y.ToString(),
                        Foreground = textBrush,
                        FontSize = 12,
                        IsHitTestVisible = false
                    };
                    tb.Tag = "Grid";
                    Canvas.SetLeft(tb, 2);
                    Canvas.SetTop(tb, Math.Max(0, sy + 2));
                    PART_Overlay.Children.Add(tb);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetCoordinateGrid failed: {ex}");
            }
        }

        public static readonly DependencyProperty CurrentFrameProperty = DependencyProperty.Register(
            nameof(CurrentFrame), typeof(BitmapSource), typeof(RenderHostControl), new PropertyMetadata(null));

        public BitmapSource? CurrentFrame
        {
            get => (BitmapSource?)GetValue(CurrentFrameProperty);
            private set => SetValue(CurrentFrameProperty, value);
        }

        /// <summary>
        /// Set a bitmap frame produced by the renderer (BitmapSource must be frozen).
        /// This method is safe to call from the UI thread; calls from background threads will be dispatched.
        /// </summary)
        public void SetFrame(BitmapSource frame)
        {
            if (_disposed) return;
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            if (!frame.IsFrozen)
            {
                // Freeze as a safety (WriteableBitmap from renderer should already be frozen)
                try
                {
                    frame.Freeze();
                }
                catch
                {
                    // If Freeze fails, we'll still try to use the frame on the UI thread.
                }
            }

            if (Dispatcher.CheckAccess())
            {
                PART_Backbuffer.Source = frame;
                CurrentFrame = frame;
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    PART_Backbuffer.Source = frame;
                    CurrentFrame = frame;
                });
            }
        }

        /// <summary>
        /// Clear any displayed frame.
        /// </summary>
        public void Clear()
        {
            if (_disposed) return;
            if (Dispatcher.CheckAccess())
            {
                PART_Backbuffer.Source = null;
                CurrentFrame = null;
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    PART_Backbuffer.Source = null;
                    CurrentFrame = null;
                });
            }
        }

        /// <summary>
        /// Clear any overlay visuals (quad outline/points).
        /// </summary>
        public void ClearOverlay()
        {
            if (_disposed) return;
            if (Dispatcher.CheckAccess()) PART_Overlay.Children.Clear();
            else Dispatcher.Invoke(() => PART_Overlay.Children.Clear());
        }

        /// <summary>
        /// Draw a quad outline and optional points on the overlay canvas.
        /// Coordinates are in renderer pixel space and will be mapped to overlay size which matches control actual size.
        /// This method clears existing mesh overlays and draws only this one - use AddMeshOverlay for multiple overlays.
        /// </summary>
        public void SetMeshOverlay(System.Windows.Point[]? quadPoints, bool showPoints)
        {
            if (_disposed) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetMeshOverlay(quadPoints, showPoints));
                return;
            }

            // Clear only mesh overlays, preserve grid overlays
            ClearMeshOverlay();
            
            if (quadPoints == null || quadPoints.Length < 4) return;

            AddMeshOverlay(quadPoints, showPoints);
        }

        /// <summary>
        /// Add a mesh overlay without clearing existing ones. Allows multiple quad outlines to be displayed simultaneously.
        /// </summary>
        public void AddMeshOverlay(System.Windows.Point[]? quadPoints, bool showPoints)
        {
            if (_disposed) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AddMeshOverlay(quadPoints, showPoints));
                return;
            }

            if (quadPoints == null || quadPoints.Length < 4) return;

            try
            {
                // Map source renderer pixel coordinates to overlay control coordinates
                double scaleX = 1.0, scaleY = 1.0;
                try
                {
                    // prefer mapping using the actual current frame pixel size -> overlay size
                    if (CurrentFrame != null && CurrentFrame.PixelWidth > 0 && CurrentFrame.PixelHeight > 0 && PART_Overlay.ActualWidth > 0 && PART_Overlay.ActualHeight > 0)
                    {
                        scaleX = PART_Overlay.ActualWidth / CurrentFrame.PixelWidth;
                        scaleY = PART_Overlay.ActualHeight / CurrentFrame.PixelHeight;
                    }
                    else if (PART_Backbuffer.ActualWidth > 0 && PART_Backbuffer.ActualHeight > 0 && CurrentFrame != null && CurrentFrame.PixelWidth > 0 && CurrentFrame.PixelHeight > 0)
                    {
                        scaleX = PART_Backbuffer.ActualWidth / CurrentFrame.PixelWidth;
                        scaleY = PART_Backbuffer.ActualHeight / CurrentFrame.PixelHeight;
                    }
                }
                catch { }

                // Draw outline
                var poly = new System.Windows.Shapes.Polygon
                {
                    Stroke = System.Windows.Media.Brushes.White,
                    StrokeThickness = 3,
                    Fill = System.Windows.Media.Brushes.Transparent,
                    IsHitTestVisible = false
                };
                poly.Tag = "Mesh";
                var pts = new PointCollection
                {
                    new System.Windows.Point(quadPoints[0].X * scaleX, quadPoints[0].Y * scaleY),
                    new System.Windows.Point(quadPoints[1].X * scaleX, quadPoints[1].Y * scaleY),
                    new System.Windows.Point(quadPoints[3].X * scaleX, quadPoints[3].Y * scaleY),
                    new System.Windows.Point(quadPoints[2].X * scaleX, quadPoints[2].Y * scaleY)
                };
                poly.Points = pts;
                PART_Overlay.Children.Add(poly);

                if (showPoints)
                {
                    // draw red points at each corner
                    for (int i = 0; i < 4; i++)
                    {
                        var ellipse = new System.Windows.Shapes.Ellipse
                        {
                            Width = 12,
                            Height = 12,
                            Fill = System.Windows.Media.Brushes.Red,
                            Stroke = System.Windows.Media.Brushes.Black,
                            StrokeThickness = 1,
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(ellipse, quadPoints[i].X * scaleX - 6);
                        Canvas.SetTop(ellipse, quadPoints[i].Y * scaleY - 6);
                        ellipse.Tag = "Mesh";
                        PART_Overlay.Children.Add(ellipse);
                    }
                }

                // Adding children to the Canvas automatically triggers the required visual update;
                // calling InvalidateVisual() is redundant and can cause composition artifacts
                // on wireless display adapters (rotation glitches).
            }
            catch { }
        }

        /// <summary>
        /// Remove any grid elements previously drawn on the overlay.
        /// </summary>
        public void ClearGridOverlay()
        {
            if (_disposed) return;
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => ClearGridOverlay()); return; }
            try
            {
                for (int i = PART_Overlay.Children.Count - 1; i >= 0; --i)
                {
                    var child = PART_Overlay.Children[i] as FrameworkElement;
                    if (child != null && child.Tag is string t && t == "Grid") PART_Overlay.Children.RemoveAt(i);
                }
            }
            catch { }
        }

        /// <summary>
        /// Remove any mesh overlay elements previously drawn on the overlay.
        /// </summary>
        public void ClearMeshOverlay()
        {
            if (_disposed) return;
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => ClearMeshOverlay()); return; }
            try
            {
                for (int i = PART_Overlay.Children.Count - 1; i >= 0; --i)
                {
                    var child = PART_Overlay.Children[i] as FrameworkElement;
                    if (child != null && child.Tag is string t && t == "Mesh") PART_Overlay.Children.RemoveAt(i);
                }
            }
            catch { }
        }

        /// <summary>
        /// Render all supplied overlays into a single frozen bitmap and display it via PART_CompositeOverlay.
        /// This avoids modifying the visual tree (no Canvas child add/remove) which prevents
        /// composition artifacts (90° rotation glitches) on wireless display adapters.
        /// Use this for fullscreen monitor windows instead of AddMeshOverlay/ClearMeshOverlay.
        /// </summary>
        public void SetCompositeMeshOverlay(System.Collections.Generic.List<(System.Windows.Point[] QuadPoints, bool ShowPoints)>? overlays)
        {
            if (_disposed) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetCompositeMeshOverlay(overlays));
                return;
            }

            try
            {
                if (overlays == null || overlays.Count == 0)
                {
                    ClearCompositeMeshOverlay();
                    return;
                }

                // Determine the rendering size from the current frame or control size
                double renderW = PART_Backbuffer.ActualWidth;
                double renderH = PART_Backbuffer.ActualHeight;
                if (renderW <= 0 || renderH <= 0)
                {
                    renderW = ActualWidth;
                    renderH = ActualHeight;
                }
                if (renderW <= 0 || renderH <= 0) return;

                // Compute scale from renderer pixel coordinates to control coordinates
                double scaleX = 1.0, scaleY = 1.0;
                if (CurrentFrame != null && CurrentFrame.PixelWidth > 0 && CurrentFrame.PixelHeight > 0)
                {
                    scaleX = renderW / CurrentFrame.PixelWidth;
                    scaleY = renderH / CurrentFrame.PixelHeight;
                }

                // Compute the pixel dimensions of the overlay buffer
                int pixW = (int)Math.Ceiling(renderW);
                int pixH = (int)Math.Ceiling(renderH);
                if (pixW <= 0 || pixH <= 0) return;

                // Allocate once; only reallocate when the render size changes.
                // Never reassign PART_CompositeOverlay.Source after the first assignment — even
                // creating a new RenderTargetBitmap (without assigning it to any visual) allocates
                // a D3D surface via WPF MIL, which causes Miracast adapters to renegotiate the
                // display session and manifests as a 90° rotation of the entire monitor output.
                if (_compositeOverlayBitmap == null ||
                    _compositeOverlayBitmap.PixelWidth != pixW ||
                    _compositeOverlayBitmap.PixelHeight != pixH)
                {
                    _compositeOverlayBitmap = new WriteableBitmap(pixW, pixH, 96, 96, PixelFormats.Pbgra32, null);
                    _compositeOverlayPixels = new byte[pixH * pixW * 4];
                    PART_CompositeOverlay.Source = _compositeOverlayBitmap;
                }

                // Clear to fully transparent (Pbgra32: all-zero bytes = transparent black)
                Array.Clear(_compositeOverlayPixels!, 0, _compositeOverlayPixels!.Length);

                // Rasterize overlay primitives directly into the pixel buffer.
                // This path uses zero D3D/WPF-media allocations, keeping the Miracast session stable.
                foreach (var (quadPoints, showPoints) in overlays)
                {
                    if (quadPoints.Length < 4) continue;

                    // Quad corners: [0]=TL, [1]=TR, [2]=BL, [3]=BR (matching AddMeshOverlay order)
                    var p0 = new System.Windows.Point(quadPoints[0].X * scaleX, quadPoints[0].Y * scaleY);
                    var p1 = new System.Windows.Point(quadPoints[1].X * scaleX, quadPoints[1].Y * scaleY);
                    var p2 = new System.Windows.Point(quadPoints[2].X * scaleX, quadPoints[2].Y * scaleY);
                    var p3 = new System.Windows.Point(quadPoints[3].X * scaleX, quadPoints[3].Y * scaleY);

                    // Draw quad outline: TL→TR, TR→BR, BR→BL, BL→TL — white, 3px thick
                    DrawThickLine(_compositeOverlayPixels!, pixW, pixH, p0, p1, 3, 255, 255, 255);
                    DrawThickLine(_compositeOverlayPixels!, pixW, pixH, p1, p3, 3, 255, 255, 255);
                    DrawThickLine(_compositeOverlayPixels!, pixW, pixH, p3, p2, 3, 255, 255, 255);
                    DrawThickLine(_compositeOverlayPixels!, pixW, pixH, p2, p0, 3, 255, 255, 255);

                    if (showPoints)
                    {
                        foreach (var pt in new[] { p0, p1, p2, p3 })
                        {
                            int cx = (int)Math.Round(pt.X), cy = (int)Math.Round(pt.Y);
                            // Black border (r=7) then red fill (r=6), matching DrawEllipse(red, blackPen, pt, 6, 6)
                            FillCirclePixels(_compositeOverlayPixels!, pixW, pixH, cx, cy, 7, 0, 0, 0);
                            FillCirclePixels(_compositeOverlayPixels!, pixW, pixH, cx, cy, 6, 255, 0, 0);
                        }
                    }
                }

                int stride = pixW * 4;

                _compositeOverlayBitmap.Lock();
                _compositeOverlayBitmap.WritePixels(new Int32Rect(0, 0, pixW, pixH), _compositeOverlayPixels!, stride, 0);
                _compositeOverlayBitmap.Unlock();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetCompositeMeshOverlay failed: {ex}");
            }
        }

        /// <summary>
        /// Clear the composited overlay image by writing transparent pixels.
        /// Avoids reassigning PART_CompositeOverlay.Source which would trigger a new D3D texture
        /// allocation and cause Miracast adapters to rotate the display output.
        /// </summary>
        public void ClearCompositeMeshOverlay()
        {
            if (_disposed) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => ClearCompositeMeshOverlay());
                return;
            }

            if (_compositeOverlayBitmap == null || _compositeOverlayPixels == null) return;

            Array.Clear(_compositeOverlayPixels, 0, _compositeOverlayPixels.Length);
            _compositeOverlayBitmap.Lock();
            _compositeOverlayBitmap.WritePixels(
                new Int32Rect(0, 0, _compositeOverlayBitmap.PixelWidth, _compositeOverlayBitmap.PixelHeight),
                _compositeOverlayPixels,
                _compositeOverlayBitmap.PixelWidth * 4,
                0);
            _compositeOverlayBitmap.Unlock();
        }

        /// <summary>
        /// Pre-allocates the composite overlay bitmap at the given pixel dimensions.
        /// Call this immediately after a fullscreen window is shown so that
        /// PART_CompositeOverlay.Source is never null when the first overlay is drawn.
        /// Assigning Source for the first time changes the Image DesiredSize from (0,0)
        /// to (pixW,pixH) DIPs, triggering a WPF layout pass that can cause Miracast
        /// adapters to renegotiate the display session (manifesting as a 90° rotation).
        /// Pre-allocating here ensures that Source is already set before any overlay refresh
        /// fires, so the first SetCompositeMeshOverlay call skips the Source reassignment.
        /// </summary>
        public void InitializeCompositeOverlay(int pixW, int pixH)
        {
            if (_disposed) return;
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => InitializeCompositeOverlay(pixW, pixH)); return; }
            if (pixW <= 0 || pixH <= 0) return;
            if (_compositeOverlayBitmap != null &&
                _compositeOverlayBitmap.PixelWidth == pixW &&
                _compositeOverlayBitmap.PixelHeight == pixH) return;
            _compositeOverlayBitmap = new WriteableBitmap(pixW, pixH, 96, 96, PixelFormats.Pbgra32, null);
            _compositeOverlayPixels = new byte[pixH * pixW * 4];
            PART_CompositeOverlay.Source = _compositeOverlayBitmap;
        }

        /// <summary>
        /// When true, the backbuffer image will use Stretch.Fill to occupy the host control fully (useful for fullscreen windows).
        /// When false, it will use the default Stretch.Uniform to preserve aspect ratio for previews.
        /// Safe to call from any thread.
        /// </summary>
        public void SetFullscreenStretch(bool fullscreen)
        {
            if (_disposed) return;
            if (Dispatcher.CheckAccess())
            {
                PART_Backbuffer.Stretch = fullscreen ? Stretch.Fill : Stretch.Uniform;
            }
            else
            {
                Dispatcher.Invoke(() => PART_Backbuffer.Stretch = fullscreen ? Stretch.Fill : Stretch.Uniform);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _compositeOverlayBitmap = null;
            _compositeOverlayPixels = null;
            Clear();
        }

        /// <summary>
        /// Draws a thick line into a Pbgra32 pixel buffer using a circular stamp brush.
        /// Clips the segment to buffer bounds before iterating to prevent O(N) blocking
        /// when mesh points are out of bounds (N can exceed 30,000 for extreme coordinates,
        /// freezing the UI thread and causing Miracast adapters to rotate the display output).
        /// </summary>
        private static void DrawThickLine(
            byte[] buffer, int w, int h,
            System.Windows.Point p0, System.Windows.Point p1,
            int thickness, byte r, byte g, byte b)
        {
            // Clip to [0,w-1]×[0,h-1] BEFORE computing step count.
            if (!ClipLineToBounds(ref p0, ref p1, w, h)) return;

            double dx = p1.X - p0.X, dy = p1.Y - p0.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            int steps = Math.Max(1, (int)Math.Ceiling(len) + 1);
            double stepX = dx / steps, stepY = dy / steps;
            int radius = thickness / 2;

            for (int s = 0; s <= steps; s++)
            {
                int cx = (int)Math.Round(p0.X + stepX * s);
                int cy = (int)Math.Round(p0.Y + stepY * s);
                FillCirclePixels(buffer, w, h, cx, cy, radius, r, g, b);
            }
        }

        /// <summary>
        /// Liang-Barsky line clipping: clips segment (p0, p1) to the rectangle [0, w-1] x [0, h-1].
        /// Returns false if the segment lies entirely outside the clipping region.
        /// </summary>
        private static bool ClipLineToBounds(ref System.Windows.Point p0, ref System.Windows.Point p1, int w, int h)
        {
            double x0 = p0.X, y0 = p0.Y, x1 = p1.X, y1 = p1.Y;
            double dx = x1 - x0, dy = y1 - y0;
            double t0 = 0.0, t1 = 1.0;

            if (!ClipParam(-dx, x0, ref t0, ref t1)) return false;
            if (!ClipParam(dx, (w - 1) - x0, ref t0, ref t1)) return false;
            if (!ClipParam(-dy, y0, ref t0, ref t1)) return false;
            if (!ClipParam(dy, (h - 1) - y0, ref t0, ref t1)) return false;
            if (t1 < t0) return false;

            p1 = new System.Windows.Point(x0 + t1 * dx, y0 + t1 * dy);
            p0 = new System.Windows.Point(x0 + t0 * dx, y0 + t0 * dy);
            return true;
        }

        /// <summary>Liang-Barsky parameter update helper.</summary>
        private static bool ClipParam(double p, double q, ref double t0, ref double t1)
        {
            if (p == 0.0) return q >= 0.0;
            double r = q / p;
            if (p < 0.0) { if (r > t1) return false; if (r > t0) t0 = r; }
            else { if (r < t0) return false; if (r < t1) t1 = r; }
            return true;
        }

        /// <summary>Fills a solid circle into a Pbgra32 pixel buffer.</summary>
        private static void FillCirclePixels(
            byte[] buffer, int w, int h,
            int cx, int cy, int radius, byte r, byte g, byte b)
        {
            int r2 = radius * radius;

            for (int oy = -radius; oy <= radius; oy++)
            {
                int py = cy + oy;
                if (py < 0 || py >= h) continue;

                for (int ox = -radius; ox <= radius; ox++)
                {
                    if (ox * ox + oy * oy > r2) continue;

                    int px = cx + ox;
                    if (px < 0 || px >= w) continue;

                    int idx = (py * w + px) * 4;
                    buffer[idx] = b;       // B (Pbgra32 layout)
                    buffer[idx + 1] = g;   // G
                    buffer[idx + 2] = r;   // R
                    buffer[idx + 3] = 255; // A (fully opaque)
                }
            }
        }
    }
}