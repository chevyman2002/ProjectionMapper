using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProjectionMapper.Rendering;
using ProjectionMapper.Services;
using ProjectionMapper.Models;
using ProjectionMapper.ViewModels;

namespace ProjectionMapper
{
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _vm;

        // Renderer and manager
        private readonly SoftwareRenderer _softwareRenderer;
        private readonly RendererManager _rendererManager;

        // Services
        private readonly VideoService _videoService;
        private readonly FileDialogService _fileDialog;

        public MainWindow()
        {
            InitializeComponent();

            _vm = new MainWindowViewModel();
            DataContext = _vm;

            // Create a software renderer and renderer manager, attach the host (output preview)
            _softwareRenderer = new SoftwareRenderer();
            _rendererManager = new RendererManager(_softwareRenderer);
            // Attach to the output host so the composed output shows on the right panel
            _rendererManager.AttachHost(PART_OutputHost);

            // Make the renderer manager available as a resource so child controls can find it if needed
            this.Resources["RendererManager"] = _rendererManager;

            // Create VideoService (ffmpeg path empty -> expects ffmpeg on PATH)
            _videoService = new VideoService(_rendererManager, ffmpegPath: null);
            _fileDialog = new FileDialogService();

            // Expose VideoService so MeshEditorControl can subscribe for isolated previews
            this.Resources["VideoService"] = _videoService;

            // Wire ViewModel events
            _vm.ImportRequested += async () => await HandleImportAsync();
            _vm.PreviewRequested += () => { _vm.StatusText = "Preview requested"; };
            _vm.DeleteImportedRequested += async imported => await HandleDeleteImportedAsync(imported);

            // Start renderer (use output host size; fallback to 1280x720)
            Loaded += async (_, __) =>
            {
                var hostForSize = PART_OutputHost ?? PART_InputHost;
                var w = (int)Math.Max(1, hostForSize.ActualWidth);
                var h = (int)Math.Max(1, hostForSize.ActualHeight);
                if (w == 0 || h == 0) { w = 1280; h = 720; }
                await _rendererManager.StartAsync(w, h);
            };

            Closing += async (_, __) =>
            {
                // Unregister/cleanup services
                try { await _videoService.UnregisterLayerAsync(""); } catch { }
                _videoService.Dispose();
                _rendererManager.Dispose();
            };
        }

        private async Task HandleImportAsync()
        {
            try
            {
                // Show open file dialog - only single file in this simple UI
                var path = await _fileDialog.ShowOpenFileDialogAsync("Import video", "Video files|*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.webm|All files|*.*");
                if (string.IsNullOrEmpty(path)) return;
                if (!File.Exists(path)) return;

                // Create an ImportedVideoViewModel (parent node)
                var id = Guid.NewGuid().ToString("N");
                var importedVm = new ImportedVideoViewModel(id, Path.GetFileName(path), path);

                // Create a host decoding layer so the video is available for isolated preview
                var hostLayer = new LayerModel
                {
                    Id = id,
                    Name = importedVm.Name,
                    SourcePath = path,
                    // mark as preview-only so frames do not get submitted to main renderer
                    PreviewOnly = true
                };

                // Determine default decode size from input host if available
                var hostForLayer = PART_InputHost ?? PART_OutputHost;
                var w = (int)Math.Max(1, hostForLayer.ActualWidth);
                var h = (int)Math.Max(1, hostForLayer.ActualHeight);
                if (w == 0 || h == 0) { w = 1280; h = 720; }
                hostLayer.X = 0; hostLayer.Y = 0; hostLayer.Width = w; hostLayer.Height = h;

                // Register decoder for the imported video so MeshEditor can show isolated preview
                await _videoService.RegisterLayerAsync(hostLayer);
                importedVm.HostLayer = hostLayer;

                // Add to view model collection (parent node). Do NOT create mesh layer automatically.
                _vm.ImportedVideos.Add(importedVm);

                // Select the imported video in the VM
                _vm.SelectedImportedVideo = importedVm;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to import video: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task HandleDeleteImportedAsync(ImportedVideoViewModel? imported)
        {
            if (imported == null) return;

            var res = MessageBox.Show(this, $"Delete imported source '{imported.Name}' and all its mesh layers?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;

            try
            {
                // Unregister any host decoder
                if (imported.HostLayer != null && !string.IsNullOrEmpty(imported.HostLayer.Id))
                {
                    await _videoService.UnregisterLayerAsync(imported.HostLayer.Id);
                }
            }
            catch { }

            // Remove all nested mesh layers
            try
            {
                imported.MeshLayers.Clear();
            }
            catch { }

            // Remove from VM collection
            _vm.ImportedVideos.Remove(imported);

            // Clear selection if it was the deleted one
            if (_vm.SelectedImportedVideo == imported) _vm.SelectedImportedVideo = null;
            if (_vm.SelectedMeshLayer != null && imported.MeshLayers.Contains(_vm.SelectedMeshLayer)) _vm.SelectedMeshLayer = null;
        }

        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            // If a mesh LayerViewModel is selected, set SelectedMeshLayer on VM
            if (e.NewValue is LayerViewModel layerVm)
            {
                _vm.SelectedMeshLayer = layerVm;
                return;
            }

            // If an imported video (parent) is selected, set SelectedImportedVideo
            if (e.NewValue is ImportedVideoViewModel imported)
            {
                _vm.SelectedImportedVideo = imported;
                // Optionally select nothing for mesh layer
                _vm.SelectedMeshLayer = null;
                return;
            }

            // Otherwise clear selections
            _vm.SelectedImportedVideo = null;
            _vm.SelectedMeshLayer = null;
        }

        // Ensure right-click selects the TreeViewItem under the mouse so context menu actions apply to it
        private void TreeView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var clickedElement = e.OriginalSource as DependencyObject;
            if (clickedElement == null) return;

            var tvi = VisualUpwardSearch<TreeViewItem>(clickedElement);
            if (tvi != null)
            {
                tvi.IsSelected = true;
                // do not mark handled so ContextMenu still opens
            }
        }

        private static T? VisualUpwardSearch<T>(DependencyObject source) where T : DependencyObject
        {
            while (source != null && !(source is T))
            {
                source = VisualTreeHelper.GetParent(source);
            }
            return source as T;
        }

        // Context menu click handlers
        private void CreateMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var mi = sender as MenuItem;
            var cm = FindParentContextMenu(mi);
            var imported = (cm?.PlacementTarget as FrameworkElement)?.DataContext as ImportedVideoViewModel;
            if (imported != null)
            {
                _vm.SelectedImportedVideo = imported;
                if (_vm.CreateMeshCommand.CanExecute(null)) _vm.CreateMeshCommand.Execute(null);
            }
        }

