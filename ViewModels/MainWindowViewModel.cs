using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using ProjectionMapper.Models;
using System.Numerics;
using ProjectionMapper.Services;

namespace ProjectionMapper.ViewModels
{
    /// <summary>
    /// Main window view model holding the project and basic commands.
    /// Kept small and testable.
    /// </summary>
    public class MainWindowViewModel : BaseViewModel
    {
        private ProjectModel? _project;
        private readonly UndoRedoService _undoRedoService;

        // Track whether the project has unsaved changes
        private bool _hasUnsavedChanges;
        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            set => SetProperty(ref _hasUnsavedChanges, value);
        }

        public MainWindowViewModel()
        {
            // Initialize undo/redo service
            _undoRedoService = new UndoRedoService();
            _undoRedoService.CanUndoChanged += (s, e) => 
            {
                (UndoCommand as RelayCommand)?.RaiseCanExecuteChanged();
            };
            _undoRedoService.CanRedoChanged += (s, e) => 
            {
                (RedoCommand as RelayCommand)?.RaiseCanExecuteChanged();
            };
            
            // Track changes via undo/redo service - if there are actions, the project is dirty
            _undoRedoService.ActionRecorded += (s, e) => 
            {
                HasUnsavedChanges = true;
            };
            
            // Start with an empty project
            _project = new ProjectModel { Name = "Untitled Project" };
            Projects = new ObservableCollection<ProjectModel> { _project };

            // Collection of imported videos (parent nodes)
            ImportedVideos = new ObservableCollection<ImportedVideoViewModel>();
            
            // Track changes when imported videos collection changes
            ImportedVideos.CollectionChanged += (s, e) => 
            {
                HasUnsavedChanges = true;
            };

            // Initialize playlist groups collection
            PlaylistGroups = new ObservableCollection<PlaylistGroupViewModel>();
            PlaylistGroups.CollectionChanged += (s, e) =>
            {
                HasUnsavedChanges = true;
                RaisePropertyChanged(nameof(PlaylistGroupCount));
            };

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

            // Undo/Redo commands
            UndoCommand = new RelayCommand(_ => _undoRedoService.Undo(), _ => _undoRedoService.CanUndo);
            RedoCommand = new RelayCommand(_ => _undoRedoService.Redo(), _ => _undoRedoService.CanRedo);

            // File operation commands  
            SaveProjectCommand = new AsyncRelayCommand(async _ =>
            {
                if (SaveProjectRequested != null)
                    await SaveProjectRequested.Invoke();
            }, _ => true); // Always allow save
            SaveAsProjectCommand = new AsyncRelayCommand(async _ =>
            {
                if (SaveAsProjectRequested != null)
                    await SaveAsProjectRequested.Invoke();
            }, _ => true); // Always allow save as
            LoadProjectCommand = new AsyncRelayCommand(async _ =>
            {
                if (LoadProjectRequested != null)
                    await LoadProjectRequested.Invoke();
            });
            NewProjectCommand = new AsyncRelayCommand(async _ =>
            {
                if (NewProjectRequested != null)
                    await NewProjectRequested.Invoke();
            });
            PreviewCommand = new RelayCommand(_ => PreviewRequested?.Invoke());

            // Playlist commands
            CreateGroupCommand = new RelayCommand(ExecuteCreateGroupCommand);
            DeleteGroupCommand = new RelayCommand(ExecuteDeleteGroupCommand, _ => SelectedPlaylistGroup != null);
            AddVideoToGroupCommand = new RelayCommand(ExecuteAddVideoToGroupCommand, _ => SelectedPlaylistGroup != null && SelectedImportedVideo != null);
            RemoveVideoFromGroupCommand = new RelayCommand(ExecuteRemoveVideoFromGroupCommand, _ => SelectedPlaylistGroup != null && SelectedImportedVideo != null);
            TogglePlaylistModeCommand = new RelayCommand(ExecuteTogglePlaylistModeCommand);
            MoveGroupUpCommand = new RelayCommand(ExecuteMoveGroupUpCommand, _ => SelectedPlaylistGroup != null && SelectedPlaylistGroup.Order > 0);
            MoveGroupDownCommand = new RelayCommand(ExecuteMoveGroupDownCommand, _ => SelectedPlaylistGroup != null && SelectedPlaylistGroup.Order < PlaylistGroups.Count - 1);

            // sensible defaults for zoom
            InputZoom = 1.0;
            OutputZoom = 1.0;
        }

        public UndoRedoService UndoRedoService => _undoRedoService;

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

        // Playlist groups for group-based playback
        public ObservableCollection<PlaylistGroupViewModel> PlaylistGroups { get; }

