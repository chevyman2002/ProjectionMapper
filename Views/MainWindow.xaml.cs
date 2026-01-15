using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProjectionMapper.Rendering;
using ProjectionMapper.Services;
using ProjectionMapper.Models;
using ProjectionMapper.ViewModels;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using ProjectionMapper.Views;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Numerics;

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
        private readonly ProjectService _projectService;
        private readonly PlaylistService _playlistService;

        // Monitor list for UI
        private readonly ObservableCollection<MonitorItem> _monitorItems = new();
        private List<MonitorInfo> _monitors = new();

        // Track current project file path
        private string? _currentProjectPath;

        // Track fullscreen windows for preview restore
        private readonly Dictionary<int, bool> _previewMonitorStates = new();
        private readonly Dictionary<int, FullScreenOutputWindow> _activeMonitorWindows = new();

        // P/Invoke for positioning windows
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        private record MonitorInfo(int Width, int Height, int Left, int Top);

        private class MonitorItem
        {
            public int Index { get; set; }
            public string Name { get; set; } = string.Empty;
            public override string ToString() => Name;
        }

        public MainWindow()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("MainWindow constructor starting...");
                
                InitializeComponent();

                _vm = new MainWindowViewModel();
                DataContext = _vm;
                _vm.PropertyChanged += OnViewModelPropertyChanged;

                // Track audio flag changes for imported videos to keep VideoService audio state in sync
                try
                {
                    _vm.ImportedVideos.CollectionChanged += OnImportedVideosCollectionChanged;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"MainWindow: Failed to subscribe to ImportedVideos changes: {ex}");
                }

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
                _projectService = new ProjectService();
                
                // Create PlaylistService for group-based playback
                _playlistService = new PlaylistService(_videoService);
                UpdateLoopingMode(_vm.IsPlaylistMode);
                
                // Expose VideoService so MeshEditorControl can subscribe for isolated previews
                this.Resources["VideoService"] = _videoService;

                // Wire event handlers to actual implementations
                _vm.ImportRequested += OnImportRequested;
                _vm.PreviewRequested += OnPreviewRequested;
                _vm.DeleteImportedRequested += OnDeleteImportedRequested;
                _vm.SaveProjectRequested += OnSaveProjectRequested;
                _vm.SaveAsProjectRequested += OnSaveAsProjectRequested;
                _vm.LoadProjectRequested += OnLoadProjectRequested;
                _vm.NewProjectRequested += OnNewProjectRequested;
                _vm.PlayPauseRequestedAsync += OnPlayPauseRequestedAsync;
                _vm.RestartRequestedAsync += OnRestartRequestedAsync;
                _vm.MeshLayerCreated += OnMeshLayerCreated;

                // Hook up playlist service events
                _playlistService.GroupChanged += (index) =>
                {
                    try
                    {
                        Debug.WriteLine($"MainWindow: Playlist group changed to index {index}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"MainWindow: Playlist group changed handler error: {ex}");
                    }
                };
                _playlistService.PlaylistCompleted += () =>
                {
                    try
                    {
                        Debug.WriteLine("MainWindow: Playlist completed");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"MainWindow: Playlist completed handler error: {ex}");
                    }
                };

                // Basic Loaded handler - initialize renderer before loading any project
                Loaded += async (_, __) =>
                {
                    try
                    {
                        // Initialize renderer with default HD size
                        var w = 1920;
                        var h = 1080;
                        await _rendererManager.StartAsync(w, h);
                        Debug.WriteLine($"MainWindow: Renderer initialized with size {w}x{h}");

                        // Enumerate monitors and populate the dropdown
                        EnumerateMonitors();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Renderer start failed: {ex}");
                    }
                };

                // Handle window closing with proper cleanup and shutdown
                Closing += async (sender, e) =>
                {
                    try
                    {
                        Debug.WriteLine("MainWindow: Window closing, checking for unsaved changes");
                        
                        // Check for unsaved changes
                        if (_vm.HasUnsavedChanges)
                        {
                            var result = MessageBox.Show(
                                "You have unsaved changes. Do you want to save before closing?",
                                "Unsaved Changes",
                                MessageBoxButton.YesNoCancel,
                                MessageBoxImage.Question);

                            if (result == MessageBoxResult.Cancel)
                            {
                                e.Cancel = true;
                                return;
                            }

                            if (result == MessageBoxResult.Yes)
                            {
                                await OnSaveProjectRequested();
                                // If save failed or was cancelled, don't close
                                if (_vm.HasUnsavedChanges)
                                {
                                    e.Cancel = true;
                                    return;
                                }
                            }
                        }

                        Debug.WriteLine("MainWindow: Disposing services");
                        
                        // Stop all video playback first - use Task.Run to avoid deadlock on UI thread
                        try 
                        { 
                            Task.Run(async () => await _videoService.StopAllAsync()).Wait(2000); 
                        } 
                        catch (Exception ex) 
                        { 
                            Debug.WriteLine($"MainWindow: StopAllAsync failed: {ex}"); 
                        }
                        
                        // Close all fullscreen windows first
                        foreach (var kv in _activeMonitorWindows.ToList())
                        {
                            try { kv.Value.Close(); } catch (Exception ex) { Debug.WriteLine($"MainWindow: Closing fullscreen window failed: {ex}"); }
                        }
                        _activeMonitorWindows.Clear();
                        
                        try { _rendererManager?.Dispose(); } catch (Exception ex) { Debug.WriteLine($"MainWindow: RendererManager dispose failed: {ex}"); }
                        try { _videoService?.Dispose(); } catch (Exception ex) { Debug.WriteLine($"MainWindow: VideoService dispose failed: {ex}"); }
                        try { _playlistService?.Dispose(); } catch (Exception ex) { Debug.WriteLine($"MainWindow: PlaylistService dispose failed: {ex}"); }

                        Debug.WriteLine("MainWindow: Cleanup completed, shutting down application");
                        
                        // Force application shutdown
                        Application.Current.Shutdown();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"MainWindow: Closing handler failed: {ex}");
                        // Force shutdown even if cleanup fails
                        Application.Current.Shutdown();
                    }
                };

                System.Diagnostics.Debug.WriteLine("MainWindow constructor completed successfully");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MainWindow constructor failed: {ex}");
                
                // Show error dialog
                try
                {
                    MessageBox.Show($"Failed to initialize application: {ex.Message}\n\nCheck debug output for details.", 
                        "Initialization Error", 
                        MessageBoxButton.OK, 
                        MessageBoxImage.Error);
                }
                catch { }
                
                throw; // Re-throw to prevent partially initialized window
            }
        }

        #region Global Mesh Overlay Toggle

        /// <summary>
        /// Handles the global mesh overlay visibility toggle.
        /// When unchecked, hides all mesh overlays on all outputs.
        /// </summary>
        private void OnGlobalShowMeshOverlayChanged(object sender, RoutedEventArgs e)
        {
            try
            {
                // Guard against null _rendererManager during initialization
                // (checkbox events can fire during InitializeComponent before services are created)
                if (_rendererManager == null)
                {
                    Debug.WriteLine("**_rendererManager** was null.");
                    return;
                }

                bool show = PART_GlobalShowMeshOverlayCheckbox.IsChecked == true;
                _rendererManager.ShowMeshOverlay = show;
                Debug.WriteLine($"MainWindow: Global mesh overlay visibility set to {show}");

                if (show)
                {
                    // Refresh all mesh overlays to make them visible again
                    RefreshAllMeshOverlays();
                }
                else
                {
                    // Clear all overlays from all hosts
                    _rendererManager.ClearAllOverlays();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: OnGlobalShowMeshOverlayChanged failed: {ex}");
            }
        }

        /// <summary>
        /// Refreshes mesh overlays for all mesh layers to reflect current visibility state.
        /// </summary>
        private void RefreshAllMeshOverlays()
        {
            try
            {
                foreach (var video in _vm.ImportedVideos)
                {
                    foreach (var mesh in video.MeshLayers)
                    {
                        if (mesh?.Model == null) continue;
                        if (!mesh.ShowOverlay) continue;

                        var layerId = mesh.Model.Id;
                        if (string.IsNullOrEmpty(layerId)) continue;

                        var targetMonitor = mesh.TargetMonitorIndex;
                        var quadPoints = _rendererManager.MapNormalizedToRendererPoints(mesh.OutputMeshPoints, targetMonitor >= 0 ? targetMonitor : null);
                        
                        if (quadPoints != null && quadPoints.Length >= 4)
                        {
                            _rendererManager.AddMeshOverlayForMonitor(targetMonitor >= 0 ? targetMonitor : null, quadPoints, true, layerId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: RefreshAllMeshOverlays failed: {ex}");
            }
        }

        #endregion

        #region Event Handler Implementations

        private async void OnImportRequested()
        {
            try
            {
                Debug.WriteLine("MainWindow: Import requested");
                
                var path = await _fileDialog.ShowOpenFileDialogAsync(
                    "Import Video",
                    "Video Files (*.mp4;*.avi;*.mov;*.mkv;*.wmv)|*.mp4;*.avi;*.mov;*.mkv;*.wmv|All Files (*.*)|*.*");

                if (string.IsNullOrEmpty(path))
                {
                    Debug.WriteLine("MainWindow: Import cancelled by user");
                    return;
                }

                Debug.WriteLine($"MainWindow: Importing video from {path}");

                // Create imported video view model
                var id = Guid.NewGuid().ToString("N");
                var name = Path.GetFileNameWithoutExtension(path);
                var imported = new ImportedVideoViewModel(id, name, path);

                // Create host layer for this video
                var hostLayer = new LayerModel
                {
                    Id = id,
                    Name = $"{name} (Host)",
                    SourcePath = path,
                    Width = _rendererManager.OutputWidth,
                    Height = _rendererManager.OutputHeight,
                    Visible = true,
                    PreviewOnly = false
                };

                imported.HostLayer = hostLayer;
                imported.NotifyHostLayerChanged();

                // Add to view model
                _vm.ImportedVideos.Add(imported);
                _vm.SelectedImportedVideo = imported;

                // Register with video service
                await _videoService.RegisterLayerAsync(hostLayer, playAudio: false);

                Debug.WriteLine($"MainWindow: Successfully imported video '{name}'");
                _vm.StatusText = $"Imported: {name}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: Import failed: {ex}");
                MessageBox.Show($"Failed to import video: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnPreviewRequested()
        {
            try
            {
                Debug.WriteLine("MainWindow: Preview requested - feature not yet implemented");
                MessageBox.Show("Full-screen preview feature coming soon!", "Preview", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: Preview failed: {ex}");
            }
        }

        private async void OnDeleteImportedRequested(ImportedVideoViewModel? imported)
        {
            try
            {
                if (imported == null)
                {
                    Debug.WriteLine("MainWindow: Delete imported requested with null video");
                    return;
                }

                Debug.WriteLine($"MainWindow: Delete imported requested for '{imported.Name}'");

                var result = MessageBox.Show(
                    $"Are you sure you want to delete '{imported.Name}' and all its mesh layers?",
                    "Confirm Delete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    Debug.WriteLine("MainWindow: Delete cancelled by user");
                    return;
                }

                // Unregister host layer
                if (imported.HostLayer != null)
                {
                    await _videoService.UnregisterLayerAsync(imported.HostLayer.Id ?? string.Empty);
                }

                // Unregister all mesh layers
                foreach (var mesh in imported.MeshLayers.ToArray())
                {
                    if (mesh.Model?.Id != null)
                    {
                        await _videoService.UnregisterMeshLayerAsync(mesh.Model.Id);
                    }
                }

                // Remove from view model
                _vm.ImportedVideos.Remove(imported);

                Debug.WriteLine($"MainWindow: Successfully deleted '{imported.Name}'");
                _vm.StatusText = $"Deleted: {imported.Name}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: Delete imported failed: {ex}");
                MessageBox.Show($"Failed to delete video: {ex.Message}", "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task OnSaveProjectRequested()
        {
            try
            {
                Debug.WriteLine("MainWindow: Save project requested");

                // If no current path, do Save As
                if (string.IsNullOrEmpty(_currentProjectPath))
                {
                    await OnSaveAsProjectRequested();
                    return;
                }

                Debug.WriteLine($"MainWindow: Saving project to {_currentProjectPath}");

                // Build project model from current state
                var project = BuildProjectModel();

                // Save project
                var success = await _projectService.SaveAsync(project, _currentProjectPath);

                if (success)
                {
                    _vm.MarkProjectClean();
                    Debug.WriteLine("MainWindow: Project saved successfully");
                    _vm.StatusText = $"Saved: {Path.GetFileName(_currentProjectPath)}";
                }
                else
                {
                    Debug.WriteLine("MainWindow: Project save failed");
                    MessageBox.Show("Failed to save project. Check debug output for details.", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: Save project failed: {ex}");
                MessageBox.Show($"Failed to save project: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task OnSaveAsProjectRequested()
        {
            try
            {
                Debug.WriteLine("MainWindow: Save As project requested");

                var path = await _fileDialog.ShowSaveFileDialogAsync(
                    "Save Project As",
                    "Untitled.pmproj",
                    "ProjectionMapper Project (*.pmproj)|*.pmproj|All Files (*.*)|*.*");

                if (string.IsNullOrEmpty(path))
                {
                    Debug.WriteLine("MainWindow: Save As cancelled by user");
                    return;
                }

                Debug.WriteLine($"MainWindow: Saving project to {path}");

                // Build project model from current state
                var project = BuildProjectModel();

                // Save project
                var success = await _projectService.SaveAsync(project, path);

                if (success)
                {
                    _currentProjectPath = path;
                    _vm.MarkProjectClean();
                    Debug.WriteLine("MainWindow: Project saved successfully");
                    _vm.StatusText = $"Saved: {Path.GetFileName(path)}";
                    Title = $"ProjectionMapper - {Path.GetFileName(path)}";
                }
                else
                {
                    Debug.WriteLine("MainWindow: Project save failed");
                    MessageBox.Show("Failed to save project. Check debug output for details.", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: Save As project failed: {ex}");
                MessageBox.Show($"Failed to save project: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task OnLoadProjectRequested()
        {
            try
            {
                Debug.WriteLine("MainWindow: Load project requested");

                // Check for unsaved changes
                if (_vm.HasUnsavedChanges)
                {
                    var result = MessageBox.Show(
                        "You have unsaved changes. Do you want to save before loading a new project?",
                        "Unsaved Changes",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Cancel)
                    {
                        Debug.WriteLine("MainWindow: Load cancelled by user");
                        return;
                    }

                    if (result == MessageBoxResult.Yes)
                    {
                        await OnSaveProjectRequested();
                        // If save failed, don't load
                        if (_vm.HasUnsavedChanges)
                        {
                            return;
                        }
                    }
                }

                var path = await _fileDialog.ShowOpenFileDialogAsync(
                    "Load Project",
                    "ProjectionMapper Project (*.pmproj)|*.pmproj|All Files (*.*)|*.*");

                if (string.IsNullOrEmpty(path))
                {
                    Debug.WriteLine("MainWindow: Load cancelled by user");
                    return;
                }

                Debug.WriteLine($"MainWindow: Loading project from {path}");

                // Load project
                var project = await _projectService.LoadAsync(path);

                if (project == null)
                {
                    Debug.WriteLine("MainWindow: Project load failed");
                    MessageBox.Show("Failed to load project. The file may be corrupted or incompatible.", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Clear current project state
                await ClearProjectState();

                // Load project state into UI
                await LoadProjectState(project);

                _currentProjectPath = path;
                _vm.MarkProjectClean();
                Debug.WriteLine("MainWindow: Project loaded successfully");
                _vm.StatusText = $"Loaded: {Path.GetFileName(path)}";
                Title = $"ProjectionMapper - {Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: Load project failed: {ex}");
                MessageBox.Show($"Failed to load project: {ex.Message}", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task OnNewProjectRequested()
        {
            try
            {
                Debug.WriteLine("MainWindow: New project requested");

                // Check for unsaved changes
                if (_vm.HasUnsavedChanges)
                {
                    var result = MessageBox.Show(
                        "You have unsaved changes. Do you want to save before creating a new project?",
                        "Unsaved Changes",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Cancel)
                    {
                        Debug.WriteLine("MainWindow: New project cancelled by user");
                        return;
                    }

                    if (result == MessageBoxResult.Yes)
                    {
                        await OnSaveProjectRequested();
                        // If save failed, don't create new project
                        if (_vm.HasUnsavedChanges)
                        {
                            return;
                        }
                    }
                }

                // Clear current project state
                await ClearProjectState();

                _currentProjectPath = null;
                _vm.MarkProjectClean();
                Debug.WriteLine("MainWindow: New project created");
                _vm.StatusText = "New project created";
                Title = "ProjectionMapper - Untitled";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: New project failed: {ex}");
                MessageBox.Show($"Failed to create new project: {ex.Message}", "New Project Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task OnPlayPauseRequestedAsync()
        {
            try
            {
                Debug.WriteLine($"MainWindow: Play/Pause requested, IsPlaying={_vm.IsPlaying}");

                if (_vm.IsPlaying)
                {
                    if (!_vm.IsPlaylistMode && _vm.PlaylistGroups.Count > 0)
                    {
                        Debug.WriteLine("MainWindow: Playlist groups detected - enabling playlist mode automatically");
                        _vm.IsPlaylistMode = true;
                    }

                    // Start or resume playback
                    if (_vm.IsPlaylistMode)
                    {
                        // Check if playlist is paused - resume instead of starting fresh
                        if (_playlistService.IsPaused)
                        {
                            Debug.WriteLine("MainWindow: Resuming playlist playback from pause");
                            await _playlistService.ResumeCurrentGroupAsync();
                        }
                        else
                        {
                            Debug.WriteLine("MainWindow: Starting playlist playback");
                            // Build list of playlist groups from the view model
                            var groups = _vm.BuildPlaylistGroupModels();
                            await _playlistService.StartPlaylistAsync(groups);
                        }
                    }
                    else
                    {
                        Debug.WriteLine("MainWindow: Starting legacy playback");
                        await _videoService.ResumeAllAsync();
                    }
                    _vm.StatusText = "Playing";
                }
                else
                {
                    // Pause playback
                    if (_vm.IsPlaylistMode)
                    {
                        Debug.WriteLine("MainWindow: Pausing playlist playback");
                        await _playlistService.PauseCurrentGroupAsync();
                    }
                    else
                    {
                        Debug.WriteLine("MainWindow: Pausing legacy playback");
                        await _videoService.PauseAllAsync();
                    }
                    _vm.StatusText = "Paused";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: Play/Pause failed: {ex}");
                MessageBox.Show($"Playback error: {ex.Message}", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task OnRestartRequestedAsync()
        {
            try
            {
                Debug.WriteLine("MainWindow: Restart requested");

                if (_vm.IsPlaylistMode)
                {
                    Debug.WriteLine("MainWindow: Restarting playlist playback");
                    await _playlistService.RestartPlaylistAsync();
                }
                else
                {
                    Debug.WriteLine("MainWindow: Restarting legacy playback");
                    await _videoService.RestartAllAsync();
                }

                _vm.StatusText = "Restarted";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: Restart failed: {ex}");
                MessageBox.Show($"Restart error: {ex.Message}", "Restart Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void OnMeshLayerCreated(LayerModel? layer)
        {
            try
            {
                if (layer == null)
                {
                    Debug.WriteLine("MainWindow: Mesh layer created with null model");
                    return;
                }

                Debug.WriteLine($"MainWindow: Mesh layer created: {layer.Name}");
                await _videoService.RegisterMeshLayerAsync(layer);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: Mesh layer registration failed: {ex}");
            }
        }

        /// <summary>
        /// Reacts to view-model property changes so service behavior (like looping) stays in sync with UI state.
        /// </summary>
        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            try
            {
                if (e.PropertyName == nameof(MainWindowViewModel.IsPlaylistMode))
                {
                    UpdateLoopingMode(_vm.IsPlaylistMode);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: OnViewModelPropertyChanged failed: {ex}");
            }
        }

        /// <summary>
        /// Ensures decoder looping behavior matches the active playback mode. Playlist mode disables looping so clips stop at EOF.
        /// </summary>
        private void UpdateLoopingMode(bool playlistModeActive)
        {
            try
            {
                if (playlistModeActive)
                {
                    _videoService.DisableLoopingForAll();
                    Debug.WriteLine("MainWindow: Looping disabled (playlist mode)");
                }
                else
                {
                    _videoService.EnableLoopingForAll();
                    Debug.WriteLine("MainWindow: Looping enabled (legacy mode)");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: UpdateLoopingMode failed: {ex}");
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Subscribes to property changes on imported videos so we can react to PlayAudio toggles.
        /// </summary>
        private void AttachImportedVideoHandlers(ImportedVideoViewModel? video)
        {
            if (video == null) return;

            try
            {
                video.PropertyChanged -= ImportedVideo_PropertyChanged;
                video.PropertyChanged += ImportedVideo_PropertyChanged;

                video.MeshLayers.CollectionChanged -= MeshLayers_CollectionChanged;
                video.MeshLayers.CollectionChanged += MeshLayers_CollectionChanged;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: AttachImportedVideoHandlers failed: {ex}");
            }
        }

        // Track when last EnsureMonitorWindows was called to prevent rapid calls
        private DateTime _lastEnsureMonitorWindowsCall = DateTime.MinValue;
        private readonly object _ensureMonitorWindowsLock = new();

        /// <summary>
        /// Ensures fullscreen output windows exist for all monitors referenced by any mesh layer.
        /// Also removes windows that are no longer needed.
        /// </summary>
        private void EnsureMonitorWindows()
        {
            try
            {
                // Throttle calls to prevent rapid window creation/destruction
                lock (_ensureMonitorWindowsLock)
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastEnsureMonitorWindowsCall).TotalMilliseconds < 100)
                    {
                        return;
                    }
                    _lastEnsureMonitorWindowsCall = now;
                }

                if (_monitors == null || _monitors.Count == 0)
                {
                    // Nothing to reconcile without monitor metadata
                    foreach (var idx in _activeMonitorWindows.Keys.ToList())
                    {
                        HideMonitorWindow(idx);
                    }
                    return;
                }

                var required = new HashSet<int>();

                // Only mesh layers target monitors - collect all target monitor indices
                foreach (var video in _vm.ImportedVideos)
                {
                    foreach (var mesh in video.MeshLayers)
                    {
                        var meshMonitor = mesh.Model?.TargetMonitorIndex ?? -1;
                        if (meshMonitor >= 0)
                        {
                            required.Add(meshMonitor);
                        }
                    }
                }

                // Hide windows no longer referenced by any layer
                foreach (var active in _activeMonitorWindows.Keys.ToList())
                {
                    if (!required.Contains(active))
                    {
                        HideMonitorWindow(active);
                    }
                }

                // Spin up windows for newly required monitors
                foreach (var monitorIndex in required)
                {
                    ShowMonitorWindow(monitorIndex);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: EnsureMonitorWindows failed: {ex}");
            }
        }

        // P/Invoke to place window at exact monitor pixel bounds
        private const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        /// <summary>
        /// Creates (or reuses) a fullscreen output window for the requested monitor.
        /// Uses the same approach as the original working implementation.
        /// </summary>
        private void ShowMonitorWindow(int monitorIndex)
        {
            if (monitorIndex < 0)
            {
                return;
            }

            // Check if monitors list is valid and has this index
            if (_monitors == null || monitorIndex >= _monitors.Count)
            {
                Debug.WriteLine($"MainWindow: ShowMonitorWindow - monitor index {monitorIndex} out of range (count: {_monitors?.Count ?? 0})");
                return;
            }

            if (_activeMonitorWindows.ContainsKey(monitorIndex))
            {
                return;
            }

            try
            {
                var mon = _monitors[monitorIndex];
                if (mon == null)
                {
                    Debug.WriteLine($"MainWindow: ShowMonitorWindow - monitor {monitorIndex} is null");
                    return;
                }

                // Create fullscreen window and position it using monitor bounds converted to DIPs
                var win = new FullScreenOutputWindow
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Topmost = true,
                    WindowState = WindowState.Normal
                };

                // Convert monitor pixel boundaries to WPF DIPs
                var source = PresentationSource.FromVisual(this);
                double dpiX = 1.0, dpiY = 1.0;
                if (source != null && source.CompositionTarget != null)
                {
                    dpiX = source.CompositionTarget.TransformFromDevice.M11;
                    dpiY = source.CompositionTarget.TransformFromDevice.M22;
                }

                win.Left = mon.Left / dpiX;
                win.Top = mon.Top / dpiY;
                win.Width = mon.Width / dpiX;
                win.Height = mon.Height / dpiX;

                win.WindowStartupLocation = WindowStartupLocation.Manual;

                win.Closed += FullScreenWindow_Closed;
                win.Tag = monitorIndex;

                // Ensure native handle exists so we can position the window before showing it
                try
                {
                    var helper = new WindowInteropHelper(win);
                    var hwnd = helper.EnsureHandle();

                    const int GWL_STYLE = -16;
                    const int WS_POPUP = unchecked((int)0x80000000);

                    Debug.WriteLine($"MainWindow: Calling SetWindowPos for monitor {monitorIndex} -> rect {mon.Left},{mon.Top} {mon.Width}x{mon.Height}");
                    bool ok = SetWindowPos(hwnd, HWND_TOPMOST, mon.Left, mon.Top, mon.Width, mon.Height, SWP_SHOWWINDOW);
                    var err = Marshal.GetLastWin32Error();
                    Debug.WriteLine($"MainWindow: SetWindowPos returned {ok}, GetLastError={err}");

                    if (!ok)
                    {
                        try
                        {
                            var prev = GetWindowLong(hwnd, GWL_STYLE);
                            SetWindowLong(hwnd, GWL_STYLE, prev | WS_POPUP);
                            ok = SetWindowPos(hwnd, HWND_TOPMOST, mon.Left, mon.Top, mon.Width, mon.Height, SWP_SHOWWINDOW);
                            err = Marshal.GetLastWin32Error();
                            Debug.WriteLine($"MainWindow: Retry SetWindowPos returned {ok}, GetLastError={err}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"MainWindow: Retry SetWindowPos exception: {ex}");
                        }
                    }

                    // Show the window AFTER native positioning
                    win.Show();

                    _activeMonitorWindows[monitorIndex] = win;

                    // Initialize the fullscreen renderer for this monitor
                    _rendererManager.ShowFullScreenWindow(monitorIndex, win, mon.Width, mon.Height);
                    _rendererManager.AttachHost(monitorIndex, win);

                    // Set to maximized to ensure true fullscreen
                    win.WindowState = WindowState.Maximized;

                    Debug.WriteLine($"MainWindow: ShowMonitorWindow success for monitor {monitorIndex}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"MainWindow: ShowMonitorWindow placement/show failed: {ex}");
                    try
                    {
                        win.WindowState = WindowState.Maximized;
                        win.Show();
                        _activeMonitorWindows[monitorIndex] = win;
                        _rendererManager.ShowFullScreenWindow(monitorIndex, win, mon.Width, mon.Height);
                        _rendererManager.AttachHost(monitorIndex, win);
                    }
                    catch (Exception ex2)
                    {
                        Debug.WriteLine($"MainWindow: ShowMonitorWindow fallback show failed: {ex2}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: ShowMonitorWindow failed for monitor {monitorIndex}: {ex}");
            }
        }

        /// <summary>
        /// Hides and disposes the fullscreen output window assigned to the specified monitor.
        /// </summary>
        private void HideMonitorWindow(int monitorIndex)
        {
            if (!_activeMonitorWindows.TryGetValue(monitorIndex, out var window))
            {
                return;
            }

            try
            {
                window.Closed -= FullScreenWindow_Closed;
                _activeMonitorWindows.Remove(monitorIndex);
                _rendererManager.HideFullScreenWindow(monitorIndex);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: HideMonitorWindow failed for monitor {monitorIndex}: {ex}");
            }
        }

        /// <summary>
        /// Cleans up renderer bookkeeping if a fullscreen window is closed directly by the user (e.g., via ESC).
        /// </summary>
        private void FullScreenWindow_Closed(object? sender, EventArgs e)
        {
            if (sender is not FullScreenOutputWindow window)
            {
                return;
            }

            if (window.Tag is int monitorIndex)
            {
                window.Closed -= FullScreenWindow_Closed;
                _activeMonitorWindows.Remove(monitorIndex);

                try
                {
                    _rendererManager.HideFullScreenWindow(monitorIndex);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"MainWindow: FullScreenWindow_Closed cleanup failed for monitor {monitorIndex}: {ex}");
                }
            }
        }

        /// <summary>
        /// Removes property change handlers from imported videos to prevent memory leaks.
        /// </summary>
        private void DetachImportedVideoHandlers(ImportedVideoViewModel? video)
        {
            if (video == null) return;

            try
            {
                video.PropertyChanged -= ImportedVideo_PropertyChanged;
                video.MeshLayers.CollectionChanged -= MeshLayers_CollectionChanged;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: DetachImportedVideoHandlers failed: {ex}");
            }
        }

        private void MeshLayers_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            try
            {
                EnsureMonitorWindows();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: MeshLayers_CollectionChanged failed: {ex}");
            }
        }

        /// <summary>
        /// Handles changes to imported video properties so audio state stays consistent with the UI.
        /// </summary>
        private void ImportedVideo_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not ImportedVideoViewModel video || e.PropertyName != nameof(ImportedVideoViewModel.PlayAudio))
            {
                return;
            }

            try
            {
                var layerId = video.HostLayer?.Id;
                if (string.IsNullOrEmpty(layerId))
                {
                    return;
                }

                // CRITICAL FIX: In playlist mode, only allow audio changes for videos in the current group
                // Otherwise, toggling audio on one video would affect all videos
                if (_vm.IsPlaylistMode && _playlistService.IsPlaying)
                {
                    var currentGroup = _playlistService.CurrentGroup;
                    if (currentGroup == null || !currentGroup.SourceIds.Contains(layerId))
                    {
                        Debug.WriteLine($"MainWindow: Ignoring audio toggle for {layerId} - not in current playlist group");
                        return;
                    }
                }

                if (video.PlayAudio)
                {
                    _videoService.StartAudioForLayer(layerId);
                }
                else
                {
                    _videoService.StopAudioForLayer(layerId);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: ImportedVideo_PropertyChanged failed: {ex}");
            }
        }

        /// <summary>
        /// Keeps handler subscriptions aligned with the imported videos collection.
        /// </summary>
        private void OnImportedVideosCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            try
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    foreach (var video in _vm.ImportedVideos)
                    {
                        AttachImportedVideoHandlers(video);
                    }
                    EnsureMonitorWindows();
                    return;
                }

                if (e.NewItems != null)
                {
                    foreach (ImportedVideoViewModel video in e.NewItems)
                    {
                        AttachImportedVideoHandlers(video);
                    }
                }

                if (e.OldItems != null)
                {
                    foreach (ImportedVideoViewModel video in e.OldItems)
                    {
                        DetachImportedVideoHandlers(video);
                    }
                }

                EnsureMonitorWindows();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: OnImportedVideosCollectionChanged failed: {ex}");
            }
        }

        // P/Invoke helpers to query physical display mode (to avoid DPI scaling artifacts)
        private const int ENUM_CURRENT_SETTINGS = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;

            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;

            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            // remaining fields omitted
        }

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        /// <summary>
        /// Enumerates connected monitors and populates the monitor dropdown.
        /// Uses EnumDisplaySettings to obtain the physical pixel resolution to avoid DPI scaling artifacts.
        /// </summary>
        private void EnumerateMonitors()
        {
            try
            {
                Debug.WriteLine("MainWindow: Enumerating monitors...");
                _monitors.Clear();
                _monitorItems.Clear();

                // Use Windows Forms to enumerate display devices, but query EnumDisplaySettings
                System.Windows.Forms.Screen[]? screens = null;
                try
                {
                    screens = System.Windows.Forms.Screen.AllScreens;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"MainWindow: Screen.AllScreens failed (display may be disconnecting): {ex}");
                    return;
                }

                if (screens == null || screens.Length == 0)
                {
                    Debug.WriteLine("MainWindow: No screens found");
                    return;
                }

                for (int i = 0; i < screens.Length; i++)
                {
                    try
                    {
                        var screen = screens[i];
                        if (screen == null) continue;

                        int physW = screen.Bounds.Width;
                        int physH = screen.Bounds.Height;

                        try
                        {
                            // Query current display mode for the device name to get true physical pixels
                            var dm = new DEVMODE();
                            dm.dmDeviceName = new string('\0', 32);
                            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));

                            if (EnumDisplaySettings(screen.DeviceName, ENUM_CURRENT_SETTINGS, ref dm))
                            {
                                // Use dmPelsWidth/dmPelsHeight which reflect the actual mode in pixels
                                if (dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
                                {
                                    physW = dm.dmPelsWidth;
                                    physH = dm.dmPelsHeight;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"MainWindow: EnumDisplaySettings failed for {screen.DeviceName}: {ex}");
                        }

                        var bounds = screen.Bounds;

                        _monitors.Add(new MonitorInfo(physW, physH, bounds.Left, bounds.Top));

                        var isPrimary = screen.Primary ? " (Primary)" : "";
                        var monitorItem = new MonitorItem
                        {
                            Index = i,
                            Name = $"Display {i + 1}: {physW}x{physH}{isPrimary}"
                        };
                        _monitorItems.Add(monitorItem);

                        Debug.WriteLine($"MainWindow: Found monitor {i}: {monitorItem.Name} at ({bounds.Left}, {bounds.Top})");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"MainWindow: Failed to enumerate monitor {i}: {ex}");
                    }
                }

                // Populate PART_MeshMonitorCombo (for mesh layers)
                if (PART_MeshMonitorCombo != null)
                {
                    PART_MeshMonitorCombo.Items.Clear();
                    PART_MeshMonitorCombo.Items.Add("(None)");
                    foreach (var item in _monitorItems)
                    {
                        PART_MeshMonitorCombo.Items.Add(item.Name);
                    }
                    PART_MeshMonitorCombo.SelectedIndex = 0;

                    // Wire up selection changed event for PART_MeshMonitorCombo
                    PART_MeshMonitorCombo.SelectionChanged -= OnMeshMonitorComboSelectionChanged;
                    PART_MeshMonitorCombo.SelectionChanged += OnMeshMonitorComboSelectionChanged;
                }

                Debug.WriteLine($"MainWindow: Found {_monitorItems.Count} monitors");

                // Reconcile fullscreen output windows with the latest monitor inventory
                EnsureMonitorWindows();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: EnumerateMonitors failed: {ex}");
            }
        }

        private void OnMeshMonitorComboSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (_vm.SelectedMeshLayer == null) return;
                
                var selectedIndex = PART_MeshMonitorCombo.SelectedIndex - 1; // -1 because "None" is at index 0
                
                // Use the ViewModel property to trigger PropertyChanged notification
                // This allows the overlay system to detect the change and update overlays accordingly
                _vm.SelectedMeshLayer.TargetMonitorIndex = selectedIndex;
                Debug.WriteLine($"MainWindow: Set target monitor to {selectedIndex} for mesh layer");

                EnsureMonitorWindows();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: OnMeshMonitorComboSelectionChanged failed: {ex}");
            }
        }

        private ProjectModel BuildProjectModel()
        {
            try
            {
                var project = new ProjectModel
                {
                    Name = _vm.ActiveProject?.Name ?? "Untitled Project",
                    ProjectVersion = 2,
                    PlaylistMode = _vm.IsPlaylistMode,
                    ShowMeshOverlay = true,
                    ShowCoordinateGrid = false,
                    InputZoom = _vm.InputZoom,
                    OutputZoom = _vm.OutputZoom
                };

                // Add imported videos
                foreach (var imported in _vm.ImportedVideos)
                {
                    var videoData = new ImportedVideoData
                    {
                        Id = imported.Id,
                        Name = imported.Name,
                        SourcePath = imported.SourcePath,
                        PlayAudio = imported.PlayAudio,
                        Visible = true
                    };

                    // Add mesh layers
                    foreach (var mesh in imported.MeshLayers)
                    {
                        var meshData = new MeshLayerData
                        {
                            Id = mesh.Model.Id ?? Guid.NewGuid().ToString("N"),
                            Name = mesh.Name ?? "Mesh",
                            SourceId = mesh.Model.SourceId ?? string.Empty,
                            X = mesh.X,
                            Y = mesh.Y,
                            Width = mesh.Width,
                            Height = mesh.Height,
                            Opacity = mesh.Opacity,
                            Visible = mesh.Visible,
                            RotationDegrees = mesh.RotationDegrees,
                            // Access TargetMonitorIndex through the Model property
                            TargetMonitorIndex = mesh.Model.TargetMonitorIndex,
                            ShowOverlay = mesh.ShowOverlay
                        };

                        // Copy mesh points
                        var srcPoints = mesh.Model.MeshPoints;
                        var srcOutputPoints = mesh.Model.OutputMeshPoints;
                        meshData.MeshPoints = new float[8];
                        meshData.OutputMeshPoints = new float[8];

                        for (int i = 0; i < 4 && i < srcPoints.Length; i++)
                        {
                            meshData.MeshPoints[i * 2] = srcPoints[i].X;
                            meshData.MeshPoints[i * 2 + 1] = srcPoints[i].Y;
                        }

                        for (int i = 0; i < 4 && i < srcOutputPoints.Length; i++)
                        {
                            meshData.OutputMeshPoints[i * 2] = srcOutputPoints[i].X;
                            meshData.OutputMeshPoints[i * 2 + 1] = srcOutputPoints[i].Y;
                        }

                        videoData.MeshLayers.Add(meshData);
                    }

                    project.ImportedVideos.Add(videoData);
                }

                // Add playlist groups
                project.PlaylistGroups = _vm.BuildPlaylistGroupModels();

                return project;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: BuildProjectModel failed: {ex}");
                throw;
            }
        }

        private async Task ClearProjectState()
        {
            try
            {
                Debug.WriteLine("MainWindow: Clearing project state");

                // Stop playback - pass re-enable looping flag to restore legacy mode behavior
                await _playlistService.StopPlaylistAsync(reEnableLooping: true);
                await _videoService.StopAllAsync();

                // Clear imported videos (detach handlers first to avoid lingering subscriptions)
                foreach (var video in _vm.ImportedVideos.ToList())
                {
                    DetachImportedVideoHandlers(video);
                }
                _vm.ImportedVideos.Clear();

                // Clear playlist groups
                _vm.PlaylistGroups.Clear();

                // Reset playlist mode
                _vm.IsPlaylistMode = false;

                Debug.WriteLine("MainWindow: Project state cleared");

                // Ensure fullscreen outputs are closed when nothing targets them
                EnsureMonitorWindows();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: ClearProjectState failed: {ex}");
                throw;
            }
        }

        private async Task LoadProjectState(ProjectModel project)
        {
            try
            {
                Debug.WriteLine($"MainWindow: Loading project state for '{project.Name}'");

                // Set project properties
                _vm.IsPlaylistMode = project.PlaylistMode;
                _vm.InputZoom = project.InputZoom;
                _vm.OutputZoom = project.OutputZoom;

                // Load imported videos
                foreach (var videoData in project.ImportedVideos)
                {
                    var imported = new ImportedVideoViewModel(videoData.Id, videoData.Name, videoData.SourcePath);

                    // Create host layer
                    var hostLayer = new LayerModel
                    {
                        Id = videoData.Id,
                        Name = $"{videoData.Name} (Host)",
                        SourcePath = videoData.SourcePath,
                        Width = _rendererManager.OutputWidth,
                        Height = _rendererManager.OutputHeight,
                        Visible = videoData.Visible,
                        PlayAudio = videoData.PlayAudio,
                        PreviewOnly = videoData.MeshLayers.Count > 0
                    };

                    imported.HostLayer = hostLayer;
                    imported.NotifyHostLayerChanged();

                    // Load mesh layers
                    foreach (var meshData in videoData.MeshLayers)
                    {
                        var meshModel = new LayerModel
                        {
                            Id = meshData.Id,
                            Name = meshData.Name,
                            SourceId = meshData.SourceId,
                            X = meshData.X,
                            Y = meshData.Y,
                            Width = meshData.Width,
                            Height = meshData.Height,
                            Opacity = meshData.Opacity,
                            Visible = meshData.Visible,
                            RotationDegrees = meshData.RotationDegrees,
                            TargetMonitorIndex = meshData.TargetMonitorIndex,
                            ShowOverlay = meshData.ShowOverlay
                        };

                        // Copy mesh points
                        for (int i = 0; i < 4 && (i * 2 + 1) < meshData.MeshPoints.Length; i++)
                        {
                            meshModel.MeshPoints[i] = new Vector2(
                                meshData.MeshPoints[i * 2],
                                meshData.MeshPoints[i * 2 + 1]);
                        }

                        for (int i = 0; i < 4 && (i * 2 + 1) < meshData.OutputMeshPoints.Length; i++)
                        {
                            meshModel.OutputMeshPoints[i] = new Vector2(
                                meshData.OutputMeshPoints[i * 2],
                                meshData.OutputMeshPoints[i * 2 + 1]);
                        }

                        var meshVm = new LayerViewModel(meshModel);
                        imported.MeshLayers.Add(meshVm);

                        await _videoService.RegisterMeshLayerAsync(meshModel);
                    }

                    _vm.ImportedVideos.Add(imported);
                    await _videoService.RegisterLayerAsync(hostLayer, playAudio: videoData.PlayAudio);
                }

                // Load playlist groups
                _vm.LoadPlaylistGroups(project.PlaylistGroups);
                _vm.UpdatePlaylistGroupVideos();

                // In playlist mode, looping is already disabled (set earlier in LoadProjectState)
                // Videos will play normally and stop at EOF, waiting for the playlist to advance
                // We don't need to pause them here - let them play so the user can see the preview
               

                Debug.WriteLine("MainWindow: Project state loaded successfully");

                 // Update fullscreen window assignments to reflect the freshly loaded project
                 EnsureMonitorWindows();
 
                // Automatically start playlist playback so only one group runs at a time
                await EnsurePlaylistAutostartAsync().ConfigureAwait(false);
             }
             catch (Exception ex)
             {
                 Debug.WriteLine($"MainWindow: LoadProjectState failed: {ex}");
                 throw;
             }
        }

        /// <summary>
        /// Ensures playlist playback starts automatically when playlist mode is active after loading a project.
        /// </summary>
        private async Task EnsurePlaylistAutostartAsync()
        {
            try
            {
                if (!_vm.IsPlaylistMode)
                {
                    return;
                }

                if (_vm.PlaylistGroups == null || _vm.PlaylistGroups.Count == 0)
                {
                    return;
                }

                var groups = _vm.BuildPlaylistGroupModels();
                if (groups == null || groups.Count == 0)
                {
                    return;
                }

                Debug.WriteLine("MainWindow: Auto-starting playlist playback after project load");
                await _playlistService.StartPlaylistAsync(groups).ConfigureAwait(false);
                _vm.SetPlaybackState(true);
                _vm.StatusText = "Playing (Playlist Mode)";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: EnsurePlaylistAutostartAsync failed: {ex}");
            }
        }

        #endregion

        // Event handlers required by XAML
        private void Window_Loaded(object sender, RoutedEventArgs e) { }
        
        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("MainWindow: Exit menu clicked");
                Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: ExitMenuItem_Click failed: {ex}");
                Application.Current.Shutdown();
            }
        }
        
        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("MainWindow: About menu clicked");
                var aboutWindow = new AboutWindow
                {
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                aboutWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: AboutMenuItem_Click failed: {ex}");
                MessageBox.Show($"Failed to open About dialog: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            try
            {
                var selectedItem = e.NewValue;
                Debug.WriteLine($"MainWindow: TreeView selection changed to: {selectedItem?.GetType().Name}");

                // Clear current selections first
                _vm.SelectedMeshLayer = null;
                _vm.SelectedImportedVideo = null;
                _vm.SelectedPlaylistGroup = null;

                // Handle different selection types
                if (selectedItem is PlaylistGroupTreeViewModel groupTree)
                {
                    // Find the corresponding PlaylistGroupViewModel
                    var groupVm = _vm.PlaylistGroups.FirstOrDefault(g => g.Model.Id == groupTree.Id);
                    if (groupVm != null)
                    {
                        _vm.SelectedPlaylistGroup = groupVm;
                        Debug.WriteLine($"MainWindow: Selected playlist group: {groupVm.Name}");
                    }
                    
                    // Reset mesh monitor combo when no mesh is selected
                    PART_MeshMonitorCombo.SelectedIndex = 0;
                }
                else if (selectedItem is ImportedVideoTreeViewModel videoTree)
                {
                    // Set the selected imported video
                    var video = videoTree.ImportedVideo;
                    _vm.SelectedImportedVideo = video;
                    Debug.WriteLine($"MainWindow: Selected imported video: {video.Name}");
                    
                    // Reset mesh monitor combo when no mesh is selected
                    PART_MeshMonitorCombo.SelectedIndex = 0;
                }
                else if (selectedItem is LayerViewModel meshVm)
                {
                    // Set both the mesh layer AND its parent video
                    _vm.SelectedMeshLayer = meshVm;
                    
                    // Find the parent imported video that contains this mesh
                    var parentVideo = _vm.ImportedVideos.FirstOrDefault(v => v.MeshLayers.Contains(meshVm));
                    if (parentVideo != null)
                    {
                        _vm.SelectedImportedVideo = parentVideo;
                        Debug.WriteLine($"MainWindow: Selected mesh layer: {meshVm.Name} (parent: {parentVideo.Name})");
                    }
                    else
                    {
                        Debug.WriteLine($"MainWindow: Selected mesh layer: {meshVm.Name} (no parent found)");
                    }
                    
                    // Sync PART_MeshMonitorCombo with the mesh layer's target monitor
                    var targetMonitor = meshVm.Model?.TargetMonitorIndex ?? -1;
                    // SelectedIndex 0 = "(None)", so targetMonitor -1 maps to index 0, targetMonitor 0 maps to index 1, etc.
                    var comboIndex = targetMonitor + 1;
                    if (comboIndex >= 0 && comboIndex < PART_MeshMonitorCombo.Items.Count)
                    {
                        PART_MeshMonitorCombo.SelectedIndex = comboIndex;
                    }
                    else
                    {
                        PART_MeshMonitorCombo.SelectedIndex = 0;
                    }
                }
                else
                {
                    // Nothing selected - reset combo
                    PART_MeshMonitorCombo.SelectedIndex = 0;
                }

                // Update command states
                (_vm.CreateMeshCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (_vm.DeleteMeshCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (_vm.CopyMeshCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (_vm.PasteMeshCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: TreeView_SelectedItemChanged failed: {ex}");
            }
        }
        
        private void TreeView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                // Ensure the item under the mouse becomes selected so context menu commands operate on it
                var original = e.OriginalSource as DependencyObject;
                while (original != null && !(original is TreeViewItem))
                {
                    original = VisualTreeHelper.GetParent(original);
                }

                if (original is TreeViewItem item)
                {
                    item.IsSelected = true;
                    // Allow event to continue so context menu opens
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: TreeView_PreviewMouseRightButtonDown failed: {ex}");
            }
        }

        private void CreateMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Invoke the view-model command to create a mesh for the currently selected imported video
                if (_vm?.CreateMeshCommand != null && _vm.CreateMeshCommand.CanExecute(null))
                {
                    _vm.CreateMeshCommand.Execute(null);
                }
                else
                {
                    Debug.WriteLine("MainWindow: CreateMeshCommand cannot execute or is null");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: CreateMeshMenuItem_Click failed: {ex}");
            }
        }

        private void RenameGroupMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try { Debug.WriteLine("MainWindow: RenameGroupMenuItem_Click - not implemented"); }
            catch (Exception ex) { Debug.WriteLine($"RenameGroupMenuItem_Click failed: {ex}"); }
        }

        private void DeleteGroupMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try { Debug.WriteLine("MainWindow: DeleteGroupMenuItem_Click - not implemented"); }
            catch (Exception ex) { Debug.WriteLine($"DeleteGroupMenuItem_Click failed: {ex}"); }
        }

        private void MoveGroupUpMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try { Debug.WriteLine("MainWindow: MoveGroupUpMenuItem_Click - not implemented"); }
            catch (Exception ex) { Debug.WriteLine($"MoveGroupUpMenuItem_Click failed: {ex}"); }
        }

        private void MoveGroupDownMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try { Debug.WriteLine("MainWindow: MoveGroupDownMenuItem_Click - not implemented"); }
            catch (Exception ex) { Debug.WriteLine($"MoveGroupDownMenuItem_Click failed: {ex}"); }
        }

        private void PlayGroupNowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try { Debug.WriteLine("MainWindow: PlayGroupNowMenuItem_Click - not implemented"); }
            catch (Exception ex) { Debug.WriteLine($"PlayGroupNowMenuItem_Click failed: {ex}"); }
        }

        private void VideoContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            try { Debug.WriteLine("MainWindow: VideoContextMenu_Opened"); }
            catch (Exception ex) { Debug.WriteLine($"VideoContextMenu_Opened failed: {ex}"); }
        }

        private void DeleteSourceMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try 
            { 
                Debug.WriteLine("MainWindow: DeleteSourceMenuItem_Click");
                DeleteSelectedVideo();
            }
            catch (Exception ex) { Debug.WriteLine($"DeleteSourceMenuItem_Click failed: {ex}"); }
        }

        private void RemoveVideoFromGroupMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try 
            { 
                Debug.WriteLine("MainWindow: RemoveVideoFromGroupMenuItem_Click");
                // Get the selected video and remove it from its current group
                if (_vm.SelectedImportedVideo != null)
                {
                    var video = _vm.SelectedImportedVideo;
                    
                    // Find the parent group and remove the video
                    foreach (var group in _vm.PlaylistGroups)
                    {
                        if (group.Videos.Contains(video))
                        {
                            group.Videos.Remove(video);
                            Debug.WriteLine($"MainWindow: Removed '{video.Name}' from group '{group.Name}'");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"RemoveVideoFromGroupMenuItem_Click failed: {ex}"); }
        }

        private void CopyMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try { Debug.WriteLine("MainWindow: CopyMeshMenuItem_Click - not implemented"); }
            catch (Exception ex) { Debug.WriteLine($"CopyMeshMenuItem_Click failed: {ex}"); }
        }

        private void PasteMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try { Debug.WriteLine("MainWindow: PasteMeshMenuItem_Click - not implemented"); }
            catch (Exception ex) { Debug.WriteLine($"PasteMeshMenuItem_Click failed: {ex}"); }
        }

        private void HideSourceOutputMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try { Debug.WriteLine("MainWindow: HideSourceOutputMenuItem_Click - not implemented"); }
            catch (Exception ex) { Debug.WriteLine($"HideSourceOutputMenuItem_Click failed: {ex}"); }
        }

        private void RenameMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try { Debug.WriteLine("MainWindow: RenameMeshMenuItem_Click - not implemented"); }
            catch (Exception ex) { Debug.WriteLine($"RenameMeshMenuItem_Click failed: {ex}"); }
        }

        private void DeleteMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try 
            { 
                Debug.WriteLine("MainWindow: DeleteMeshMenuItem_Click");
                DeleteSelectedMeshLayer();
            }
            catch (Exception ex) { Debug.WriteLine($"DeleteMeshMenuItem_Click failed: {ex}"); }
        }

        private void DeleteSelectedVideo()
        {
            try
            {
                if (_vm.SelectedImportedVideo == null) return;

                var videoToDelete = _vm.SelectedImportedVideo;

                // Stop the video first
                _videoService.UnregisterLayerAsync(videoToDelete.Id).GetAwaiter().GetResult();

                // Remove from collection
                _vm.ImportedVideos.Remove(videoToDelete);

                Debug.WriteLine($"MainWindow: Deleted video '{videoToDelete.Name}'");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: DeleteSelectedVideo failed: {ex}");
            }
        }

        private void DeleteSelectedMeshLayer()
        {
            try
            {
                if (_vm.SelectedMeshLayer == null || _vm.SelectedImportedVideo == null) return;

                var meshToDelete = _vm.SelectedMeshLayer;
                var parentVideo = _vm.SelectedImportedVideo;

                // Remove from collection
                parentVideo.MeshLayers.Remove(meshToDelete);

                Debug.WriteLine($"MainWindow: Deleted mesh layer '{meshToDelete.Name}'");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow: DeleteSelectedMeshLayer failed: {ex}");
            }
        }

        private void DeleteVideo_Click(object sender, RoutedEventArgs e)
        {
            DeleteSelectedVideo();
        }

        private void DeleteMeshLayer_Click(object sender, RoutedEventArgs e)
        {
            DeleteSelectedMeshLayer();
        }
    }
}