        private void DeleteSourceMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var mi = sender as MenuItem;
            var cm = FindParentContextMenu(mi);
            var imported = (cm?.PlacementTarget as FrameworkElement)?.DataContext as ImportedVideoViewModel;
            if (imported != null)
            {
                // Use VM event so confirmation and deletion flow is centralized
                _vm.DeleteImportedCommand.Execute(imported);
            }
        }

        private void DeleteMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var mi = sender as MenuItem;
            var cm = FindParentContextMenu(mi);
            var layer = (cm?.PlacementTarget as FrameworkElement)?.DataContext as LayerViewModel;
            if (layer != null)
            {
                var parent = _vm.ImportedVideos.FirstOrDefault(iv => iv.MeshLayers.Contains(layer));
                if (parent != null)
                {
                    parent.MeshLayers.Remove(layer);
                    if (_vm.SelectedMeshLayer == layer) _vm.SelectedMeshLayer = null;
                }
            }
        }

        private void CopyMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var mi = sender as MenuItem;
            var cm = FindParentContextMenu(mi);
            var layer = (cm?.PlacementTarget as FrameworkElement)?.DataContext as LayerViewModel;
            if (layer != null)
            {
                _vm.SelectedMeshLayer = layer;
                if (_vm.CopyMeshCommand.CanExecute(null)) _vm.CopyMeshCommand.Execute(null);
            }
        }

        private void PasteMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var mi = sender as MenuItem;
            var cm = FindParentContextMenu(mi);
            var fe = cm?.PlacementTarget as FrameworkElement;
            if (fe?.DataContext is ImportedVideoViewModel imported)
            {
                _vm.SelectedImportedVideo = imported;
                if (_vm.PasteMeshCommand.CanExecute(null)) _vm.PasteMeshCommand.Execute(null);
            }
            else if (fe?.DataContext is LayerViewModel layer)
            {
                var parent = _vm.ImportedVideos.FirstOrDefault(iv => iv.MeshLayers.Contains(layer));
                if (parent != null)
                {
                    _vm.SelectedImportedVideo = parent;
                    if (_vm.PasteMeshCommand.CanExecute(null)) _vm.PasteMeshCommand.Execute(null);
                }
            }
        }

        private static ContextMenu? FindParentContextMenu(DependencyObject d)
        {
            while (d != null)
            {
                if (d is ContextMenu cm) return cm;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }
    }
}