        private ImportedVideoViewModel? _selectedImportedVideo;
        public ImportedVideoViewModel? SelectedImportedVideo
        {
            get => _selectedImportedVideo;
            set
            {
                if (SetProperty(ref _selectedImportedVideo, value))
                {
                    // Update command states when selection changes
                    (AddVideoToGroupCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (RemoveVideoFromGroupCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        private PlaylistGroupViewModel? _selectedPlaylistGroup;
        /// <summary>
        /// Gets or sets the currently selected playlist group.
        /// </summary>
        public PlaylistGroupViewModel? SelectedPlaylistGroup
        {
            get => _selectedPlaylistGroup;
            set
            {
                if (SetProperty(ref _selectedPlaylistGroup, value))
                {
                    // Update command states when selection changes
                    (DeleteGroupCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (AddVideoToGroupCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (RemoveVideoFromGroupCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (MoveGroupUpCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (MoveGroupDownCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        private PlaylistGroupViewModel? _currentPlaylistGroup;
        /// <summary>
        /// Gets or sets the currently playing playlist group (active during playback).
        /// </summary>
        public PlaylistGroupViewModel? CurrentPlaylistGroup
        {
            get => _currentPlaylistGroup;
            set => SetProperty(ref _currentPlaylistGroup, value);
        }

        private bool _isPlaylistMode;
        /// <summary>
        /// Gets or sets whether the project is in playlist mode (group-based sequential playback).
        /// </summary>
        public bool IsPlaylistMode
        {
            get => _isPlaylistMode;
            set
            {
                if (SetProperty(ref _isPlaylistMode, value))
                {
                    HasUnsavedChanges = true;
                    RaisePropertyChanged(nameof(PlaylistModeText));
                }
            }
        }

        /// <summary>
        /// Gets text describing the current playlist mode state.
        /// </summary>
        public string PlaylistModeText => IsPlaylistMode ? "Playlist Mode" : "Legacy Mode";

        /// <summary>
        /// Gets the number of playlist groups.
        /// </summary>
        public int PlaylistGroupCount => PlaylistGroups.Count;

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

        // Undo/Redo commands
        public ICommand UndoCommand { get; }
        public ICommand RedoCommand { get; }

        // File operations
        public ICommand SaveProjectCommand { get; }
        public ICommand SaveAsProjectCommand { get; }
        public ICommand LoadProjectCommand { get; }
        public ICommand NewProjectCommand { get; }

        // Playlist commands
        public ICommand CreateGroupCommand { get; }
        public ICommand DeleteGroupCommand { get; }
        public ICommand AddVideoToGroupCommand { get; }
        public ICommand RemoveVideoFromGroupCommand { get; }
        public ICommand TogglePlaylistModeCommand { get; }
        public ICommand MoveGroupUpCommand { get; }
        public ICommand MoveGroupDownCommand { get; }

        // Events surfaced to the host window so it can perform file dialogs / services
        public event Action? ImportRequested;
        public event Action? PreviewRequested;
        public event Func<System.Threading.Tasks.Task>? PlayPauseRequestedAsync;
        public event Func<System.Threading.Tasks.Task>? RestartRequestedAsync;

        // Event requested when an imported video should be deleted (UI may show confirmation)
        public event Action<ImportedVideoViewModel?>? DeleteImportedRequested;

        // New event: notify host to register mesh layer with services when created
        public event Action<LayerModel?>? MeshLayerCreated;

        // New events for file operations
        public event Func<System.Threading.Tasks.Task>? SaveProjectRequested;
        public event Func<System.Threading.Tasks.Task>? SaveAsProjectRequested;
        public event Func<System.Threading.Tasks.Task>? LoadProjectRequested;
        public event Func<System.Threading.Tasks.Task>? NewProjectRequested;

        /// <summary>
        /// Mark the project as clean (no unsaved changes). Call this after successful save or load operations.
        /// </summary>
        public void MarkProjectClean()
        {
            HasUnsavedChanges = false;
        }

        /// <summary>
        /// Mark the project as dirty (has unsaved changes). Call this when project is modified.
        /// </summary>
        public void MarkProjectDirty()
        {
            HasUnsavedChanges = true;
        }

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
            // Importing will trigger collection changed which automatically marks dirty
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
            
            // Record undo action for mesh creation
            try
            {
                var action = new CreateMeshAction(SelectedImportedVideo, vm, MeshLayerCreated);
                _undoRedoService.RecordAction(action);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to record create mesh action: {ex}");
            }

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
            
            // Record undo action for mesh deletion
            try
            {
                var action = new DeleteMeshAction(SelectedImportedVideo, removed, MeshLayerCreated);
                _undoRedoService.RecordAction(action);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to record delete mesh action: {ex}");
            }

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

        private void ExecuteSaveProjectCommand(object? _)
        {
            // Raise the save project event
            SaveProjectRequested?.Invoke();
        }

        private void ExecuteLoadProjectCommand(object? _)
        {
            // Raise the load project event
            LoadProjectRequested?.Invoke();
        }

        #region Playlist Commands

        private int _nextGroupNumber = 1;

        private void ExecuteCreateGroupCommand(object? _)
        {
            try
            {
                var model = new PlaylistGroupModel
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = $"Group {_nextGroupNumber++}",
                    Order = PlaylistGroups.Count
                };

                var vm = new PlaylistGroupViewModel(model);
                PlaylistGroups.Add(vm);
                SelectedPlaylistGroup = vm;

                // If playlist mode is not enabled, enable it when first group is created
                if (!IsPlaylistMode && PlaylistGroups.Count == 1)
                {
                    IsPlaylistMode = true;
                }

                HasUnsavedChanges = true;
                System.Diagnostics.Debug.WriteLine($"Created playlist group: {model.Name}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExecuteCreateGroupCommand failed: {ex}");
            }
        }

        private void ExecuteDeleteGroupCommand(object? _)
        {
            if (SelectedPlaylistGroup == null) return;

            try
            {
                var groupToDelete = SelectedPlaylistGroup;
                var index = PlaylistGroups.IndexOf(groupToDelete);
                
                PlaylistGroups.Remove(groupToDelete);

                // Reorder remaining groups
                for (int i = 0; i < PlaylistGroups.Count; i++)
                {
                    PlaylistGroups[i].Order = i;
                }

                // Select adjacent group if available
                if (PlaylistGroups.Count > 0)
                {
                    SelectedPlaylistGroup = PlaylistGroups[Math.Max(0, Math.Min(index, PlaylistGroups.Count - 1))];
                }
                else
                {
                    SelectedPlaylistGroup = null;
                }

                HasUnsavedChanges = true;
                System.Diagnostics.Debug.WriteLine($"Deleted playlist group: {groupToDelete.Name}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExecuteDeleteGroupCommand failed: {ex}");
            }
        }

        private void ExecuteAddVideoToGroupCommand(object? _)
        {
            if (SelectedPlaylistGroup == null || SelectedImportedVideo == null) return;

            try
            {
                var sourceId = SelectedImportedVideo.Id;
                
                // Check if the video is already in this group
                if (SelectedPlaylistGroup.Model.SourceIds.Contains(sourceId))
                {
                    System.Diagnostics.Debug.WriteLine($"Video {SelectedImportedVideo.Name} already in group {SelectedPlaylistGroup.Name}");
                    return;
                }

                // Add to model's source IDs
                SelectedPlaylistGroup.Model.SourceIds.Add(sourceId);
                
                // Add to view model's Videos collection
                SelectedPlaylistGroup.Videos.Add(SelectedImportedVideo);
                SelectedPlaylistGroup.RefreshVideoCount();

                HasUnsavedChanges = true;
                System.Diagnostics.Debug.WriteLine($"Added video {SelectedImportedVideo.Name} to group {SelectedPlaylistGroup.Name}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExecuteAddVideoToGroupCommand failed: {ex}");
            }
        }

        private void ExecuteRemoveVideoFromGroupCommand(object? _)
        {
            if (SelectedPlaylistGroup == null || SelectedImportedVideo == null) return;

            try
            {
                var sourceId = SelectedImportedVideo.Id;
                
                // Remove from model's source IDs
                SelectedPlaylistGroup.Model.SourceIds.Remove(sourceId);
                
                // Remove from view model's Videos collection
                var videoToRemove = SelectedPlaylistGroup.Videos.FirstOrDefault(v => v.Id == sourceId);
                if (videoToRemove != null)
                {
                    SelectedPlaylistGroup.Videos.Remove(videoToRemove);
                }
                SelectedPlaylistGroup.RefreshVideoCount();

                HasUnsavedChanges = true;
                System.Diagnostics.Debug.WriteLine($"Removed video {SelectedImportedVideo.Name} from group {SelectedPlaylistGroup.Name}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExecuteRemoveVideoFromGroupCommand failed: {ex}");
            }
        }

        private void ExecuteTogglePlaylistModeCommand(object? _)
        {
            try
            {
                IsPlaylistMode = !IsPlaylistMode;
                System.Diagnostics.Debug.WriteLine($"Playlist mode toggled: {IsPlaylistMode}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExecuteTogglePlaylistModeCommand failed: {ex}");
            }
        }

        private void ExecuteMoveGroupUpCommand(object? _)
        {
            if (SelectedPlaylistGroup == null || SelectedPlaylistGroup.Order <= 0) return;

            try
            {
                var currentIndex = SelectedPlaylistGroup.Order;
                var targetIndex = currentIndex - 1;

                // Swap with the group above
                var otherGroup = PlaylistGroups.FirstOrDefault(g => g.Order == targetIndex);
                if (otherGroup != null)
                {
                    otherGroup.Order = currentIndex;
                }
                SelectedPlaylistGroup.Order = targetIndex;

                // Re-sort the collection
                var sorted = PlaylistGroups.OrderBy(g => g.Order).ToList();
                PlaylistGroups.Clear();
                foreach (var g in sorted)
                {
                    PlaylistGroups.Add(g);
                }

                // Update command states
                (MoveGroupUpCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (MoveGroupDownCommand as RelayCommand)?.RaiseCanExecuteChanged();

                HasUnsavedChanges = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExecuteMoveGroupUpCommand failed: {ex}");
            }
        }

        private void ExecuteMoveGroupDownCommand(object? _)
        {
            if (SelectedPlaylistGroup == null || SelectedPlaylistGroup.Order >= PlaylistGroups.Count - 1) return;

            try
            {
                var currentIndex = SelectedPlaylistGroup.Order;
                var targetIndex = currentIndex + 1;

                // Swap with the group below
                var otherGroup = PlaylistGroups.FirstOrDefault(g => g.Order == targetIndex);
                if (otherGroup != null)
                {
                    otherGroup.Order = currentIndex;
                }
                SelectedPlaylistGroup.Order = targetIndex;

                // Re-sort the collection
                var sorted = PlaylistGroups.OrderBy(g => g.Order).ToList();
                PlaylistGroups.Clear();
                foreach (var g in sorted)
                {
                    PlaylistGroups.Add(g);
                }

                // Update command states
                (MoveGroupUpCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (MoveGroupDownCommand as RelayCommand)?.RaiseCanExecuteChanged();

                HasUnsavedChanges = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExecuteMoveGroupDownCommand failed: {ex}");
            }
        }

        /// <summary>
        /// Populates PlaylistGroups from the specified list of group models.
        /// Used when loading a project.
        /// </summary>
        /// <param name="groups">The list of playlist group models to load.</param>
        public void LoadPlaylistGroups(System.Collections.Generic.List<PlaylistGroupModel> groups)
        {
            try
            {
                PlaylistGroups.Clear();
                _nextGroupNumber = 1;

                if (groups == null || groups.Count == 0) return;

                var sortedGroups = groups.OrderBy(g => g.Order).ToList();
                
                foreach (var model in sortedGroups)
                {
                    var vm = new PlaylistGroupViewModel(model);
                    PlaylistGroups.Add(vm);

                    // Update next group number
                    if (model.Name.StartsWith("Group ") && 
                        int.TryParse(model.Name.Substring(6).Trim(), out var num))
                    {
                        _nextGroupNumber = Math.Max(_nextGroupNumber, num + 1);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadPlaylistGroups failed: {ex}");
            }
        }

        /// <summary>
        /// Builds a list of PlaylistGroupModel objects from the current PlaylistGroups.
        /// Used when saving a project.
        /// </summary>
        /// <returns>A list of playlist group models.</returns>
        public System.Collections.Generic.List<PlaylistGroupModel> BuildPlaylistGroupModels()
        {
            try
            {
                return PlaylistGroups.Select(vm => vm.Model).ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"BuildPlaylistGroupModels failed: {ex}");
                return new System.Collections.Generic.List<PlaylistGroupModel>();
            }
        }

        /// <summary>
        /// Updates the Videos collection in each PlaylistGroupViewModel based on the ImportedVideos collection.
        /// Call this after loading a project to populate the Videos collections.
        /// </summary>
        public void UpdatePlaylistGroupVideos()
        {
            try
            {
                foreach (var group in PlaylistGroups)
                {
                    group.Videos.Clear();
                    
                    foreach (var sourceId in group.Model.SourceIds)
                    {
                        var video = ImportedVideos.FirstOrDefault(v => v.Id == sourceId);
                        if (video != null)
                        {
                            group.Videos.Add(video);
                        }
                    }
                    
                    group.RefreshVideoCount();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdatePlaylistGroupVideos failed: {ex}");
            }
        }

        #endregion
    }
}