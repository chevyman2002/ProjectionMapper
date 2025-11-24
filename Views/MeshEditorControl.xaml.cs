using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Numerics;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Shapes;
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
        private bool _isDisposed = false;

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

        // suppress reacting to VM changes while user is actively manipulating the mesh
        private bool _suppressVmRebind = false;

        // Collections for tracking all mesh layer visual elements
        private readonly List<System.Windows.Shapes.Polygon> _allMeshPolygons = new();
        private readonly List<(Thumb TL, Thumb TR, Thumb BL, Thumb BR, LayerViewModel Layer)> _allMeshHandles = new();

        // Track mesh point values at the start of a drag operation for undo/redo
        private Dictionary<int, Vector2> _meshPointsBeforeDrag = new();
        private bool _isDragging = false;

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

            PART_InputHandle_TL.DragStarted += InputHandle_DragStarted;
            PART_InputHandle_TR.DragStarted += InputHandle_DragStarted;
            PART_InputHandle_BL.DragStarted += InputHandle_DragStarted;
            PART_InputHandle_BR.DragStarted += InputHandle_DragStarted;

            PART_InputHandle_TL.DragCompleted += InputHandle_DragCompleted;
            PART_InputHandle_TR.DragCompleted += InputHandle_DragCompleted;
            PART_InputHandle_BL.DragCompleted += InputHandle_DragCompleted;
            PART_InputHandle_BR.DragCompleted += InputHandle_DragCompleted;

            PART_InputHandle_TL.DragDelta += InputHandle_TL_DragDelta;
            PART_InputHandle_TR.DragDelta += InputHandle_TR_DragDelta;
            PART_InputHandle_BL.DragDelta += InputHandle_BL_DragDelta;
            PART_InputHandle_BR.DragDelta += InputHandle_BR_DragDelta;

            PART_OutputDrag.DragDelta += OutputDrag_DragDelta;

            Loaded += (_, __) => ApplyLayout();
            Unloaded += (_, __) => OnUnloaded();
        }

        private void OnUnloaded()
        {
            _isDisposed = true;
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
            set => SetValue(VideoServiceProperty, value);        }

        private static void OnVideoServiceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MeshEditorControl ctl && !ctl._isDisposed)
            {
                // Detach any existing source handler from the old service
                try
                {
                    if (ctl._serviceHandlerForSource != null && e.OldValue is VideoService oldSvc)
                    {
                        oldSvc.FrameDecoded -= ctl._serviceHandlerForSource;
                    }

                    // Update internal reference
                    ctl._videoService = e.NewValue as VideoService;

                    // If a source id is already selected, (re)subscribe to frames for it so input preview shows immediately
                    if (!string.IsNullOrEmpty(ctl.SelectedSourceId) && ctl._videoService != null)
                    {
                        // ensure previous handler is null before creating a new one
                        ctl._serviceHandlerForSource = (layerId, bmp) =>
                        {
                            if (!ctl._isDisposed && layerId == ctl.SelectedSourceId)
                            {
                                ctl.SafeUpdateInputImage(bmp);
                            }
                        };
                        ctl._videoService.FrameDecoded += ctl._serviceHandlerForSource;
                    }

                    // If a layer is selected, re-run the selected-layer binding logic to attach its frame handler to the new service
                    if (ctl.SelectedLayer != null)
                    {
                        // Re-invoke the change handler so it reattaches subscriptions appropriately
                        ctl.OnSelectedLayerChanged(ctl.SelectedLayer, ctl.SelectedLayer);
                    }
                }
                catch { }
            }
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

        public static readonly DependencyProperty UndoRedoServiceProperty = DependencyProperty.Register(
            nameof(UndoRedoService), typeof(UndoRedoService), typeof(MeshEditorControl), new PropertyMetadata(null));

        public UndoRedoService? UndoRedoService
        {
            get => (UndoRedoService?)GetValue(UndoRedoServiceProperty);
            set => SetValue(UndoRedoServiceProperty, value);
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
            if (d is MeshEditorControl ctl && !ctl._isDisposed)
            {
                ctl.OnSelectedLayerChanged((LayerViewModel?)e.OldValue, (LayerViewModel?)e.NewValue);
            }
        }

        // New DP for all mesh layers from the selected imported video
        public static readonly DependencyProperty AllMeshLayersProperty = DependencyProperty.Register(
            nameof(AllMeshLayers), typeof(System.Collections.ObjectModel.ObservableCollection<LayerViewModel>), typeof(MeshEditorControl),
            new PropertyMetadata(null, OnAllMeshLayersChanged));

        public System.Collections.ObjectModel.ObservableCollection<LayerViewModel>? AllMeshLayers
        {
            get => (System.Collections.ObjectModel.ObservableCollection<LayerViewModel>?)GetValue(AllMeshLayersProperty);
            set => SetValue(AllMeshLayersProperty, value);
        }

        private static void OnAllMeshLayersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MeshEditorControl ctl && !ctl._isDisposed)
            {
                ctl.OnAllMeshLayersChanged((System.Collections.ObjectModel.ObservableCollection<LayerViewModel>?)e.OldValue, 
                                          (System.Collections.ObjectModel.ObservableCollection<LayerViewModel>?)e.NewValue);
            }
        }

        private void OnSelectedLayerChanged(LayerViewModel? oldVm, LayerViewModel? newVm)
        {
            if (_isDisposed) return;

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
                    if (!_isDisposed && layerId == newVm.Id)
                    {
                        SafeUpdateInputImage(bmp);
                    }
                };
                _videoService.FrameDecoded += _frameHandler;
            }
            else
            {
                // fallback: show combined render host frame if available
                SafeUpdateInputImageFromHost();
            }

            // Update all external overlays when selection changes
            UpdateAllExternalOverlays();
        }

        private void OnAllMeshLayersChanged(System.Collections.ObjectModel.ObservableCollection<LayerViewModel>? oldCollection, 
                                          System.Collections.ObjectModel.ObservableCollection<LayerViewModel>? newCollection)
        {
            if (_isDisposed) return;

            // Unsubscribe from old collection events
            if (oldCollection != null)
            {
                oldCollection.CollectionChanged -= AllMeshLayers_CollectionChanged;
                // Unsubscribe from property changes of individual layers and remove their overlays
                foreach (var layer in oldCollection)
                {
                    try { layer.PropertyChanged -= AllMeshLayer_PropertyChanged; } catch { }
                    
                    // Remove overlay for this layer
                    if (layer?.Model?.Id != null && _rendererManager != null)
                    {
                        var targetMonitor = layer.Model.TargetMonitorIndex;
                        try 
                        { 
                            _rendererManager.RemoveMeshOverlayForMonitor(targetMonitor >= 0 ? targetMonitor : null, layer.Model.Id); 
                        } 
                        catch { }
                    }
                }
            }

            // Subscribe to new collection events
            if (newCollection != null)
            {
                newCollection.CollectionChanged += AllMeshLayers_CollectionChanged;
                // Subscribe to property changes of individual layers
                foreach (var layer in newCollection)
                {
                    try { layer.PropertyChanged += AllMeshLayer_PropertyChanged; } catch { }
                }
            }

            // Refresh the display to show all mesh layer outlines and external overlays
            RefreshAllMeshLayerOverlays();
            UpdateAllExternalOverlays();

            System.Diagnostics.Debug.WriteLine($"OnAllMeshLayersChanged: {newCollection?.Count ?? 0} layers");
        }

        private void AllMeshLayers_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (_isDisposed) return;

            System.Diagnostics.Debug.WriteLine($"AllMeshLayers_CollectionChanged: Action={e.Action}");

            // Handle added items
            if (e.NewItems != null)
            {
                foreach (LayerViewModel layer in e.NewItems)
                {
                    try { layer.PropertyChanged += AllMeshLayer_PropertyChanged; } catch { }
                    System.Diagnostics.Debug.WriteLine($"  Added layer: {layer.Name} (ID: {layer.Id})");
                    
                    // Add overlay for the new layer immediately
                    UpdateExternalOverlayForLayer(layer);
                }
            }

            // Handle removed items
            if (e.OldItems != null)
            {
                foreach (LayerViewModel layer in e.OldItems)
                {
                    try { layer.PropertyChanged -= AllMeshLayer_PropertyChanged; } catch { }
                    System.Diagnostics.Debug.WriteLine($"  Removed layer: {layer.Name} (ID: {layer.Id})");
                    
                    // Remove overlay for this layer
                    if (layer?.Model?.Id != null && _rendererManager != null)
                    {
                        var targetMonitor = layer.Model.TargetMonitorIndex;
                        try 
                        { 
                            _rendererManager.RemoveMeshOverlayForMonitor(targetMonitor >= 0 ? targetMonitor : null, layer.Model.Id); 
                        } 
                        catch { }
                    }
                }
            }

            // Refresh the display for UI overlays (this is separate from external overlays)
            RefreshAllMeshLayerOverlays();
        }

        private void AllMeshLayer_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_isDisposed || _suppressVmRebind) return;

            // Only update external overlays for relevant property changes and only for the specific layer
            if (e == null || string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(LayerViewModel.MeshPoints) || 
                e.PropertyName == nameof(LayerViewModel.OutputMeshPoints) ||
                e.PropertyName == nameof(LayerViewModel.ShowOverlay) ||
                e.PropertyName == nameof(LayerViewModel.Visible))
            {
                // Update only the specific layer that changed, not all layers
                if (sender is LayerViewModel changedLayer)
                {
                    try 
                    { 
                        if (!Dispatcher.CheckAccess())
                        {
                            Dispatcher.BeginInvoke(() => 
                            {
                                RefreshAllMeshLayerOverlays(); // UI overlays still need full refresh
                                UpdateExternalOverlayForLayer(changedLayer); // Only update this layer's external overlay
                            });
                        }
                        else
                        {
                            RefreshAllMeshLayerOverlays(); // UI overlays still need full refresh
                            UpdateExternalOverlayForLayer(changedLayer); // Only update this layer's external overlay
                        }
                    } 
                    catch { }
                }
            }
        }

        private void SafeUpdateInputImage(BitmapSource? bmp)
        {
            if (_isDisposed) return;

            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(() => SafeUpdateInputImage(bmp));
                    return;
                }

                PART_InputImage.Source = bmp;
            }
            catch { }
        }

        private void SafeUpdateInputImageFromHost()
        {
            if (_isDisposed) return;

            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(() => SafeUpdateInputImageFromHost());
                    return;
                }

                if (_boundHost != null) 
                {
                    PART_InputImage.Source = _boundHost.CurrentFrame;
                }
                else 
                {
                    PART_InputImage.Source = null;
                }
            }
            catch { }
        }

        private void SelectedLayer_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isDisposed || _suppressVmRebind) return;

            if (e == null || e.PropertyName == nameof(LayerViewModel.MeshPoints) || e.PropertyName == nameof(LayerViewModel.OutputMeshPoints) || string.IsNullOrEmpty(e.PropertyName))
            {
                // use BeginInvoke to avoid potential cross-thread issues
                try 
                { 
                    if (!Dispatcher.CheckAccess())
                    {
                        Dispatcher.BeginInvoke(() => 
                        {
                            MapSelectedLayerMeshToRects();
                            // Only update the selected layer's external overlay, not all layers
                            if (SelectedLayer != null)
                            {
                                UpdateExternalOverlayForLayer(SelectedLayer);
                            }
                        });
                    }
                    else
                    {
                        MapSelectedLayerMeshToRects();
                        // Only update the selected layer's external overlay, not all layers
                        if (SelectedLayer != null)
                        {
                            UpdateExternalOverlayForLayer(SelectedLayer);
                        }
                    }
                } 
                catch { }
            }
        }
        #endregion

        public static readonly DependencyProperty SelectedSourceIdProperty = DependencyProperty.Register(
            nameof(SelectedSourceId), typeof(string), typeof(MeshEditorControl), new PropertyMetadata(null, OnSelectedSourceIdChanged));

        public string? SelectedSourceId
        {
            get => (string?)GetValue(SelectedSourceIdProperty);
            set => SetValue(SelectedSourceIdProperty, value);
        }

        private static void OnSelectedSourceIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MeshEditorControl ctl && !ctl._isDisposed)
            {
                ctl.OnSelectedSourceIdChanged((string?)e.OldValue, (string?)e.NewValue);
            }
        }

        private Action<string, BitmapSource?>? _serviceHandlerForSource;

        private void OnSelectedSourceIdChanged(string? oldId, string? newId)
        {
            if (_isDisposed) return;

            // Unsubscribe previous handler
            if (_videoService != null && _serviceHandlerForSource != null)
            {
                _videoService.FrameDecoded -= _serviceHandlerForSource;
                _serviceHandlerForSource = null;
            }

            if (string.IsNullOrEmpty(newId) || _videoService == null)
            {
                // Clear input image if no source selected
                SafeUpdateInputImage(null);
                return;
            }

            // Subscribe to frames for the selected source id
            _serviceHandlerForSource = (layerId, bmp) =>
            {
                if (!_isDisposed && layerId == newId)
                {
                    SafeUpdateInputImage(bmp);
                }
            };

            _videoService.FrameDecoded += _serviceHandlerForSource;
        }

        private void PART_Canvas_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)
        {
            if (_isDisposed) return;

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
            if (_isDisposed) return;

            if (e.MiddleButton == MouseButtonState.Pressed)
            {
                _isMiddleDown = true;
                _lastMouse = e.GetPosition(PART_Canvas);
                PART_Canvas.CaptureMouse();
            }
        }

        private void Canvas_MouseMove(object? sender, MouseEventArgs e)
        {
            if (_isDisposed) return;

            if (_isMiddleDown && e.MiddleButton == MouseButtonState.Pressed)
            {
                var pos = e.GetPosition(PART_Canvas);
                var delta = pos - _lastMouse;
                _lastMouse = pos;
                OffsetOutputAndCorners(delta.X, delta.Y);
                WriteBackMeshPoints();
                WriteBackOutputRect();
                ForwardOutputRect();
            }
        }

        private void Canvas_MouseUp(object? sender, MouseButtonEventArgs e)
        {
            if (_isDisposed) return;

            if (_isMiddleDown && e.MiddleButton == MouseButtonState.Released)
            {
                _isMiddleDown = false;
                PART_Canvas.ReleaseMouseCapture();
            }
        }

        private void Canvas_MouseWheel(object? sender, MouseWheelEventArgs e)
        {
            if (_isDisposed) return;

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
            if (_isDisposed) return;

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
            while (!token.IsCancellationRequested && !_isDisposed)
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
            if (_isDisposed) return;

            try
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

                // If there's no selected layer, hide the selected layer's overlays
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
                }
                else
                {
                    // Show the selected layer's overlays with handles for editing
                    PART_InputPolygon.Visibility = Visibility.Visible;
                    PART_InputHandle_TL.Visibility = Visibility.Visible;
                    PART_InputHandle_TR.Visibility = Visibility.Visible;
                    PART_InputHandle_BL.Visibility = Visibility.Visible;
                    PART_InputHandle_BR.Visibility = Visibility.Visible;

                    // Hide output overlays in the input preview - output is shown in the output host
                    PART_OutputRect.Visibility = Visibility.Collapsed;
                    PART_OutputDrag.Visibility = Visibility.Collapsed;

                    // If there's no isolated preview, fallback to bound host frame
                    if (_videoService == null && _boundHost != null) PART_InputImage.Source = _boundHost.CurrentFrame;
                }

                // Also refresh all mesh layer overlays
                RefreshAllMeshLayerOverlays();
            }
            catch { }
        }

        private void RefreshAllMeshLayerOverlays()
        {
            if (_isDisposed) return;

            try
            {
                var cw = PART_Canvas.ActualWidth;
                var ch = PART_Canvas.ActualHeight;
                if (double.IsNaN(cw) || cw <= 0 || double.IsNaN(ch) || ch <= 0) return;

                // Clear existing overlays
                ClearAllMeshLayerOverlays();

                // Create overlays for all mesh layers
                if (AllMeshLayers != null)
                {
                    foreach (var layer in AllMeshLayers)
                    {
                        if (layer == null) continue;
                        CreateOverlayForMeshLayer(layer, cw, ch);
                    }
                }
            }
            catch { }
        }

        private void ClearAllMeshLayerOverlays()
        {
            try
            {
                // Remove polygons from canvas
                foreach (var polygon in _allMeshPolygons)
                {
                    try { PART_Canvas.Children.Remove(polygon); } catch { }
                }
                _allMeshPolygons.Clear();

                // Remove handles from canvas
                foreach (var (tl, tr, bl, br, _) in _allMeshHandles)
                {
                    try { PART_Canvas.Children.Remove(tl); } catch { }
                    try { PART_Canvas.Children.Remove(tr); } catch { }
                    try { PART_Canvas.Children.Remove(bl); } catch { }
                    try { PART_Canvas.Children.Remove(br); } catch { }
                }
                _allMeshHandles.Clear();
            }
            catch { }
        }

        private void CreateOverlayForMeshLayer(LayerViewModel layer, double canvasWidth, double canvasHeight)
        {
            try
            {
                var pts = layer.MeshPoints;
                if (pts == null || pts.Length < 4) return;

                // Calculate corners
                var corners = new Point[4];
                corners[0] = new Point(pts[0].X * canvasWidth, pts[0].Y * canvasHeight); // TL
                corners[1] = new Point(pts[1].X * canvasWidth, pts[1].Y * canvasHeight); // TR
                corners[2] = new Point(pts[2].X * canvasWidth, pts[2].Y * canvasHeight); // BL
                corners[3] = new Point(pts[3].X * canvasWidth, pts[3].Y * canvasHeight); // BR

                // Determine if this is the selected layer to use different visual style
                bool isSelected = layer == SelectedLayer;

                // Create polygon
                var polygon = new System.Windows.Shapes.Polygon
                {
                    Points = new System.Windows.Media.PointCollection { corners[0], corners[1], corners[3], corners[2] },
                    Stroke = isSelected ? System.Windows.Media.Brushes.DodgerBlue : System.Windows.Media.Brushes.Orange,
                    StrokeThickness = isSelected ? 2 : 1.5,
                    Fill = isSelected ? new SolidColorBrush(Color.FromArgb(64, 52, 152, 219)) : new SolidColorBrush(Color.FromArgb(32, 255, 165, 0)),
                    IsHitTestVisible = false // Don't interfere with mouse events
                };

                PART_Canvas.Children.Add(polygon);
                _allMeshPolygons.Add(polygon);

                // Only show handles for the selected layer (for editing)
                if (isSelected)
                {
                    // The main polygon and handles are already handled by ApplyLayout for the selected layer
                    return;
                }

                // For non-selected layers, create small visual indicators only (no interactive handles)
                var handleSize = 6.0;
                var handleBrush = System.Windows.Media.Brushes.Orange;
                
                var tlIndicator = new System.Windows.Shapes.Ellipse
                {
                    Width = handleSize, Height = handleSize,
                    Fill = handleBrush,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(tlIndicator, corners[0].X - handleSize / 2);
                Canvas.SetTop(tlIndicator, corners[0].Y - handleSize / 2);
                PART_Canvas.Children.Add(tlIndicator);

                var trIndicator = new System.Windows.Shapes.Ellipse
                {
                    Width = handleSize, Height = handleSize,
                    Fill = handleBrush,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(trIndicator, corners[1].X - handleSize / 2);
                Canvas.SetTop(trIndicator, corners[1].Y - handleSize / 2);
                PART_Canvas.Children.Add(trIndicator);

                var blIndicator = new System.Windows.Shapes.Ellipse
                {
                    Width = handleSize, Height = handleSize,
                    Fill = handleBrush,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(blIndicator, corners[2].X - handleSize / 2);
                Canvas.SetTop(blIndicator, corners[2].Y - handleSize / 2);
                PART_Canvas.Children.Add(blIndicator);

                var brIndicator = new System.Windows.Shapes.Ellipse
                {
                    Width = handleSize, Height = handleSize,
                    Fill = handleBrush,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(brIndicator, corners[3].X - handleSize / 2);
                Canvas.SetTop(brIndicator, corners[3].Y - handleSize / 2);
                PART_Canvas.Children.Add(brIndicator);
            }
            catch { }
        }

        private static void PositionHandle(Thumb t, double left, double top)
        {
            Canvas.SetLeft(t, left);
            Canvas.SetTop(t, top);
        }

        private void OffsetOutputAndCorners(double dx, double dy)
        {
            if (_isDisposed) return;

            _suppressVmRebind = true;
            try
            {
                for (int i = 0; i < 4; i++) _corners[i] = new Point(_corners[i].X + dx, _corners[i].Y + dy);
                _outputRect = new Rect(_outputRect.X + dx, _outputRect.Y + dy, _outputRect.Width, _outputRect.Height);
                ApplyLayout();
            }
            finally { _suppressVmRebind = false; }
        }

        private void InputHandle_DragStarted(object sender, DragStartedEventArgs e)
        {
            try
            {
                _isDragging = true;
                _meshPointsBeforeDrag.Clear();
                
                var vm = SelectedLayer;
                if (vm == null) return;

                // Store current mesh point values before drag
                var pts = vm.MeshPoints;
                if (pts != null && pts.Length >= 4)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        _meshPointsBeforeDrag[i] = pts[i];
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"InputHandle_DragStarted failed: {ex}");
            }
        }

        private void InputHandle_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            try
            {
                _isDragging = false;

                var vm = SelectedLayer;
                if (vm == null || UndoRedoService == null) return;

                // Record undo action for each changed mesh point
                var pts = vm.MeshPoints;
                if (pts != null && pts.Length >= 4)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        if (_meshPointsBeforeDrag.ContainsKey(i))
                        {
                            var oldValue = _meshPointsBeforeDrag[i];
                            var newValue = pts[i];

                            // Only record if the value actually changed
                            if (Math.Abs(oldValue.X - newValue.X) > 0.001f || Math.Abs(oldValue.Y - newValue.Y) > 0.001f)
                            {
                                var action = new MeshPointChangeAction(vm, i, oldValue, newValue, isOutputMesh: false);
                                UndoRedoService.RecordAction(action);
                            }
                        }
                    }
                }

                _meshPointsBeforeDrag.Clear();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"InputHandle_DragCompleted failed: {ex}");
            }
        }

        private void InputHandle_TL_DragDelta(object sender, DragDeltaEventArgs e) => MoveCorner(0, e.HorizontalChange, e.VerticalChange);
        private void InputHandle_TR_DragDelta(object sender, DragDeltaEventArgs e) => MoveCorner(1, e.HorizontalChange, e.VerticalChange);
        private void InputHandle_BL_DragDelta(object sender, DragDeltaEventArgs e) => MoveCorner(2, e.HorizontalChange, e.VerticalChange);
        private void InputHandle_BR_DragDelta(object sender, DragDeltaEventArgs e) => MoveCorner(3, e.HorizontalChange, e.VerticalChange);

        private void MoveCorner(int index, double dx, double dy)
        {
            if (_isDisposed) return;

            _suppressVmRebind = true;
            try
            {
                _corners[index] = new Point(_corners[index].X + dx, _corners[index].Y + dy);
                ApplyLayout();
                WriteBackMeshPoints();
            }
            finally { _suppressVmRebind = false; }
        }

        private void OutputDrag_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_isDisposed) return;

            var dx = e.HorizontalChange;
            var dy = e.VerticalChange;
            _suppressVmRebind = true;
            try
            {
                _outputRect = new Rect(_outputRect.X + dx, _outputRect.Y + dy, _outputRect.Width, _outputRect.Height);
                ApplyLayout();
                WriteBackOutputRect();
                ForwardOutputRect();
            }
            finally { _suppressVmRebind = false; }
        }

        private void ForwardOutputRect()
        {
            if (_isDisposed) return;

            // Forward output rect to the renderer manager so live output moves
            if (_rendererManager == null) return;
            if (SelectedLayer == null) return;

            try
            {
                // Convert canvas coords to renderer coordinates. Assume render host size equals canvas size for now.
                var dest = new Rect(_outputRect.X, _outputRect.Y, _outputRect.Width, _outputRect.Height);
                // Request the renderer to move the layer mapping - easiest is to submit an empty frame with the new dest rect and current opacity
                _rendererManager.SubmitLayerFrame(SelectedLayer.Id ?? string.Empty, null, dest, null, SelectedLayer.Opacity);
            }
            catch { }
        }

        private void WriteBackMeshPoints()
        {
            if (_isDisposed) return;

            var vm = SelectedLayer;
            if (vm == null) return;
            var cw = PART_Canvas.ActualWidth;
            var ch = PART_Canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            try
            {
                var tl = new Vector2((float)(_corners[0].X / cw), (float)(_corners[0].Y / ch));
                var tr = new Vector2((float)(_corners[1].X / cw), (float)(_corners[1].Y / ch));
                var bl = new Vector2((float)(_corners[2].X / cw), (float)(_corners[2].Y / ch));
                var br = new Vector2((float)(_corners[3].X / cw), (float)(_corners[3].Y / ch));

                // Update input mesh points (source cropping) - this is the primary purpose of input pane
                vm.SetMeshPoint(0, tl);
                vm.SetMeshPoint(1, tr);
                vm.SetMeshPoint(2, bl);
                vm.SetMeshPoint(3, br);

                // Only update the selected layer's external overlay in real-time, not all layers
                UpdateExternalOverlayForLayer(vm);
            }
            catch { }
        }

        private void UpdateExternalOverlayForLayer(LayerViewModel layer)
        {
            if (_isDisposed || layer?.Model == null || _rendererManager == null) return;

            try
            {
                var layerId = layer.Model.Id;
                if (string.IsNullOrEmpty(layerId)) return;

                var targetMonitor = layer.Model.TargetMonitorIndex;

                var showOverlayPref = layer.Model.ShowOverlay;
                if (!showOverlayPref) 
                {
                    // Remove overlay if disabled
                    try 
                    { 
                        _rendererManager.RemoveMeshOverlayForMonitor(targetMonitor >= 0 ? targetMonitor : null, layerId); 
                    } 
                    catch { }
                    return;
                }

                // Choose which mesh points to use for external overlay:
                // If OutputMeshPoints have been modified from defaults (not all corners at 0,0,1,1), use them
                // Otherwise, use MeshPoints for the overlay
                Vector2[]? meshPointsToUse = null;
                var outputPts = layer.OutputMeshPoints;
                var inputPts = layer.MeshPoints;

                // Check if OutputMeshPoints are still at default positions (indicating they haven't been edited in output pane)
                bool outputPtsAreDefault = (outputPts != null && outputPts.Length >= 4 &&
                    IsPointApproximately(outputPts[0], new Vector2(0f, 0f)) &&
                    IsPointApproximately(outputPts[1], new Vector2(1f, 0f)) &&
                    IsPointApproximately(outputPts[2], new Vector2(0f, 1f)) &&
                    IsPointApproximately(outputPts[3], new Vector2(1f, 1f)));

                // If output points are at defaults, use input points; otherwise use output points
                meshPointsToUse = outputPtsAreDefault ? inputPts : outputPts;

                // Map chosen mesh points to renderer coordinates
                Point[]? quadForRenderer = null;
                try
                {
                    quadForRenderer = _rendererManager.MapNormalizedToRendererPoints(meshPointsToUse, targetMonitor >= 0 ? targetMonitor : null);
                }
                catch { quadForRenderer = null; }

                if (quadForRenderer != null && quadForRenderer.Length >= 4)
                {
                    // Remove existing overlay for this layer first, then add the new one
                    // This approach is much more efficient than clearing all overlays
                    try 
                    { 
                        _rendererManager.RemoveMeshOverlayForMonitor(targetMonitor >= 0 ? targetMonitor : null, layerId); 
                    } 
                    catch { }
                    
                    _rendererManager.AddMeshOverlayForMonitor(targetMonitor >= 0 ? targetMonitor : null, quadForRenderer, true, layerId);
                }
            }
            catch (Exception ex) 
            { 
                System.Diagnostics.Debug.WriteLine($"UpdateExternalOverlayForLayer failed for {layer?.Name}: {ex.Message}");
            }
        }

        private static bool IsPointApproximately(Vector2 point, Vector2 target, float tolerance = 0.01f)
        {
            return Math.Abs(point.X - target.X) < tolerance && Math.Abs(point.Y - target.Y) < tolerance;
        }

        private void UpdateAllExternalOverlays()
        {
            if (_isDisposed || _rendererManager == null || AllMeshLayers == null) return;

            try
            {
                // Don't clear and recreate all overlays - just update each one individually
                // This prevents exponential performance degradation with multiple mesh layers
                foreach (var meshLayer in AllMeshLayers)
                {
                    if (meshLayer?.Model == null) continue;
                    UpdateExternalOverlayForLayer(meshLayer);
                }
            }
            catch { }
        }

        private void WriteBackOutputRect()
        {
            if (_isDisposed) return;

            var vm = SelectedLayer;
            if (vm == null) return;

            // Update ViewModel's mapping rectangle from the canvas output rect
            try
            {
                vm.X = (int)Math.Round(_outputRect.X);
                vm.Y = (int)Math.Round(_outputRect.Y);
                vm.Width = (int)Math.Max(1, Math.Round(_outputRect.Width));
                vm.Height = (int)Math.Max(1, Math.Round(_outputRect.Height));
            }
            catch { }
        }

        private void MapSelectedLayerMeshToRects()
        {
            if (_isDisposed) return;

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
                try
                {
                    Dispatcher.BeginInvoke(new Action(MapSelectedLayerMeshToRects), System.Windows.Threading.DispatcherPriority.Loaded);
                }
                catch { }
                return;
            }

            var cw2 = PART_Canvas.ActualWidth;
            var ch2 = PART_Canvas.ActualHeight;

            try
            {
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
            catch { }
        }

        public void Dispose()
        {
            _isDisposed = true;
            
            // Clean up video service subscription
            if (_videoService != null && _frameHandler != null) _videoService.FrameDecoded -= _frameHandler;
            
            // Clean up all mesh layers subscription
            if (AllMeshLayers != null)
            {
                AllMeshLayers.CollectionChanged -= AllMeshLayers_CollectionChanged;
                foreach (var layer in AllMeshLayers)
                {
                    try { layer.PropertyChanged -= AllMeshLayer_PropertyChanged; } catch { }
                }
            }
            
            // Clear overlay elements
            ClearAllMeshLayerOverlays();
            
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}


