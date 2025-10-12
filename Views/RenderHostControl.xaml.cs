using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProjectionMapper.Views
{
    public partial class RenderHostControl : UserControl, IDisposable
    {
        private bool _disposed;

        public RenderHostControl()
        {
            InitializeComponent();
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
        /// </summary>
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
        /// </summary>
        public void SetMeshOverlay(System.Windows.Point[]? quadPoints, bool showPoints)
        {
            if (_disposed) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetMeshOverlay(quadPoints, showPoints));
                return;
            }

            PART_Overlay.Children.Clear();
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
                        PART_Overlay.Children.Add(ellipse);
                    }
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