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

                // Force redraw of the overlay canvas
                PART_Overlay.InvalidateVisual();
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
        }
    }
}