using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ProjectionMapper.Views
{
    /// <summary>
    /// Interactive canvas skeleton: supports panning with middle button and basic mouse handling.
    /// All heavy processing must run on background threads and marshal results to UI thread.
    /// </summary>
    public partial class MeshEditorControl : UserControl, IDisposable
    {
        private bool _isMiddleDown;
        private Point _lastMouse;
        private readonly CancellationTokenSource _cts = new();

        public MeshEditorControl()
        {
            InitializeComponent();
            PART_Canvas.MouseDown += Canvas_MouseDown;
            PART_Canvas.MouseMove += Canvas_MouseMove;
            PART_Canvas.MouseUp += Canvas_MouseUp;
            PART_Canvas.MouseWheel += Canvas_MouseWheel;

            // Example background worker usage demonstration (non-blocking)
            Task.Run(() => BackgroundUpdateLoop(_cts.Token), _cts.Token);
        }

        private void Canvas_MouseDown(object? sender, MouseButtonEventArgs e)
        {
            if (e.MiddleButton == MouseButtonState.Pressed)
            {
                _isMiddleDown = true;
                _lastMouse = e.GetPosition(PART_Canvas);
                PART_Canvas.CaptureMouse();
            }
        }

        private void Canvas_MouseMove(object? sender, MouseEventArgs e)
        {
            if (_isMiddleDown && e.MiddleButton == MouseButtonState.Pressed)
            {
                var pos = e.GetPosition(PART_Canvas);
                var delta = pos - _lastMouse;
                _lastMouse = pos;

                // TODO: apply pan delta to transform (deferred to ViewModel / renderer)
            }
        }

        private void Canvas_MouseUp(object? sender, MouseButtonEventArgs e)
        {
            if (_isMiddleDown && e.MiddleButton == MouseButtonState.Released)
            {
                _isMiddleDown = false;
                PART_Canvas.ReleaseMouseCapture();
            }
        }

        private void Canvas_MouseWheel(object? sender, MouseWheelEventArgs e)
        {
            // TODO: implement zoom with proper focal point
        }

        private async Task BackgroundUpdateLoop(CancellationToken token)
        {
            // Background tasks must not access UI elements directly.
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                    // Example: compute something expensive in background, then dispatch to UI thread as needed.
                    // Application.Current?.Dispatcher?.Invoke(() => { /* update UI-bound properties */ });
                }
                catch (OperationCanceledException) { break; }
                catch (Exception)
                {
                    // Log or surface to UI via an injected logger — keep the loop resilient
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}