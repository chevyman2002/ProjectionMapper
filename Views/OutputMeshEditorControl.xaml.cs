using ProjectionMapper.Rendering;
using ProjectionMapper.Services;
using ProjectionMapper.ViewModels;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace ProjectionMapper.Views
{
    public partial class OutputMeshEditorControl : UserControl, IDisposable
    {
        // Track pending overlay retry attempts to avoid scheduling duplicates
        private readonly System.Collections.Generic.HashSet<string> _overlayRetryPending = new();

        private LayerViewModel? _vm;
        private VideoService? _videoService;
        private bool _isDisposed = false;

        // corners in canvas coordinates: TL, TR, BL, BR
        // Initialized to normalized coordinates (will be mapped to actual canvas size in ApplyLayout)
        // Matches LayerModel.OutputMeshPoints defaults: centered ~40% width/height
        private Point[] _corners = new Point[4]
        {
            new Point(0.3, 0.3),   // TL - normalized, centered ~40% of output
            new Point(0.7, 0.3),   // TR
            new Point(0.3, 0.7),   // BL
            new Point(0.7, 0.7)    // BR
        };
        
        // Flag to track if corners are in normalized coordinates (need conversion to canvas coords)
        private bool _cornersAreNormalized = true;

        private bool _suppressVmRebind = false;

        // Track output mesh point values at the start of a drag operation for undo/redo
        private System.Collections.Generic.Dictionary<int, Vector2> _outputMeshPointsBeforeDrag = new();
        private bool _isDragging = false;

        public OutputMeshEditorControl()
        {
            InitializeComponent();

            // corner handles -> move corners independently
            PART_Handle_TL.DragStarted += OutputHandle_DragStarted;
            PART_Handle_TR.DragStarted += OutputHandle_DragStarted;
            PART_Handle_BL.DragStarted += OutputHandle_DragStarted;
            PART_Handle_BR.DragStarted += OutputHandle_DragStarted;

            PART_Handle_TL.DragCompleted += OutputHandle_DragCompleted;
            PART_Handle_TR.DragCompleted += OutputHandle_DragCompleted;
            PART_Handle_BL.DragCompleted += OutputHandle_DragCompleted;
            PART_Handle_BR.DragCompleted += OutputHandle_DragCompleted;

            PART_Handle_TL.DragDelta += (s, e) => MoveCorner(0, e.HorizontalChange, e.VerticalChange);
            PART_Handle_TR.DragDelta += (s, e) => MoveCorner(1, e.HorizontalChange, e.VerticalChange);
            PART_Handle_BL.DragDelta += (s, e) => MoveCorner(2, e.HorizontalChange, e.VerticalChange);
            PART_Handle_BR.DragDelta += (s, e) => MoveCorner(3, e.HorizontalChange, e.VerticalChange);

            Loaded += (_, __) => ApplyLayout();
            Unloaded += (_, __) => OnUnloaded();
            
            // Handle canvas size changes to properly re-map mesh points
            PART_Canvas.SizeChanged += OnCanvasSizeChanged;
        }
        
        private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isDisposed) return;
            
            // When the canvas size changes, we need to re-map the mesh points from the ViewModel
            // to the new canvas size. This ensures proper scaling when the output preview pane resizes.
            if (_vm != null && e.PreviousSize.Width > 0 && e.PreviousSize.Height > 0)
            {
                // Scale existing corners from old size to new size
                double scaleX = e.NewSize.Width / e.PreviousSize.Width;
                double scaleY = e.NewSize.Height / e.PreviousSize.Height;
                
                for (int i = 0; i < _corners.Length; i++)
                {
                    _corners[i] = new Point(_corners[i].X * scaleX, _corners[i].Y * scaleY);
                }
            }
            
            ApplyLayout();
        }

        private void OnUnloaded()
        {
            _isDisposed = true;
            if (_videoService != null)
            {
                try { _videoService.FrameDecoded -= VideoService_FrameDecoded; } catch { }
            }
        }

        private void OutputHandle_DragStarted(object sender, DragStartedEventArgs e)
        {
            try
            {
                _isDragging = true;
                _outputMeshPointsBeforeDrag.Clear();
                
                var vm = _vm;
                if (vm == null) return;

                // Store current output mesh point values before drag
                var pts = vm.OutputMeshPoints;
                if (pts != null && pts.Length >= 4)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        _outputMeshPointsBeforeDrag[i] = pts[i];
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OutputHandle_DragStarted failed: {ex}");
            }
        }

        private void OutputHandle_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            try
            {
                _isDragging = false;

                var vm = _vm;
                if (vm == null || UndoRedoService == null) return;

                // Record undo action for each changed output mesh point
                var pts = vm.OutputMeshPoints;
                if (pts != null && pts.Length >= 4)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        if (_outputMeshPointsBeforeDrag.ContainsKey(i))
                        {
                            var oldValue = _outputMeshPointsBeforeDrag[i];
                            var newValue = pts[i];

                            // Only record if the value actually changed
                            if (Math.Abs(oldValue.X - newValue.X) > 0.001f || Math.Abs(oldValue.Y - newValue.Y) > 0.001f)
                            {
                                var action = new MeshPointChangeAction(vm, i, oldValue, newValue, isOutputMesh: true);
                                UndoRedoService.RecordAction(action);
                            }
                        }
                    }
                }

                _outputMeshPointsBeforeDrag.Clear();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OutputHandle_DragCompleted failed: {ex}");
            }
        }

        public static readonly DependencyProperty SelectedLayerProperty = DependencyProperty.Register(
            nameof(SelectedLayer), typeof(LayerViewModel), typeof(OutputMeshEditorControl), new PropertyMetadata(null, OnSelectedLayerChanged));

        public LayerViewModel? SelectedLayer
        {
            get => (LayerViewModel?)GetValue(SelectedLayerProperty);
            set => SetValue(SelectedLayerProperty, value);
        }

        private static void OnSelectedLayerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is OutputMeshEditorControl ctl && !ctl._isDisposed)
            {
                ctl.OnSelectedLayerChanged((LayerViewModel?)e.OldValue, (LayerViewModel?)e.NewValue);
            }
        }

        private void OnSelectedLayerChanged(LayerViewModel? oldVm, LayerViewModel? newVm)
        {
            if (oldVm != null)
            {
                oldVm.PropertyChanged -= SelectedLayer_PropertyChanged;
                // Clear the old layer from renderer and remove its overlay
                var layerId = oldVm.Model?.Id ?? oldVm.Id;
                if (!string.IsNullOrEmpty(layerId))
                {
                    // clear layer by submitting null frame; no destQuad -> null, opacity 0
                    RendererManager?.SubmitLayerFrame(layerId, null, new Rect(), null, 0.0);
                    
                    // Remove the mesh overlay for this layer
                    var targetMonitor = oldVm.Model?.TargetMonitorIndex;
                    try { RendererManager?.RemoveMeshOverlayForMonitor(targetMonitor >= 0 ? targetMonitor : null, layerId); } catch { }
                }
            }
            if (newVm != null)
            {
                newVm.PropertyChanged += SelectedLayer_PropertyChanged;
            }

            _vm = newVm;
            if (_vm == null)
            {
                PART_OutputPolygon.Visibility = Visibility.Collapsed;
                PART_Handle_TL.Visibility = Visibility.Collapsed;
                PART_Handle_TR.Visibility = Visibility.Collapsed;
                PART_Handle_BL.Visibility = Visibility.Collapsed;
                PART_Handle_BR.Visibility = Visibility.Collapsed;
                PART_CroppedPreview.Visibility = Visibility.Collapsed;
                return;
            }

            // Preview is always visible when layer is selected
            PART_CroppedPreview.Visibility = Visibility.Visible;

            // Polygon and handles visibility depends on ShowOverlay setting
            UpdateOverlayControlsVisibility();

            MapSelectedLayerMeshToRects();
            TryRefreshPreviewFromLastFrame();
        }

        private void SelectedLayer_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_isDisposed || _suppressVmRebind) return;
            if (e == null || string.IsNullOrEmpty(e.PropertyName)) return;

            // Handle ShowOverlay changes to update polygon/handle visibility
            if (e.PropertyName == nameof(LayerViewModel.ShowOverlay))
            {
                try
                {
                    if (!Dispatcher.CheckAccess())
                    {
                        Dispatcher.BeginInvoke(() => UpdateOverlayControlsVisibility());
                    }
                    else
                    {
                        UpdateOverlayControlsVisibility();
                    }
                }
                catch { }
            }

            // Handle mesh/position changes
            if (e.PropertyName == nameof(LayerViewModel.MeshPoints) ||
                e.PropertyName == nameof(LayerViewModel.OutputMeshPoints) ||
                e.PropertyName == nameof(LayerViewModel.X) ||
                e.PropertyName == nameof(LayerViewModel.Y) ||
                e.PropertyName == nameof(LayerViewModel.Width) ||
                e.PropertyName == nameof(LayerViewModel.Height))
            {
                try
                {
                    if (!Dispatcher.CheckAccess()) Dispatcher.BeginInvoke(() => MapSelectedLayerMeshToRects());
                    else MapSelectedLayerMeshToRects();
                }
                catch { }
            }
        }

        /// <summary>
        /// Updates the visibility of the polygon and corner handles based on ShowOverlay setting.
        /// The video preview (PART_CroppedPreview) is always kept visible when a layer is selected.
        /// </summary>
        private void UpdateOverlayControlsVisibility()
        {
            if (_isDisposed) return;

            try
            {
                var showOverlay = _vm?.Model?.ShowOverlay ?? true;
                var visibility = showOverlay ? Visibility.Visible : Visibility.Collapsed;

                PART_OutputPolygon.Visibility = visibility;
                PART_Handle_TL.Visibility = visibility;
                PART_Handle_TR.Visibility = visibility;
                PART_Handle_BL.Visibility = visibility;
                PART_Handle_BR.Visibility = visibility;
            }
            catch { }
        }

        public static readonly DependencyProperty VideoServiceProperty = DependencyProperty.Register(
            nameof(VideoService), typeof(VideoService), typeof(OutputMeshEditorControl), new PropertyMetadata(null, OnVideoServiceChanged));

        public VideoService? VideoService
        {
            get => (VideoService?)GetValue(VideoServiceProperty);
            set => SetValue(VideoServiceProperty, value);
        }

        private static void OnVideoServiceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is OutputMeshEditorControl ctl)
            {
                if (e.OldValue is VideoService old)
                {
                    try { old.FrameDecoded -= ctl.VideoService_FrameDecoded; } catch { }
                }
                if (e.NewValue is VideoService neu)
                {
                    try { neu.FrameDecoded += ctl.VideoService_FrameDecoded; } catch { }
                }
                ctl._videoService = e.NewValue as VideoService;
            }
        }

        public static readonly DependencyProperty HostRenderHostProperty = DependencyProperty.Register(
            nameof(HostRenderHost), typeof(RenderHostControl), typeof(OutputMeshEditorControl), new PropertyMetadata(null));

        public RenderHostControl? HostRenderHost
        {
            get => (RenderHostControl?)GetValue(HostRenderHostProperty);
            set => SetValue(HostRenderHostProperty, value);
        }

        public static readonly DependencyProperty RendererManagerProperty = DependencyProperty.Register(
            nameof(RendererManager), typeof(RendererManager), typeof(OutputMeshEditorControl), new PropertyMetadata(null));

        public RendererManager? RendererManager
        {
            get => (RendererManager?)GetValue(RendererManagerProperty);
            set => SetValue(RendererManagerProperty, value);
        }

        // Dependency property to expose all mesh layers collection so we can update all overlays
        public static readonly DependencyProperty AllMeshLayersProperty = DependencyProperty.Register(
            nameof(AllMeshLayers), typeof(System.Collections.ObjectModel.ObservableCollection<LayerViewModel>), typeof(OutputMeshEditorControl), new PropertyMetadata(null, OnAllMeshLayersChanged));

        public System.Collections.ObjectModel.ObservableCollection<LayerViewModel>? AllMeshLayers
        {
            get => (System.Collections.ObjectModel.ObservableCollection<LayerViewModel>?)GetValue(AllMeshLayersProperty);
            set => SetValue(AllMeshLayersProperty, value);
        }

        public static readonly DependencyProperty UndoRedoServiceProperty = DependencyProperty.Register(
            nameof(UndoRedoService), typeof(UndoRedoService), typeof(OutputMeshEditorControl), new PropertyMetadata(null));

        public UndoRedoService? UndoRedoService
        {
            get => (UndoRedoService?)GetValue(UndoRedoServiceProperty);
            set => SetValue(UndoRedoServiceProperty, value);
        }

        private static void OnAllMeshLayersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is OutputMeshEditorControl ctl && !ctl._isDisposed)
            {
                ctl.OnAllMeshLayersChanged((System.Collections.ObjectModel.ObservableCollection<LayerViewModel>?)e.OldValue, 
                                          (System.Collections.ObjectModel.ObservableCollection<LayerViewModel>?)e.NewValue);
            }
        }

        private void OnAllMeshLayersChanged(System.Collections.ObjectModel.ObservableCollection<LayerViewModel>? oldCollection, 
                                          System.Collections.ObjectModel.ObservableCollection<LayerViewModel>? newCollection)
        {
            if (_isDisposed) return;

            // Unsubscribe from old collection events and REMOVE OVERLAYS to prevent ghost meshes
            if (oldCollection != null)
            {
                oldCollection.CollectionChanged -= AllMeshLayers_CollectionChanged;
                // Unsubscribe from property changes of individual layers AND remove their overlays
                foreach (var layer in oldCollection)
                {
                    try { layer.PropertyChanged -= AllMeshLayer_PropertyChanged; } catch { }
                    
                    // CRITICAL FIX: Remove overlays for old layers to prevent ghost mesh boxes
                    if (layer?.Model?.Id != null && RendererManager != null)
                    {
                        var targetMonitor = layer.Model.TargetMonitorIndex;
                        try 
                        { 
                            RendererManager.RemoveMeshOverlayForMonitor(targetMonitor >= 0 ? targetMonitor : null, layer.Model.Id);
                            Debug.WriteLine($"OutputMeshEditorControl: Removed overlay for old layer {layer.Name}");
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
                
                // CRITICAL: Immediately update overlays for existing layers on initial binding
                // This ensures overlays appear without requiring user interaction
                foreach (var layer in newCollection)
                {
                    try
                    {
                        UpdateExternalOverlayForSingleLayer(layer);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"OutputMeshEditorControl: Initial overlay for {layer.Name} failed: {ex.Message}");
                    }
                }
            }

            // Also do a delayed update to catch any timing issues with renderer initialization
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_isDisposed) return;
                    UpdateAllMeshLayersExternalOverlays();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"OutputMeshEditorControl: Delayed overlay update failed: {ex.Message}");
                }
            }), DispatcherPriority.Loaded);

            Debug.WriteLine($"OutputMeshEditorControl: OnAllMeshLayersChanged: {newCollection?.Count ?? 0} layers");
        }

        private void AllMeshLayers_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (_isDisposed) return;

            Debug.WriteLine($"OutputMeshEditorControl: AllMeshLayers_CollectionChanged: Action={e.Action}");

            // Handle added items
            if (e.NewItems != null)
            {
                foreach (LayerViewModel layer in e.NewItems)
                {
                    try { layer.PropertyChanged += AllMeshLayer_PropertyChanged; } catch { }
                    Debug.WriteLine($"  OutputMeshEditorControl: Added layer: {layer.Name} (ID: {layer.Id})");
                    
                    // Immediately add overlay for the new layer
                    UpdateExternalOverlayForSingleLayer(layer);
                }
            }

            // Handle removed items
            if (e.OldItems != null)
            {
                foreach (LayerViewModel layer in e.OldItems)
                {
                    try { layer.PropertyChanged -= AllMeshLayer_PropertyChanged; } catch { }
                    Debug.WriteLine($"  OutputMeshEditorControl: Removed layer: {layer.Name} (ID: {layer.Id})");
                    
                    // Remove overlay for this layer
                    if (layer?.Model?.Id != null && RendererManager != null)
                    {
                        var targetMonitor = layer.Model.TargetMonitorIndex;
                        try 
                        { 
                            RendererManager.RemoveMeshOverlayForMonitor(targetMonitor >= 0 ? targetMonitor : null, layer.Model.Id); 
                        } 
                        catch { }
                    }
                }
            }
        }

        private void AllMeshLayer_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_isDisposed || _suppressVmRebind) return;




            // Only update for specific property changes that affect overlays
            if (e != null && !string.IsNullOrEmpty(e.PropertyName))
            {
                if (e.PropertyName == nameof(LayerViewModel.MeshPoints) || 
                    e.PropertyName == nameof(LayerViewModel.OutputMeshPoints) ||
                    e.PropertyName == nameof(LayerViewModel.ShowOverlay) ||
                    e.PropertyName == nameof(LayerViewModel.Visible) ||
                    e.PropertyName == nameof(LayerViewModel.TargetMonitorIndex))
                {
                    // Update only the specific layer that changed, not all layers
                    if (sender is LayerViewModel changedLayer)
                    {
                        try 
                        { 
                            // If target monitor changed, first clear overlays from the old monitor
                            if (e.PropertyName == nameof(LayerViewModel.TargetMonitorIndex))
                            {
                                var layerId = changedLayer.Model?.Id ?? changedLayer.Id;
                                if (!string.IsNullOrEmpty(layerId) && RendererManager != null)
                                {
                                    // Clear from ALL monitors since we don't know the previous monitor index
                                    try { RendererManager.RemoveMeshOverlayForMonitor(null, layerId); } catch { }
                                }
                            }

                            // NOTE: Visible property only affects overlay visibility, NOT video rendering
                            // Video content always renders - only the bounding box/handles are hidden

                            if (!Dispatcher.CheckAccess())
                            {
                                Dispatcher.BeginInvoke(() => UpdateExternalOverlayForSingleLayer(changedLayer), DispatcherPriority.Normal);
                            }
                            else
                            {
                                UpdateExternalOverlayForSingleLayer(changedLayer);
                            }
                        } 
                        catch { }
                    }
                }
            }
        }

        /// <summary>
        /// Update external overlay for a single layer (efficient, targeted update).
        /// </summary>
        private void UpdateExternalOverlayForSingleLayer(LayerViewModel meshVm)
        {
            if (_isDisposed || meshVm?.Model == null || RendererManager == null) return;

            try
            {
                var layerId = meshVm.Model.Id;
                if (string.IsNullOrEmpty(layerId)) return;

                // Check both ShowOverlay and Visible properties - both must be true to show overlay
                var showOverlayPref = meshVm.Model.ShowOverlay;
                var isVisible = meshVm.Model.Visible;
                if (!showOverlayPref || !isVisible)
                {
                    // Remove overlay if disabled or hidden
                    var targetMonitor = meshVm.Model.TargetMonitorIndex;
                    try 
                    { 
                        RendererManager.RemoveMeshOverlayForMonitor(targetMonitor >= 0 ? targetMonitor : null, layerId); 
                    } 
                    catch { }
                    return;
                }

                var targetMonitor2 = meshVm.Model.TargetMonitorIndex;

                // Always use OutputMeshPoints for the overlay - they now have sensible defaults
                var meshPointsToUse = meshVm.OutputMeshPoints;

                // Map to renderer coordinates
                Point[]? quadForRenderer = null;
                try
                {
                    quadForRenderer = RendererManager.MapNormalizedToRendererPoints(meshPointsToUse, targetMonitor2 >= 0 ? targetMonitor2 : null);
                }
                catch { quadForRenderer = null; }

                if (quadForRenderer != null && quadForRenderer.Length >= 4)
                {
                    // Remove existing overlay and add new one
                    try { RendererManager.RemoveMeshOverlayForMonitor(targetMonitor2 >= 0 ? targetMonitor2 : null, layerId); } catch { }
                    try { RendererManager.AddMeshOverlayForMonitor(targetMonitor2 >= 0 ? targetMonitor2 : null, quadForRenderer, true, layerId); } catch { }

                    // If we had a pending retry scheduled, clear it since we succeeded
                    try { lock (_overlayRetryPending) { _overlayRetryPending.Remove(layerId); } } catch { }
                }
                else
                {
                    // If mapping failed (renderer not yet ready / no frame), try host-based fallback if available
                    bool didFallback = false;
                    try
                    {
                        if (HostRenderHost != null)
                        {
                            var cf = HostRenderHost.CurrentFrame;
                            if (cf != null && cf.PixelWidth > 0 && cf.PixelHeight > 0 && PART_Canvas.ActualWidth > 0 && PART_Canvas.ActualHeight > 0)
                            {
                                var scaleX = cf.PixelWidth / PART_Canvas.ActualWidth;
                                var scaleY = cf.PixelHeight / PART_Canvas.ActualHeight;
                                var fallbackQuad = new Point[4]
                                {
                                    new Point(meshVm.OutputMeshPoints[0].X * PART_Canvas.ActualWidth * scaleX, meshVm.OutputMeshPoints[0].Y * PART_Canvas.ActualHeight * scaleY),
                                    new Point(meshVm.OutputMeshPoints[1].X * PART_Canvas.ActualWidth * scaleX, meshVm.OutputMeshPoints[1].Y * PART_Canvas.ActualHeight * scaleY),
                                    new Point(meshVm.OutputMeshPoints[2].X * PART_Canvas.ActualWidth * scaleX, meshVm.OutputMeshPoints[2].Y * PART_Canvas.ActualHeight * scaleY),
                                    new Point(meshVm.OutputMeshPoints[3].X * PART_Canvas.ActualWidth * scaleX, meshVm.OutputMeshPoints[3].Y * PART_Canvas.ActualHeight * scaleY)
                                };

                                try { RendererManager.RemoveMeshOverlayForMonitor(targetMonitor2 >= 0 ? targetMonitor2 : null, layerId); } catch { }
                                try { RendererManager.AddMeshOverlayForMonitor(targetMonitor2 >= 0 ? targetMonitor2 : null, fallbackQuad, true, layerId); } catch { }
                                didFallback = true;
                            }
                        }
                    }
                    catch { didFallback = false; }

                    // If we neither mapped nor fell back, schedule a one-time retry when renderer/host is likely ready
                    if (!didFallback)
                    {
                        bool alreadyScheduled = false;
                        try { lock (_overlayRetryPending) { alreadyScheduled = !_overlayRetryPending.Add(layerId); } } catch { alreadyScheduled = true; }
                        if (!alreadyScheduled)
                        {
                            try
                            {
                                // schedule background retry - won't block UI
                                Dispatcher.BeginInvoke(new Action(async () =>
                                {
                                    try
                                    {
                                        await Task.Delay(250).ConfigureAwait(false);
                                    }
                                    catch { }
                                    try
                                    {
                                        if (_isDisposed) return;
                                        UpdateExternalOverlayForSingleLayer(meshVm);
                                    }
                                    catch { }
                                }), DispatcherPriority.Background);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UpdateExternalOverlayForSingleLayer: Failed for {meshVm.Name}: {ex.Message}");
            }
        }

        private void VideoService_FrameDecoded(string layerId, BitmapSource? bmp)
        {
            try
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                if (!IsLoaded) return;
                if (_vm == null) return;

                var srcId = _vm.Model?.SourceId;
                if (srcId == null) return;
                if (!string.Equals(srcId, layerId, StringComparison.OrdinalIgnoreCase)) return;
                if (bmp == null) return;

                // Crop using the source-side MeshPoints (input selection)
                BitmapSource frameForPreview = CropFrameToMesh(bmp, _vm.MeshPoints);

                try { if (frameForPreview != null && !frameForPreview.IsFrozen) { var clone = frameForPreview.Clone(); clone.Freeze(); frameForPreview = clone; } } catch { }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        PART_CroppedPreview.Source = frameForPreview;
                        PART_CroppedPreview.Visibility = Visibility.Visible;
                        ApplyLayout();
                    }
                    catch { }
                }), DispatcherPriority.Normal);
            }
            catch (ObjectDisposedException) { }
            catch { }
        }

        private BitmapSource CropFrameToMesh(BitmapSource? frame, Vector2[]? meshPoints)
        {
            if (frame == null) return frame!;
            if (meshPoints == null || meshPoints.Length < 4) return frame;

            try
            {
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                foreach (var p in meshPoints)
                {
                    minX = Math.Min(minX, p.X);
                    minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X);
                    maxY = Math.Max(maxY, p.Y);
                }

                minX = Math.Max(0f, Math.Min(1f, minX));
                minY = Math.Max(0f, Math.Min(1f, minY));
                maxX = Math.Max(0f, Math.Min(1f, maxX));
                maxY = Math.Max(0f, Math.Min(1f, maxY));

                int srcW = frame.PixelWidth; int srcH = frame.PixelHeight;
                int srcX = (int)Math.Floor(minX * srcW);
                int srcY = (int)Math.Floor(minY * srcH);
                int cropW = Math.Max(1, (int)Math.Ceiling((maxX - minX) * srcW));
                int cropH = Math.Max(1, (int)Math.Ceiling((maxY - minY) * srcH));

                if (srcX < 0) srcX = 0; if (srcY < 0) srcY = 0;
                if (srcX + cropW > srcW) cropW = srcW - srcX;
                if (srcY + cropH > srcH) cropH = srcH - srcY;

                if (cropW > 0 && cropH > 0)
                {
                    var cb = new CroppedBitmap(frame, new Int32Rect(srcX, srcY, cropW, cropH));
                    try { cb.Freeze(); } catch { }
                    return cb;
                }
            }
            catch { }

            return frame;
        }

        private void TryRefreshPreviewFromLastFrame()
        {
            if (_videoService == null || _vm == null) return;

            var srcId = _vm.Model?.SourceId;
            if (string.IsNullOrEmpty(srcId)) return;

            try
            {
                if (_video_service_try_get_last_frame(srcId, out var last))
                {
                    if (last != null)
                    {
                        var frameForPreview = CropFrameToMesh(last, _vm.MeshPoints);
                        try { if (frameForPreview != null && !frameForPreview.IsFrozen) { var clone = frameForPreview.Clone(); clone.Freeze(); frameForPreview = clone; } } catch { }
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                PART_CroppedPreview.Source = frameForPreview;
                                PART_CroppedPreview.Visibility = Visibility.Visible;
                                ApplyLayout();
                            }
                            catch { }
                        }), DispatcherPriority.Normal);
                    }
                }
            }
            catch { }
        }

        // wrapper to avoid direct exception on service being null/changed
        private bool _video_service_try_get_last_frame(string srcId, out BitmapSource? last)
        {
            last = null;
            try
            {
                return _videoService?.TryGetLastFrame(srcId, out last) ?? false;
            }
            catch { return false; }
        }

        private void ApplyLayout()
        {
            try
            {
                // If corners are in normalized coordinates, convert to actual canvas coordinates
                if (_cornersAreNormalized && PART_Canvas.ActualWidth > 0 && PART_Canvas.ActualHeight > 0)
                {
                    for (int i = 0; i < _corners.Length; i++)
                    {
                        _corners[i] = new Point(_corners[i].X * PART_Canvas.ActualWidth, _corners[i].Y * PART_Canvas.ActualHeight);
                    }
                    _cornersAreNormalized = false; // Now in canvas coordinates
                }

                // compute bounding box from corners for drawing overlay rect
                double minX = Math.Min(Math.Min(_corners[0].X, _corners[1].X), Math.Min(_corners[2].X, _corners[3].X));
                double minY = Math.Min(Math.Min(_corners[0].Y, _corners[1].Y), Math.Min(_corners[2].Y, _corners[3].Y));
                double maxX = Math.Max(Math.Max(_corners[0].X, _corners[1].X), Math.Max(_corners[2].X, _corners[3].X));
                double maxY = Math.Max(Math.Max(_corners[0].Y, _corners[1].Y), Math.Max(_corners[2].Y, _corners[3].Y));

                var rect = new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));

                double width = rect.Width;
                double height = rect.Height;

                // Position and clip the preview image to fill the polygon
                Canvas.SetLeft(PART_CroppedPreview, minX);
                Canvas.SetTop(PART_CroppedPreview, minY);
                PART_CroppedPreview.Width = width;
                PART_CroppedPreview.Height = height;
                PART_CroppedPreview.Stretch = Stretch.Fill;

                // Create clip geometry for the polygon shape, offset by the bounding box position
                var clipGeometry = new PathGeometry();
                var figure = new PathFigure();
                figure.StartPoint = new Point(_corners[0].X - minX, _corners[0].Y - minY);
                figure.Segments.Add(new LineSegment(new Point(_corners[1].X - minX, _corners[1].Y - minY), true));
                figure.Segments.Add(new LineSegment(new Point(_corners[3].X - minX, _corners[3].Y - minY), true));
                figure.Segments.Add(new LineSegment(new Point(_corners[2].X - minX, _corners[2].Y - minY), true));
                figure.IsClosed = true;
                clipGeometry.Figures.Add(figure);
                PART_CroppedPreview.Clip = clipGeometry;

                PART_OutputPolygon.Points = new PointCollection { _corners[0], _corners[1], _corners[3], _corners[2] };

                Canvas.SetLeft(PART_Handle_TL, _corners[0].X - 6);
                Canvas.SetTop(PART_Handle_TL, _corners[0].Y - 6);
                Canvas.SetLeft(PART_Handle_TR, _corners[1].X - 6);
                Canvas.SetTop(PART_Handle_TR, _corners[1].Y - 6);
                Canvas.SetLeft(PART_Handle_BL, _corners[2].X - 6);
                Canvas.SetTop(PART_Handle_BL, _corners[2].Y - 6);
                Canvas.SetLeft(PART_Handle_BR, _corners[3].X - 6);
                Canvas.SetTop(PART_Handle_BR, _corners[3].Y - 6);
            }
            catch { }
        }


        private Transform ComputePerspectiveTransform(Point tl, Point tr, Point bl, Point br)
        {
            // For a simple quad warp in WPF, we use a MatrixTransform approximation
            // True perspective requires a custom shader or using a 3D transform
            // For now, use a simple affine transform as a placeholder
            // TODO: Implement true perspective warp using PlaneProjection or custom shader
            
            // Simple bounding box transform for now
            var minX = Math.Min(Math.Min(tl.X, tr.X), Math.Min(bl.X, br.X));
            var minY = Math.Min(Math.Min(tl.Y, tr.Y), Math.Min(bl.Y, br.Y));
            var maxX = Math.Max(Math.Max(tl.X, tr.X), Math.Max(bl.X, br.X));
            var maxY = Math.Max(Math.Max(tl.Y, tr.Y), Math.Max(bl.Y, br.Y));

            var width = Math.Max(1, maxX - minX);
            var height = Math.Max(1, maxY - minY);

            var transformGroup = new TransformGroup();
            transformGroup.Children.Add(new ScaleTransform(width / PART_Canvas.ActualWidth, height / PART_Canvas.ActualHeight));
            transformGroup.Children.Add(new TranslateTransform(minX, minY));

            return transformGroup;
        }

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

        private void WriteBackMeshPoints()
        {
            if (_isDisposed || _vm == null) return;

            var cw = PART_Canvas.ActualWidth;
            var ch = PART_Canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            try
            {
                // Write corners back to OutputMeshPoints (normalized 0-1 coords relative to output canvas)
                var tl = new System.Numerics.Vector2((float)(_corners[0].X / cw), (float)(_corners[0].Y / ch));
                var tr = new System.Numerics.Vector2((float)(_corners[1].X / cw), (float)(_corners[1].Y / ch));
                var bl = new System.Numerics.Vector2((float)(_corners[2].X / cw), (float)(_corners[2].Y / ch));
                var br = new System.Numerics.Vector2((float)(_corners[3].X / cw), (float)(_corners[3].Y / ch));

                _vm.SetOutputMeshPoint(0, tl);
                _vm.SetOutputMeshPoint(1, tr);
                _vm.SetOutputMeshPoint(2, bl);
                _vm.SetOutputMeshPoint(3, br);

                // Also compute bounding rect for X/Y/Width/Height properties in renderer coordinates
                double minX = Math.Min(Math.Min(_corners[0].X, _corners[1].X), Math.Min(_corners[2].X, _corners[3].X));
                double minY = Math.Min(Math.Min(_corners[0].Y, _corners[1].Y), Math.Min(_corners[2].Y, _corners[3].Y));
                double maxX = Math.Max(Math.Max(_corners[0].X, _corners[1].X), Math.Max(_corners[2].X, _corners[3].X));
                double maxY = Math.Max(Math.Max(_corners[0].Y, _corners[1].Y), Math.Max(_corners[2].Y, _corners[3].Y));

                // Scale back to renderer coordinates if host is available
                if (HostRenderHost != null && HostRenderHost.CurrentFrame != null && PART_Canvas.ActualWidth > 0 && PART_Canvas.ActualHeight > 0)
                {
                    var scaleX = HostRenderHost.CurrentFrame.PixelWidth / PART_Canvas.ActualWidth;
                    var scaleY = HostRenderHost.CurrentFrame.PixelHeight / PART_Canvas.ActualHeight;

                    _vm.X = (int)Math.Round(minX * scaleX);
                    _vm.Y = (int)Math.Round(minY * scaleY);
                    _vm.Width = (int)Math.Round((maxX - minX) * scaleX);
                    _vm.Height = (int)Math.Round((maxY - minY) * scaleY);
                }
                else
                {
                    _vm.X = (int)Math.Round(minX);
                    _vm.Y = (int)Math.Round(minY);
                    _vm.Width = (int)Math.Round(maxX - minX);
                    _vm.Height = (int)Math.Round(maxY - minY);
                }

                // Submit to renderer with current (cropped) frame and pass a destination quad in renderer coordinates
                if (RendererManager != null)
                {
                    var layerId = _vm.Model?.Id ?? _vm.Id;
                    if (!string.IsNullOrEmpty(layerId))
                    {
                        BitmapSource? frame = null;
                        try
                        {
                            // If the layer has a source, try to grab last frame (so UI shows live preview)
                            try { if (!string.IsNullOrEmpty(_vm.Model?.SourceId)) _videoService?.TryGetLastFrame(_vm.Model.SourceId, out frame); } catch (Exception exFrame) { Debug.WriteLine($"WriteBackMeshPoints: TryGetLastFrame failed: {exFrame}"); }

                            // If we have a frame and source-side mesh points, crop the frame before submission
                            BitmapSource? frameToSubmit = null;
                            try { frameToSubmit = frame == null ? null : CropFrameToMesh(frame, _vm.MeshPoints); } catch (Exception exCrop) { Debug.WriteLine($"WriteBackMeshPoints: CropFrameToMesh failed: {exCrop}"); frameToSubmit = frame; }

                            // Build destination quad in renderer coordinates (TopLeft, TopRight, BottomLeft, BottomRight)
                            Point[]? destQuad = null;
                            
                            // CALCULATE QUAD LOCALLY to ensure consistency with drag logic
                            // Prioritize Target Monitor dimensions for wired display stability
                            try
                            {
                                var targetMon = _vm.Model?.TargetMonitorIndex ?? -1;
                                int targetW = 0, targetH = 0;
                                
                                if (targetMon >= 0 && RendererManager != null)
                                {
                                    var monSize = RendererManager.GetMonitorRendererSize(targetMon);
                                    if (monSize.Width > 0 && monSize.Height > 0)
                                    {
                                        targetW = monSize.Width;
                                        targetH = monSize.Height;
                                    }
                                }
                                
                                // If no specific monitor or size not found, fallback to HostRenderHost size or OutputWidth
                                if (targetW == 0 && RendererManager != null)
                                {
                                    targetW = RendererManager.OutputWidth;
                                    targetH = RendererManager.OutputHeight;
                                }
                                
                                if (targetW > 0 && targetH > 0)
                                {
                                    // Map normalized points to target (monitor/renderer) dimensions directly
                                    // Use _vm.OutputMeshPoints which contains the latest normalized points
                                    var pts = _vm.OutputMeshPoints;
                                    if (pts != null && pts.Length >= 4)
                                    {
                                        destQuad = new Point[4]
                                        {
                                            new Point(pts[0].X * targetW, pts[0].Y * targetH),
                                            new Point(pts[1].X * targetW, pts[1].Y * targetH),
                                            new Point(pts[2].X * targetW, pts[2].Y * targetH),
                                            new Point(pts[3].X * targetW, pts[3].Y * targetH)
                                        };
                                    }
                                }
                            }
                            catch (Exception exQuad) { Debug.WriteLine($"WriteBackMeshPoints: Local quad calc failed: {exQuad}"); destQuad = null; }

                            // Fallback to MapNormalizedToRendererPoints if local calc failed (should be rare)
                            if (destQuad == null)
                            {
                                try
                                {
                                    destQuad = RendererManager?.MapNormalizedToRendererPoints(_vm.OutputMeshPoints, _vm.Model?.TargetMonitorIndex);
                                }
                                catch { }
                            }

                            // If mapping still failed (no main host frame available), fall back to monitor-based or host-based mapping
                            if (destQuad == null)
                            {
                                var targetMon = _vm.Model?.TargetMonitorIndex ?? -1;
                                
                                // First try: If we have a target monitor, use its size
                                if (targetMon >= 0 && RendererManager != null)
                                {
                                    try
                                    {
                                        var monSize = RendererManager.GetMonitorRendererSize(targetMon);
                                        if (monSize.Width > 0 && monSize.Height > 0 && PART_Canvas.ActualWidth > 0 && PART_Canvas.ActualHeight > 0)
                                        {
                                            var scaleX = monSize.Width / PART_Canvas.ActualWidth;
                                            var scaleY = monSize.Height / PART_Canvas.ActualHeight;
                                            destQuad = new Point[4]
                                            {
                                                new Point(_corners[0].X * scaleX, _corners[0].Y * scaleY),
                                                new Point(_corners[1].X * scaleX, _corners[1].Y * scaleY),
                                                new Point(_corners[2].X * scaleX, _corners[2].Y * scaleY),
                                                new Point(_corners[3].X * scaleX, _corners[3].Y * scaleY)
                                            };
                                        }
                                    }
                                    catch { destQuad = null; }
                                }
                                
                                // Second try: Fall back to host-based mapping using HostRenderHost
                                if (destQuad == null && HostRenderHost != null && RendererManager != null)
                                {
                                    try
                                    {
                                        var cf = HostRenderHost.CurrentFrame;
                                        if (cf != null && cf.PixelWidth > 0 && cf.PixelHeight > 0 && PART_Canvas.ActualWidth > 0 && PART_Canvas.ActualHeight > 0)
                                        {
                                            var scaleX = cf.PixelWidth / PART_Canvas.ActualWidth;
                                            var scaleY = cf.PixelHeight / PART_Canvas.ActualHeight;
                                            destQuad = new Point[4]
                                            {
                                                new Point(_corners[0].X * scaleX, _corners[0].Y * scaleY),
                                                new Point(_corners[1].X * scaleX, _corners[1].Y * scaleY),
                                                new Point(_corners[2].X * scaleX, _corners[2].Y * scaleY),
                                                new Point(_corners[3].X * scaleX, _corners[3].Y * scaleY)
                                            };
                                        }
                                    }
                                    catch { destQuad = null; }
                                }
                            }

                            var destRect = new Rect(_vm.X, _vm.Y, Math.Max(1, _vm.Width), Math.Max(1, _vm.Height));
                            // clamp destQuad to renderer bounds if possible
                            // CRITICAL: Use TARGET MONITOR dimensions, not main renderer dimensions
                            try
                            {
                                if (destQuad != null && RendererManager != null)
                                {
                                    int clampW = RendererManager.OutputWidth;
                                    int clampH = RendererManager.OutputHeight;
                                    
                                    // If targeting a specific monitor, use that monitor's size for clamping
                                    var targetMon = _vm.Model?.TargetMonitorIndex ?? -1;
                                    if (targetMon >= 0)
                                    {
                                        var monSize = RendererManager.GetMonitorRendererSize(targetMon);
                                        if (monSize.Width > 0 && monSize.Height > 0)
                                        {
                                            clampW = monSize.Width;
                                            clampH = monSize.Height;
                                        }
                                    }
                                    
                                    if (clampW > 0 && clampH > 0)
                                    {
                                        for (int i = 0; i < destQuad.Length; ++i)
                                        {
                                            var p = destQuad[i];
                                            p.X = Math.Max(0, Math.Min(clampW, p.X));
                                            p.Y = Math.Max(0, Math.Min(clampH, p.Y));
                                            destQuad[i] = p;
                                        }
                                    }
                                }
                            }
                            catch { }
                            try
                            {
                                // Pass destQuad to RendererManager; send to BOTH main preview AND target monitor
                                var targetMonitor = _vm.Model?.TargetMonitorIndex ?? -1;
                                RendererManager.SubmitLayerFrameForMonitor(layerId, frameToSubmit, destRect, destQuad, _vm.Opacity, targetMonitor);

                                // Also instruct RendererManager to show the mesh overlay on the output host/fullscreen for this layer
                                try
                                {
                                    bool showPoints = true;
                                    // respect per-layer preference if present
                                    var showOverlayPref = _vm.Model?.ShowOverlay ?? true;
                                    if (!showOverlayPref || RendererManager == null)
                                    {
                                        // clear overlay on host if disabled
                                        try { HostRenderHost?.ClearOverlay(); } catch { }
                                    }
                                    else
                                    {
                                        // Map normalized output mesh points to renderer coordinates
                                        Point[]? quadForRenderer = null;
                                        try
                                        {
                                            quadForRenderer = RendererManager?.MapNormalizedToRendererPoints(_vm.OutputMeshPoints, targetMonitor);
                                        }
                                        catch { quadForRenderer = null; }

                                        if (quadForRenderer != null && quadForRenderer.Length >= 4)
                                        {
                                            try
                                            {
                                                RendererManager.AddMeshOverlayForMonitor(targetMonitor >= 0 ? targetMonitor : null, quadForRenderer, showPoints, layerId);
                                            }
                                            catch { }
                                        }
                                        else
                                        {
                                            // fallback: Try monitor-based mapping first, then host-based mapping
                                            Point[]? fallbackQuad = null;
                                            
                                            // First try: If we have a target monitor, use its size
                                            if (targetMonitor >= 0 && RendererManager != null)
                                            {
                                                try
                                                {
                                                    var monSize = RendererManager.GetMonitorRendererSize(targetMonitor);
                                                    if (monSize.Width > 0 && monSize.Height > 0 && PART_Canvas.ActualWidth > 0 && PART_Canvas.ActualHeight > 0)
                                                    {
                                                        var scaleX = monSize.Width / PART_Canvas.ActualWidth;
                                                        var scaleY = monSize.Height / PART_Canvas.ActualHeight;
                                                        fallbackQuad = new Point[4]
                                                        {
                                                            new Point(_corners[0].X * scaleX, _corners[0].Y * scaleY),
                                                            new Point(_corners[1].X * scaleX, _corners[1].Y * scaleY),
                                                            new Point(_corners[2].X * scaleX, _corners[2].Y * scaleY),
                                                            new Point(_corners[3].X * scaleX, _corners[3].Y * scaleY)
                                                        };
                                                    }
                                                }
                                                catch { fallbackQuad = null; }
                                            }
                                            
                                            // Second try: Fall back to host-based mapping using HostRenderHost
                                            if (fallbackQuad == null && HostRenderHost != null && RendererManager != null)
                                            {
                                                try
                                                {
                                                    var scaleX = 1.0;
                                                    var scaleY = 1.0;
                                                    var cf = HostRenderHost.CurrentFrame;
                                                    if (cf != null && cf.PixelWidth > 0 && cf.PixelHeight > 0 && PART_Canvas.ActualWidth > 0 && PART_Canvas.ActualHeight > 0)
                                                    {
                                                        scaleX = cf.PixelWidth / PART_Canvas.ActualWidth;
                                                        scaleY = cf.PixelHeight / PART_Canvas.ActualHeight;
                                                    }
                                                    fallbackQuad = new Point[4]
                                                    {
                                                        new Point(_corners[0].X * scaleX, _corners[0].Y * scaleY),
                                                        new Point(_corners[1].X * scaleX, _corners[1].Y * scaleY),
                                                        new Point(_corners[2].X * scaleX, _corners[2].Y * scaleY),
                                                        new Point(_corners[3].X * scaleX, _corners[3].Y * scaleY)
                                                    };
                                                }
                                                catch { fallbackQuad = null; }
                                            }
                                            
                                            // Use RendererManager to add overlay so it's properly tracked
                                            if (fallbackQuad != null)
                                            {
                                                try { RendererManager.AddMeshOverlayForMonitor(targetMonitor >= 0 ? targetMonitor : null, fallbackQuad, showPoints, layerId); } catch { }
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }
                            catch (Exception exSubmit) { Debug.WriteLine($"WriteBackMeshPoints: SubmitLayerFrame failed: {exSubmit}"); }
                        }
                        catch (Exception exGlobal) { Debug.WriteLine($"WriteBackMeshPoints global failure: {exGlobal}"); }
                    }
                }

                // CRITICAL FIX: Update ALL mesh layers' external overlays after editing ANY mesh point
                // This ensures all mesh layers show their overlays in real-time, not just the selected one
                UpdateAllMeshLayersExternalOverlays();

                // Force re-render mesh layer with cached frame (important for paused videos)
                // NOTE: WriteBackMeshPoints already submitted the frame with the latest destQuad.
                // Calling RefreshMeshLayerRendering here is redundant and potentially dangerous 
                // if it calculates a slightly different quad (floating point jitter), causing "haywire" flickering.
                // We should only rely on the direct submission here providing the immediate feedback.
                // _videoService.RefreshMeshLayerRendering(_vm.Id);
            }
            catch (Exception ex) { Debug.WriteLine($"WriteBackMeshPoints top-level error: {ex}"); }
        }

        /// <summary>
        /// Update external overlays for ALL mesh layers in real-time.
        /// This is called after editing any mesh point to ensure all overlays remain visible and positioned correctly.
        /// </summary>
        private void UpdateAllMeshLayersExternalOverlays()
        {
            if (AllMeshLayers == null || RendererManager == null) return;

            try
            {
                foreach (var meshVm in AllMeshLayers)
                {
                    if (meshVm?.Model == null) continue;
                    UpdateExternalOverlayForSingleLayer(meshVm);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UpdateAllMeshLayersExternalOverlays: Failed: {ex.Message}");
            }
        }

        private static bool IsPointApproximately(System.Numerics.Vector2 point, System.Numerics.Vector2 target, float tolerance = 0.01f)
        {
            return Math.Abs(point.X - target.X) < tolerance && Math.Abs(point.Y - target.Y) < tolerance;
        }

        private void MapSelectedLayerMeshToRects()
        {
            if (_isDisposed) return;

            var vm = _vm;
            if (vm == null)
            {
                _corners[0] = new Point(0, 0);
                _corners[1] = new Point(0, 0);
                _corners[2] = new Point(0, 0);
                _corners[3] = new Point(0, 0);
                ApplyLayout();
                return;
            }

            if (PART_Canvas.ActualWidth <= 0 || PART_Canvas.ActualHeight <= 0)
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(MapSelectedLayerMeshToRects), DispatcherPriority.Loaded);
                }
                catch { }
                return;
            }

            var cw2 = PART_Canvas.ActualWidth;
            var ch2 = PART_Canvas.ActualHeight;

            try
            {
                // Map OutputMeshPoints to canvas corners (order TL, TR, BL, BR)
                var pts = vm.OutputMeshPoints;
                if (pts == null || pts.Length < 4)
                {
                    _corners[0] = new Point(0, 0);
                    _corners[1] = new Point(cw2, 0);
                    _corners[2] = new Point(0, ch2);
                    _corners[3] = new Point(cw2, ch2);
                }
                else
                {
                    _corners[0] = new Point(pts[0].X * cw2, pts[0].Y * ch2);
                    _corners[1] = new Point(pts[1].X * cw2, pts[1].Y * ch2);
                    _corners[2] = new Point(pts[2].X * cw2, pts[2].Y * ch2);
                    _corners[3] = new Point(pts[3].X * cw2, pts[3].Y * ch2);
                }

                ApplyLayout();
            }
            catch { }
        }

        public void Dispose()
        {
            _isDisposed = true;
            if (_videoService != null) try { _videoService.FrameDecoded -= VideoService_FrameDecoded; } catch { }
            
            // Clean up all mesh layers subscription
            if (AllMeshLayers != null)
            {
                AllMeshLayers.CollectionChanged -= AllMeshLayers_CollectionChanged;
                foreach (var layer in AllMeshLayers)
                {
                    try { layer.PropertyChanged -= AllMeshLayer_PropertyChanged; } catch { }
                }
            }
        }

        private BitmapSource? WarpBitmap(BitmapSource? source, Point[] quadPoints, Rect destRect)
        {
            if (source == null || quadPoints.Length < 4) return source;

            var viewport = new Viewport3D();
            var model = new GeometryModel3D();
            var mesh = new MeshGeometry3D();

            mesh.Positions = new Point3DCollection
            {
                new Point3D(quadPoints[0].X, quadPoints[0].Y, 0),
                new Point3D(quadPoints[1].X, quadPoints[1].Y, 0),
                new Point3D(quadPoints[3].X, quadPoints[3].Y, 0),
                new Point3D(quadPoints[2].X, quadPoints[2].Y, 0)
            };

            mesh.TextureCoordinates = new PointCollection
            {
                new Point(0, 0),
                new Point(1, 0),
                new Point(1, 1),
                new Point(0, 1)
            };

            mesh.TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 };

            model.Geometry = mesh;
            model.Material = new DiffuseMaterial(new ImageBrush(source) { Stretch = Stretch.Fill });

            var visual = new ModelVisual3D { Content = model };
            viewport.Children.Add(visual);

            var camera = new OrthographicCamera
            {
                Position = new Point3D(destRect.Width / 2, destRect.Height / 2, 1),
                LookDirection = new Vector3D(0, 0, -1),
                UpDirection = new Vector3D(0, 1, 0),
                Width = destRect.Width
            };

            viewport.Camera = camera;

            var rtb = new RenderTargetBitmap((int)destRect.Width, (int)destRect.Height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(viewport);
            rtb.Freeze();

            return rtb;
        }
    }
}