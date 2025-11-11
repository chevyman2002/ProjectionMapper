using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using ProjectionMapper.Models;
using System.Numerics;

namespace ProjectionMapper.ViewModels
{
    /// <summary>
    /// Main window view model holding the project and basic commands.
    /// Kept small and testable.
    /// </summary>
    public class MainWindowViewModel : BaseViewModel
    {
        private ProjectModel? _project;

        public MainWindowViewModel()
        {
            // Start with an empty project
            _project = new ProjectModel { Name = "Untitled Project" };
            Projects = new ObservableCollection<ProjectModel> { _project };

            // Collection of imported videos (parent nodes)
            ImportedVideos = new ObservableCollection<ImportedVideoViewModel>();

            AddSurfaceCommand = new RelayCommand(ExecuteAddSurface, CanExecuteAddSurface);
            RemoveSurfaceCommand = new RelayCommand(ExecuteRemoveSurface, CanExecuteRemoveSurface);

            ImportCommand = new RelayCommand(ExecuteImportCommand);
            PreviewCommand = new RelayCommand(ExecutePreviewCommand);

            // Use AsyncRelayCommand for playback operations since they're async
            PlayPauseCommand = new AsyncRelayCommand(ExecutePlayPauseCommandAsync);
            RestartCommand = new AsyncRelayCommand(ExecuteRestartCommandAsync);

            CreateMeshCommand = new RelayCommand(ExecuteCreateMeshCommand, _ => SelectedImportedVideo != null);
            DeleteMeshCommand = new RelayCommand(ExecuteDeleteMeshCommand, _ => SelectedMeshLayer != null);
            CopyMeshCommand = new RelayCommand(ExecuteCopyMeshCommand, _ => SelectedMeshLayer != null);
            PasteMeshCommand = new RelayCommand(ExecutePasteMeshCommand, _ => SelectedImportedVideo != null && _copiedMesh != null);

            // Add imported deletion command
            DeleteImportedCommand = new RelayCommand(ExecuteDeleteImportedCommand, p => p is ImportedVideoViewModel);

            // sensible defaults for zoom
            InputZoom = 1.0;
            OutputZoom = 1.0;
        }

        public ObservableCollection<ProjectModel> Projects { get; }

        public ProjectModel? ActiveProject
        {
            get => _project;
            set => SetProperty(ref _project, value);
        }

        // Example property for status text shown in the status bar
        private string _statusText = "Ready";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        // Imported videos shown as tree parents
        public ObservableCollection<ImportedVideoViewModel> ImportedVideos { get; }

        private ImportedVideoViewModel? _selectedImportedVideo;
        public ImportedVideoViewModel? SelectedImportedVideo
        {
            get => _selectedImportedVideo;
            set => SetProperty(ref _selectedImportedVideo, value);
        }

        private LayerViewModel? _selectedMeshLayer;
        public LayerViewModel? SelectedMeshLayer
        {
            get => _selectedMeshLayer;
            set => SetProperty(ref _selectedMeshLayer, value);
        }

