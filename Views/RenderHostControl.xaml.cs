using System;
using System.Windows;
using System.Windows.Controls;
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
        }
    }
}