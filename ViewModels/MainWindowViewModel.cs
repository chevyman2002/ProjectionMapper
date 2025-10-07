using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using ProjectionMapper.Models;

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

            PlayPauseCommand = new RelayCommand(ExecutePlayPauseCommand);
            RestartCommand = new RelayCommand(ExecuteRestartCommand);

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

        // Playback
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
        public event Action? PlayPauseRequested;
        public event Action? RestartRequested;

        // Event requested when an imported video should be deleted (UI may show confirmation)
        public event Action<ImportedVideoViewModel?>? DeleteImportedRequested;

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
        private void ExecutePlayPauseCommand(object? _)
        {
            _isPlaying = !_isPlaying;
            PlayPauseRequested?.Invoke();
            // Raise UI updates if you bind icon state
            RaisePropertyChanged(nameof(IsPlaying));
        }

        public bool IsPlaying => _isPlaying;

        private void ExecuteRestartCommand(object? _)
        {
            RestartRequested?.Invoke();
        }

        private LayerModel? _copiedMesh;

        private void ExecuteCreateMeshCommand(object? _)
        {
            if (SelectedImportedVideo == null) return;
            // create an empty/default layer (not registered with decoder until user sets source)
            var layerModel = new LayerModel { Id = Guid.NewGuid().ToString("N"), Name = "Mesh Layer" };
            var vm = new LayerViewModel(layerModel);
            SelectedImportedVideo.MeshLayers.Add(vm);
            SelectedMeshLayer = vm;
        }

        private void ExecuteDeleteMeshCommand(object? _)
        {
            if (SelectedImportedVideo == null || SelectedMeshLayer == null) return;
            SelectedImportedVideo.MeshLayers.Remove(SelectedMeshLayer);
            SelectedMeshLayer = null;
        }

        private void ExecuteCopyMeshCommand(object? _)
        {
            if (SelectedMeshLayer == null) return;
            // copy dimensions and mesh points into a temp LayerModel for paste
            _copiedMesh = new LayerModel
            {
                Width = SelectedMeshLayer.Width,
                Height = SelectedMeshLayer.Height,
                X = SelectedMeshLayer.X,
                Y = SelectedMeshLayer.Y
            };
        }

        private void ExecutePasteMeshCommand(object? _)
        {
            if (SelectedImportedVideo == null || _copiedMesh == null) return;
            var copied = new LayerModel
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Mesh Layer",
                X = _copiedMesh.X,
                Y = _copiedMesh.Y,
                Width = _copiedMesh.Width,
                Height = _copiedMesh.Height
            };
            var vm = new LayerViewModel(copied);
            SelectedImportedVideo.MeshLayers.Add(vm);
            SelectedMeshLayer = vm;
        }

        private void ExecuteDeleteImportedCommand(object? param)
        {
            var imported = param as ImportedVideoViewModel;
            DeleteImportedRequested?.Invoke(imported);
        }
    }
}