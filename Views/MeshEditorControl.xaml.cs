using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Numerics;
using System.ComponentModel;
using System.Windows.Media;
using ProjectionMapper.ViewModels;
using System.Windows.Media.Imaging;
using ProjectionMapper.Views;
using ProjectionMapper.Rendering;
using ProjectionMapper.Models;
using ProjectionMapper.Services;

namespace ProjectionMapper.Views
{
    /// <summary>
    /// Interactive canvas skeleton: supports panning with middle button and basic mouse handling.
    /// Added zoom/pinch support and explicit service injection.
    /// </summary>
    public partial class MeshEditorControl : UserControl, IDisposable
    {
        private bool _isMiddleDown;
        private Point _lastMouse;
        private readonly CancellationTokenSource _cts = new();

        // Mesh corner positions in canvas coordinates (TL, TR, BL, BR)
        private Point[] _corners = new Point[4]
        {
            new Point(100,20),
            new Point(500,20),
            new Point(100,260),
            new Point(500,260)
        };

        // Output rect state
        private Rect _outputRect = new(520, 300, 400, 240);

        // Bound host/renderer manager used to forward output rect changes
        private RenderHostControl? _boundHost;
        private RendererManager? _rendererManager;

        // Video service subscription for isolated preview
        private VideoService? _videoService;
        private Action<string, BitmapSource?>? _frameHandler;

        public MeshEditorControl()
        {
            InitializeComponent();
            PART_Canvas.MouseDown += Canvas_MouseDown;
            PART_Canvas.MouseMove += Canvas_MouseMove;
            PART_Canvas.MouseUp += Canvas_MouseUp;
            PART_Canvas.MouseWheel += Canvas_MouseWheel;

            // enable touch manipulation for pinch-to-zoom
            PART_Canvas.IsManipulationEnabled = true;
            PART_Canvas.ManipulationDelta += PART_Canvas_ManipulationDelta;

            PART_InputHandle_TL.DragDelta += InputHandle_TL_DragDelta;
            PART_InputHandle_TR.DragDelta += InputHandle_TR_DragDelta;
            PART_InputHandle_BL.DragDelta += InputHandle_BL_DragDelta;
            PART_InputHandle_BR.DragDelta += InputHandle_BR_DragDelta;

            PART_OutputDrag.DragDelta += OutputDrag_DragDelta;

            Loaded += (_, __) => ApplyLayout();

            Task.Run(() => BackgroundUpdateLoop(_cts.Token), _cts.Token);
        }

        #region Dependency properties for service injection and zoom
        public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
            nameof(Zoom), typeof(double), typeof(MeshEditorControl), new PropertyMetadata(1.0));

        public double Zoom
        {
            get => (double)GetValue(ZoomProperty);
            set => SetValue(ZoomProperty, value);
        }

        public static readonly DependencyProperty VideoServiceProperty = DependencyProperty.Register(
            nameof(VideoService), typeof(VideoService), typeof(MeshEditorControl), new PropertyMetadata(null, OnVideoServiceChanged));

        public VideoService? VideoService
        {
            get => (VideoService?)GetValue(VideoServiceProperty);
            set => SetValue(VideoServiceProperty, value);
        }