        private SurfaceModel? _selectedSurface;
        public SurfaceModel? SelectedSurface
        {
            get => _selectedSurface;
            set
            {
                if (!SetProperty(ref _selectedSurface, value)) return;
                // When surface selection changes, update command states
                (RemoveSurfaceCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public ICommand AddSurfaceCommand { get; }
        public ICommand RemoveSurfaceCommand { get; }

        // Toolbar commands
        public ICommand ImportCommand { get; }
        public ICommand PreviewCommand { get; }

        // Playback - now using AsyncRelayCommand
        public ICommand PlayPauseCommand { get; }
        public ICommand RestartCommand { get; }

        // Mesh tree commands
        public ICommand CreateMeshCommand { get; }
        public ICommand DeleteMeshCommand { get; }
        public ICommand CopyMeshCommand { get; }
        public ICommand PasteMeshCommand { get; }

        // Delete imported video command
        public ICommand DeleteImportedCommand { get; }

        // Events surfaced to the host window so it can perform file dialogs / services
        public event Action? ImportRequested;
        public event Action? PreviewRequested;
        public event Func<System.Threading.Tasks.Task>? PlayPauseRequestedAsync;
        public event Func<System.Threading.Tasks.Task>? RestartRequestedAsync;

        // Event requested when an imported video should be deleted (UI may show confirmation)
        public event Action<ImportedVideoViewModel?>? DeleteImportedRequested;

        // New event: notify host to register mesh layer with services when created
        public event Action<LayerModel?>? MeshLayerCreated;

        // Zoom properties bound to the UI sliders
        private double _inputZoom;
        public double InputZoom
        {
            get => _inputZoom;
            set => SetProperty(ref _inputZoom, value);
        }

        private double _outputZoom;
        public double OutputZoom
        {
            get => _outputZoom;
            set => SetProperty(ref _outputZoom, value);
        }

        private bool CanExecuteAddSurface(object? _) => ActiveProject != null;
        private void ExecuteAddSurface(object? _)
        {
            if (ActiveProject == null) return;
            var surface = new SurfaceModel
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = $"Surface {ActiveProject.Surfaces.Count + 1}"
            };
            ActiveProject.Surfaces.Add(surface);
            SelectedSurface = surface;
        }

        private bool CanExecuteRemoveSurface(object? _) => SelectedSurface != null && ActiveProject != null;
        private void ExecuteRemoveSurface(object? _)
        {
            if (ActiveProject == null || SelectedSurface == null) return;
            ActiveProject.Surfaces.Remove(SelectedSurface);
            // select first surface if available
            SelectedSurface = ActiveProject.Surfaces.Count > 0 ? ActiveProject.Surfaces[0] : null;
        }

        private void ExecuteImportCommand(object? _)
        {
            ImportRequested?.Invoke();
        }

        private void ExecutePreviewCommand(object? _)
        {
            PreviewRequested?.Invoke();
        }

        private bool _isPlaying = true;
        private async System.Threading.Tasks.Task ExecutePlayPauseCommandAsync()
        {
            _isPlaying = !_isPlaying;

            // Notify host to handle the async operation
            if (PlayPauseRequestedAsync != null)
            {
                await PlayPauseRequestedAsync.Invoke();
            }

            // Raise UI updates if you bind icon state
            RaisePropertyChanged(nameof(IsPlaying));
        }

        public bool IsPlaying => _isPlaying;

        private async System.Threading.Tasks.Task ExecuteRestartCommandAsync()
        {
            // Notify host to handle the async operation
            if (RestartRequestedAsync != null)
            {
                await RestartRequestedAsync.Invoke();
            }
        }

        private LayerModel? _copiedMesh;

        private string GenerateUniqueMeshName()
        {
            // Find highest existing "Mesh N" and increment
            int max = 0;
            foreach (var imported in ImportedVideos)
            {
                foreach (var vm in imported.MeshLayers)
                {
                    if (vm.Name != null && vm.Name.StartsWith("Mesh "))
                    {
                        var tail = vm.Name.Substring(5).Trim();
                        if (int.TryParse(tail, out var n)) max = Math.Max(max, n);
                    }
                }
            }
            return $"Mesh {max + 1}";
        }

        private void ExecuteCreateMeshCommand(object? _)
        {
            if (SelectedImportedVideo == null) return;
            // create a mesh layer linked to the host
            var host = SelectedImportedVideo.HostLayer;

            // Default to a centered, smaller rect (20% of host) so multiple layers are easier to work with
            int defaultW = Math.Max(1, host.Width / 5);
            int defaultH = Math.Max(1, host.Height / 5);
            int defaultX = host.X + (host.Width - defaultW) / 2;
            int defaultY = host.Y + (host.Height - defaultH) / 2;

            var layerModel = new LayerModel
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = GenerateUniqueMeshName(),
                SourceId = host?.Id,
                // default to centered smaller bounds
                X = defaultX,
                Y = defaultY,
                Width = defaultW,
                Height = defaultH,
                Visible = true // show output by default
            };

            // Set normalized mesh points so input mapping matches the output rectangle.
            try
            {
                if (host != null && host.Width > 0 && host.Height > 0)
                {
                    var dst = layerModel.MeshPoints;
                    var leftNorm = (float)((double)(defaultX - host.X) / host.Width);
                    var topNorm = (float)((double)(defaultY - host.Y) / host.Height);
                    var wNorm = (float)((double)defaultW / host.Width);
                    var hNorm = (float)((double)defaultH / host.Height);

                    // Clamp
                    leftNorm = Math.Max(0f, Math.Min(1f, leftNorm));
                    topNorm = Math.Max(0f, Math.Min(1f, topNorm));
                    wNorm = Math.Max(0f, Math.Min(1f, wNorm));
                    hNorm = Math.Max(0f, Math.Min(1f, hNorm));

                    dst[0] = new Vector2(leftNorm, topNorm); // TL
                    dst[1] = new Vector2(leftNorm + wNorm, topNorm); // TR
                    dst[2] = new Vector2(leftNorm, topNorm + hNorm); // BL
                    dst[3] = new Vector2(leftNorm + wNorm, topNorm + hNorm); // BR
                }
                else
                {
                    // fallback to full rect
                    var dst = layerModel.MeshPoints;
                    dst[0] = new Vector2(0f, 0f);
                    dst[1] = new Vector2(1f, 0f);
                    dst[2] = new Vector2(0f, 1f);
                    dst[3] = new Vector2(1f, 1f);
                }
            }
            catch { }

            var vm = new LayerViewModel(layerModel);
            SelectedImportedVideo.MeshLayers.Add(vm);
            SelectedMeshLayer = vm;

            // notify host so it can register this mesh with services (VideoService)
            MeshLayerCreated?.Invoke(layerModel);

            // Prevent host from submitting full-frame into renderer (avoid duplicate output)
            try
            {
                if (host != null)
                {
                    host.PreviewOnly = true;
                }
            }
            catch { }
        }

        private void ExecuteDeleteMeshCommand(object? _)
        {
            if (SelectedImportedVideo == null || SelectedMeshLayer == null) return;

            var removed = SelectedMeshLayer;
            SelectedImportedVideo.MeshLayers.Remove(SelectedMeshLayer);

            // if no remaining meshes reference the host, restore host preview behavior
            try
            {
                var host = SelectedImportedVideo.HostLayer;
                if (host != null)
                {
                    bool any = SelectedImportedVideo.MeshLayers.Any(m => string.Equals(m.Model.SourceId, host.Id, StringComparison.OrdinalIgnoreCase));
                    if (!any)
                    {
                        host.PreviewOnly = false;
                    }
                }
            }
            catch { }

            SelectedMeshLayer = null;
        }

        private void ExecuteCopyMeshCommand(object? _)
        {
            if (SelectedMeshLayer == null) return;
            // copy dimensions and mesh points into a temp LayerModel for paste
            var model = new LayerModel
            {
                Width = SelectedMeshLayer.Width,
                Height = SelectedMeshLayer.Height,
                X = SelectedMeshLayer.X,
                Y = SelectedMeshLayer.Y
            };

            try
            {
                var src = SelectedMeshLayer.Model.MeshPoints;
                var dst = model.MeshPoints;
                var len = Math.Min(src.Length, dst.Length);
                for (int i = 0; i < len; ++i) dst[i] = src[i];
            }
            catch { }

            _copiedMesh = model;
        }

        private void ExecutePasteMeshCommand(object? _)
        {
            if (SelectedImportedVideo == null || _copiedMesh == null) return;
            var host = SelectedImportedVideo.HostLayer;

            // If host exists, center pasted mesh on host; otherwise use copied coords
            int defaultX = _copiedMesh.X, defaultY = _copiedMesh.Y, defaultW = _copiedMesh.Width, defaultH = _copiedMesh.Height;
            if (host != null && host.Width > 0 && host.Height > 0)
            {
                defaultW = Math.Max(1, Math.Min(_copiedMesh.Width > 0 ? _copiedMesh.Width : host.Width / 2, host.Width));
                defaultH = Math.Max(1, Math.Min(_copiedMesh.Height > 0 ? _copiedMesh.Height : host.Height / 2, host.Height));
                defaultX = host.X + (host.Width - defaultW) / 2;
                defaultY = host.Y + (host.Height - defaultH) / 2;
            }

            var copied = new LayerModel
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = GenerateUniqueMeshName(),
                X = defaultX,
                Y = defaultY,
                Width = defaultW,
                Height = defaultH,
                SourceId = host?.Id,
                Visible = true
            };

            try
            {
                var src = _copiedMesh.MeshPoints;
                var dst = copied.MeshPoints;
                var len = Math.Min(src.Length, dst.Length);
                for (int i = 0; i < len; ++i) dst[i] = src[i];
            }
            catch { }

            var vm = new LayerViewModel(copied);
            SelectedImportedVideo.MeshLayers.Add(vm);
            SelectedMeshLayer = vm;

            MeshLayerCreated?.Invoke(copied);

            // prevent host full-frame output to avoid duplicate
            try { if (host != null) host.PreviewOnly = true; } catch { }
        }

        private void ExecuteDeleteImportedCommand(object? param)
        {
            var imported = param as ImportedVideoViewModel;
            DeleteImportedRequested?.Invoke(imported);
        }
    }
}