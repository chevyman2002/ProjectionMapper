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
        private LayerViewModel? _vm;
        private VideoService? _videoService;
        private bool _isDisposed = false;

        // corners in canvas coordinates: TL, TR, BL, BR
        private Point[] _corners = new Point[4]
        {
            new Point(100,20),
            new Point(420,20),
            new Point(100,200),
            new Point(420,200)
        };

        private bool _suppressVmRebind = false;

        public OutputMeshEditorControl()
        {
            InitializeComponent();

            // corner handles -> move corners independently
            PART_Handle_TL.DragDelta += (s, e) => MoveCorner(0, e.HorizontalChange, e.VerticalChange);
            PART_Handle_TR.DragDelta += (s, e) => MoveCorner(1, e.HorizontalChange, e.VerticalChange);
            PART_Handle_BL.DragDelta += (s, e) => MoveCorner(2, e.HorizontalChange, e.VerticalChange);
            PART_Handle_BR.DragDelta += (s, e) => MoveCorner(3, e.HorizontalChange, e.VerticalChange);

            Loaded += (_, __) => ApplyLayout();
            Unloaded += (_, __) => OnUnloaded();
        }

        private void OnUnloaded()
        {
            _isDisposed = true;
            if (_videoService != null)
            {
                try { _videoService.FrameDecoded -= VideoService_FrameDecoded; } catch { }
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
                // Clear the old layer from renderer
                var layerId = oldVm.Model?.Id ?? oldVm.Id;
                if (!string.IsNullOrEmpty(layerId))
                {
                    // clear layer by submitting null frame; no destQuad -> null, opacity 0
                    RendererManager?.SubmitLayerFrame(layerId, null, new Rect(), null, 0.0);
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

            PART_OutputPolygon.Visibility = Visibility.Visible;
            PART_Handle_TL.Visibility = Visibility.Visible;
            PART_Handle_TR.Visibility = Visibility.Visible;
            PART_Handle_BL.Visibility = Visibility.Visible;
            PART_Handle_BR.Visibility = Visibility.Visible;
            PART_CroppedPreview.Visibility = Visibility.Visible;

            MapSelectedLayerMeshToRects();
            TryRefreshPreviewFromLastFrame();
        }

        private void SelectedLayer_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_isDisposed || _suppressVmRebind) return;
            if (e == null || string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(LayerViewModel.MeshPoints) ||
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
            var maxY = Math.Max(Math.Max(tl.Y, tr.Y), Math.Min(bl.Y, br.Y));

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
                            try
                            {
                                if (HostRenderHost != null && HostRenderHost.CurrentFrame != null && PART_Canvas.ActualWidth > 0 && PART_Canvas.ActualHeight > 0)
                                {
                                    var scaleX = HostRenderHost.CurrentFrame.PixelWidth / PART_Canvas.ActualWidth;
                                    var scaleY = HostRenderHost.CurrentFrame.PixelHeight / PART_Canvas.ActualHeight;

                                    destQuad = new Point[4]
                                    {
                                        new Point(_corners[0].X * scaleX, _corners[0].Y * scaleY), // TL
                                        new Point(_corners[1].X * scaleX, _corners[1].Y * scaleY), // TR
                                        new Point(_corners[2].X * scaleX, _corners[2].Y * scaleY), // BL
                                        new Point(_corners[3].X * scaleX, _corners[3].Y * scaleY)  // BR
                                    };
                                }
                                else
                                {
                                    destQuad = new Point[4]
                                    {
                                        new Point(_corners[0].X, _corners[0].Y), // TL
                                        new Point(_corners[1].X, _corners[1].Y), // TR
                                        new Point(_corners[2].X, _corners[2].Y), // BL
                                        new Point(_corners[3].X, _corners[3].Y)  // BR
                                    };
                                }
                            }
                            catch (Exception exQuad) { Debug.WriteLine($"WriteBackMeshPoints: create destQuad failed: {exQuad}"); destQuad = null; }

                            var destRect = new Rect(_vm.X, _vm.Y, Math.Max(1, _vm.Width), Math.Max(1, _vm.Height));
                            try
                            {
                                // Pass destQuad to RendererManager; it will pass through to the renderer which will warp if supported.
                                RendererManager.SubmitLayerFrame(layerId, frameToSubmit, destRect, destQuad, _vm.Opacity);
                            }
                            catch (Exception exSubmit) { Debug.WriteLine($"WriteBackMeshPoints: SubmitLayerFrame failed: {exSubmit}"); }
                        }
                        catch (Exception exGlobal) { Debug.WriteLine($"WriteBackMeshPoints global failure: {exGlobal}"); }
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"WriteBackMeshPoints top-level error: {ex}"); }
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