        private static void OnVideoServiceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            // nothing special here; selected layer handling will subscribe
        }

        public static readonly DependencyProperty RendererManagerProperty = DependencyProperty.Register(
            nameof(RendererManager), typeof(RendererManager), typeof(MeshEditorControl), new PropertyMetadata(null));

        public RendererManager? RendererManager
        {
            get => (RendererManager?)GetValue(RendererManagerProperty);
            set => SetValue(RendererManagerProperty, value);
        }

        public static readonly DependencyProperty InputHostProperty = DependencyProperty.Register(
            nameof(InputHost), typeof(RenderHostControl), typeof(MeshEditorControl), new PropertyMetadata(null));

        public RenderHostControl? InputHost
        {
            get => (RenderHostControl?)GetValue(InputHostProperty);
            set => SetValue(InputHostProperty, value);
        }

        public static readonly DependencyProperty ParentScrollViewerProperty = DependencyProperty.Register(
            nameof(ParentScrollViewer), typeof(ScrollViewer), typeof(MeshEditorControl), new PropertyMetadata(null));

        public ScrollViewer? ParentScrollViewer
        {
            get => (ScrollViewer?)GetValue(ParentScrollViewerProperty);
            set => SetValue(ParentScrollViewerProperty, value);
        }
        #endregion

        #region SelectedLayer DP
        public static readonly DependencyProperty SelectedLayerProperty = DependencyProperty.Register(
            nameof(SelectedLayer), typeof(LayerViewModel), typeof(MeshEditorControl),
            new PropertyMetadata(null, OnSelectedLayerChanged));

        public LayerViewModel? SelectedLayer
        {
            get => (LayerViewModel?)GetValue(SelectedLayerProperty);
            set => SetValue(SelectedLayerProperty, value);
        }

        private static void OnSelectedLayerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MeshEditorControl ctl)
            {
                ctl.OnSelectedLayerChanged((LayerViewModel?)e.OldValue, (LayerViewModel?)e.NewValue);
            }
        }

        private void OnSelectedLayerChanged(LayerViewModel? oldVm, LayerViewModel? newVm)
        {
            if (oldVm != null)
            {
                oldVm.PropertyChanged -= SelectedLayer_PropertyChanged;
            }

            if (newVm != null)
            {
                newVm.PropertyChanged += SelectedLayer_PropertyChanged;
            }

            MapSelectedLayerMeshToRects();

            // Use injected renderer manager and input host
            _rendererManager = RendererManager;
            _boundHost = InputHost;

            // Unsubscribe previous video service handler if any
            if (_videoService != null && _frameHandler != null)
            {
                _videoService.FrameDecoded -= _frameHandler;
                _frameHandler = null;
                _videoService = null;
            }

            // Use injected VideoService if available
            _videoService = VideoService;

            if (_videoService != null && newVm != null)
            {
                // subscribe to frame events and only show frames for selected layer
                _frameHandler = (layerId, bmp) =>
                {
                    if (layerId == newVm.Id)
                    {
                        Dispatcher.Invoke(() => PART_InputImage.Source = bmp);
                    }
                };
                _videoService.FrameDecoded += _frameHandler;
            }
            else
            {
                // fallback: show combined render host frame if available
                if (_boundHost != null) PART_InputImage.Source = _boundHost.CurrentFrame;
                else PART_InputImage.Source = null;
            }
        }

        private void SelectedLayer_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LayerViewModel.MeshPoints) || string.IsNullOrEmpty(e.PropertyName))
            {
                Dispatcher.Invoke(MapSelectedLayerMeshToRects);
            }
        }
        #endregion

        private void PART_Canvas_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)
        {
            // handle pinch-to-zoom. Use scale from delta and center at manipulation origin
            try
            {
                var scale = e.DeltaManipulation.Scale.Length; // average
                if (scale > 0)
                {
                    var origin = e.ManipulationOrigin; // relative to element
                    ApplyZoomAtPoint(Zoom * scale, origin);
                }
            }
            catch { }
            e.Handled = true;
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
                OffsetOutputAndCorners(delta.X, delta.Y);
                WriteBackMeshPoints();
                ForwardOutputRect();
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
            if (ParentScrollViewer == null) return;

            var oldZoom = Zoom;
            var delta = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            var mousePos = e.GetPosition(PART_Canvas);
            var newZoom = Math.Max(0.1, Math.Min(4.0, oldZoom * delta));
            ApplyZoomAtPoint(newZoom, mousePos);
            e.Handled = true;
        }

        private void ApplyZoomAtPoint(double newZoom, Point contentPoint)
        {
            if (ParentScrollViewer == null) { Zoom = newZoom; return; }
            var sv = ParentScrollViewer;
            var oldZoom = Zoom;
            if (Math.Abs(oldZoom - newZoom) < 1e-6) return;

            // mouse position in content coords already provided
            // compute mouse position in scaled absolute coords
            var mouseAbs = new Point(contentPoint.X * oldZoom, contentPoint.Y * oldZoom);
            var viewportX = mouseAbs.X - sv.HorizontalOffset;
            var viewportY = mouseAbs.Y - sv.VerticalOffset;

            Zoom = newZoom;

            var newMouseAbs = new Point(contentPoint.X * newZoom, contentPoint.Y * newZoom);
            var newOffsetX = newMouseAbs.X - viewportX;
            var newOffsetY = newMouseAbs.Y - viewportY;

            // clamp offsets
            if (double.IsNaN(newOffsetX) || double.IsInfinity(newOffsetX)) newOffsetX = 0;
            if (double.IsNaN(newOffsetY) || double.IsInfinity(newOffsetY)) newOffsetY = 0;

            sv.ScrollToHorizontalOffset(Math.Max(0, newOffsetX));
            sv.ScrollToVerticalOffset(Math.Max(0, newOffsetY));
        }

        private async Task BackgroundUpdateLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }

        private void ApplyLayout()
        {
            var cw = PART_Canvas.ActualWidth;
            var ch = PART_Canvas.ActualHeight;
            if (double.IsNaN(cw) || cw <= 0) cw = 800;
            if (double.IsNaN(ch) || ch <= 0) ch = 600;

            Canvas.SetLeft(PART_InputImage, 0);
            Canvas.SetTop(PART_InputImage, 0);
            PART_InputImage.Width = cw;
            PART_InputImage.Height = ch;

            // update polygon points from corners (order TL, TR, BR, BL for polygon winding)
            var pts = new System.Windows.Media.PointCollection { _corners[0], _corners[1], _corners[3], _corners[2] };
            PART_InputPolygon.Points = pts;

            PositionHandle(PART_InputHandle_TL, _corners[0].X - 6, _corners[0].Y - 6);
            PositionHandle(PART_InputHandle_TR, _corners[1].X - 6, _corners[1].Y - 6);
            PositionHandle(PART_InputHandle_BL, _corners[2].X - 6, _corners[2].Y - 6);
            PositionHandle(PART_InputHandle_BR, _corners[3].X - 6, _corners[3].Y - 6);

            Canvas.SetLeft(PART_OutputRect, _outputRect.X);
            Canvas.SetTop(PART_OutputRect, _outputRect.Y);
            PART_OutputRect.Width = _outputRect.Width;
            PART_OutputRect.Height = _outputRect.Height;

            Canvas.SetLeft(PART_OutputDrag, _outputRect.X);
            Canvas.SetTop(PART_OutputDrag, _outputRect.Y);
            PART_OutputDrag.Width = _outputRect.Width;
            PART_OutputDrag.Height = _outputRect.Height;

            // If there's no selected layer, hide overlays and do not show any image
            if (SelectedLayer == null)
            {
                PART_InputPolygon.Visibility = Visibility.Collapsed;
                PART_InputHandle_TL.Visibility = Visibility.Collapsed;
                PART_InputHandle_TR.Visibility = Visibility.Collapsed;
                PART_InputHandle_BL.Visibility = Visibility.Collapsed;
                PART_InputHandle_BR.Visibility = Visibility.Collapsed;
                PART_OutputRect.Visibility = Visibility.Collapsed;
                PART_OutputDrag.Visibility = Visibility.Collapsed;

                // Do not show any preview image when nothing is selected
                PART_InputImage.Source = null;

                return;
            }

            // Otherwise display overlays
            PART_InputPolygon.Visibility = Visibility.Visible;
            PART_InputHandle_TL.Visibility = Visibility.Visible;
            PART_InputHandle_TR.Visibility = Visibility.Visible;
            PART_InputHandle_BL.Visibility = Visibility.Visible;
            PART_InputHandle_BR.Visibility = Visibility.Visible;
            PART_OutputRect.Visibility = Visibility.Visible;
            PART_OutputDrag.Visibility = Visibility.Visible;

            // If there's no isolated preview, fallback to bound host frame
            if (_videoService == null && _boundHost != null) PART_InputImage.Source = _boundHost.CurrentFrame;
        }

        private static void PositionHandle(Thumb t, double left, double top)
        {
            Canvas.SetLeft(t, left);
            Canvas.SetTop(t, top);
        }

        private void OffsetOutputAndCorners(double dx, double dy)
        {
            for (int i = 0; i < 4; i++) _corners[i] = new Point(_corners[i].X + dx, _corners[i].Y + dy);
            _outputRect = new Rect(_outputRect.X + dx, _outputRect.Y + dy, _outputRect.Width, _outputRect.Height);
            ApplyLayout();
        }

        private void InputHandle_TL_DragDelta(object sender, DragDeltaEventArgs e) => MoveCorner(0, e.HorizontalChange, e.VerticalChange);
        private void InputHandle_TR_DragDelta(object sender, DragDeltaEventArgs e) => MoveCorner(1, e.HorizontalChange, e.VerticalChange);
        private void InputHandle_BL_DragDelta(object sender, DragDeltaEventArgs e) => MoveCorner(2, e.HorizontalChange, e.VerticalChange);
        private void InputHandle_BR_DragDelta(object sender, DragDeltaEventArgs e) => MoveCorner(3, e.HorizontalChange, e.VerticalChange);

        private void MoveCorner(int index, double dx, double dy)
        {
            _corners[index] = new Point(_corners[index].X + dx, _corners[index].Y + dy);
            ApplyLayout();
            WriteBackMeshPoints();
        }

        private void OutputDrag_DragDelta(object sender, DragDeltaEventArgs e)
        {
            var dx = e.HorizontalChange;
            var dy = e.VerticalChange;
            _outputRect = new Rect(_outputRect.X + dx, _outputRect.Y + dy, _outputRect.Width, _outputRect.Height);
            ApplyLayout();
            ForwardOutputRect();
        }

        private void ForwardOutputRect()
        {
            // Forward output rect to the renderer manager so live output moves
            if (_rendererManager == null) return;
            if (SelectedLayer == null) return;

            // Convert canvas coords to renderer coordinates. Assume render host size equals canvas size for now.
            var dest = new Rect(_outputRect.X, _outputRect.Y, _outputRect.Width, _outputRect.Height);
            // Request the renderer to move the layer mapping - easiest is to submit an empty frame with the new dest rect and current opacity
            _rendererManager.SubmitLayerFrame(SelectedLayer.Id ?? string.Empty, null, dest, SelectedLayer.Opacity);
        }

        private void WriteBackMeshPoints()
        {
            var vm = SelectedLayer;
            if (vm == null) return;
            var cw = PART_Canvas.ActualWidth;
            var ch = PART_Canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            var tl = new Vector2((float)(_corners[0].X / cw), (float)(_corners[0].Y / ch));
            var tr = new Vector2((float)(_corners[1].X / cw), (float)(_corners[1].Y / ch));
            var bl = new Vector2((float)(_corners[2].X / cw), (float)(_corners[2].Y / ch));
            var br = new Vector2((float)(_corners[3].X / cw), (float)(_corners[3].Y / ch));

            vm.SetMeshPoint(0, tl);
            vm.SetMeshPoint(1, tr);
            vm.SetMeshPoint(2, bl);
            vm.SetMeshPoint(3, br);
        }

        private void MapSelectedLayerMeshToRects()
        {
            var vm = SelectedLayer;
            if (vm == null)
            {
                // Hide overlays and do not show any image
                _corners[0] = new Point(0, 0);
                _corners[1] = new Point(0, 0);
                _corners[2] = new Point(0, 0);
                _corners[3] = new Point(0, 0);
                _outputRect = new Rect(0, 0, 0, 0);
                ApplyLayout();
                return;
            }

            // Ensure canvas size is available
            if (PART_Canvas.ActualWidth <= 0 || PART_Canvas.ActualHeight <= 0)
            {
                Dispatcher.BeginInvoke(new Action(MapSelectedLayerMeshToRects), System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

            var cw2 = PART_Canvas.ActualWidth;
            var ch2 = PART_Canvas.ActualHeight;

            var pts = vm.MeshPoints;
            if (pts == null || pts.Length < 4)
            {
                _corners[0] = new Point(0, 0);
                _corners[1] = new Point(cw2, 0);
                _corners[2] = new Point(0, ch2);
                _corners[3] = new Point(cw2, ch2);
            }
            else
            {
                // Order: TopLeft, TopRight, BottomLeft, BottomRight
                _corners[0] = new Point(pts[0].X * cw2, pts[0].Y * ch2);
                _corners[1] = new Point(pts[1].X * cw2, pts[1].Y * ch2);
                _corners[2] = new Point(pts[2].X * cw2, pts[2].Y * ch2);
                _corners[3] = new Point(pts[3].X * cw2, pts[3].Y * ch2);
            }

            // Output rect from VM bounds if available
            try
            {
                _outputRect = new Rect(vm.X, vm.Y, Math.Max(1, vm.Width), Math.Max(1, vm.Height));
            }
            catch
            {
                // ignore
            }

            ApplyLayout();
        }

        public void Dispose()
        {
            if (_videoService != null && _frameHandler != null) _videoService.FrameDecoded -= _frameHandler;
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}