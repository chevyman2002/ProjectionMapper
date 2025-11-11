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
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using ProjectionMapper.Views;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

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

        // Monitor list for UI
        private readonly ObservableCollection<MonitorItem> _monitorItems = new();
        private List<MonitorInfo> _monitors = new();

        private class MonitorItem
        {
            public int Index { get; set; }
            public string Name { get; set; } = string.Empty;
            public override string ToString() => Name;
        }

        private void PART_MonitorCombo_DropDownOpened(object? sender, EventArgs e)
        {
            try
            {
                RefreshMonitors();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PART_MonitorCombo_DropDownOpened: failed to refresh monitors: {ex}");
            }
        }

        private void RefreshMonitors()
        {
            try
            {
                // remember current selection so we can restore if possible
                int prevSelected = -1;
                try { prevSelected = PART_MonitorCombo != null ? PART_MonitorCombo.SelectedIndex : -1; } catch { prevSelected = -1; }

                var newMonitors = EnumerateMonitors();

                // update internal list
                _monitors = newMonitors;

                // rebuild ObservableCollection of items on UI thread
                Dispatcher.Invoke(() =>
                {
                    _monitorItems.Clear();
                    for (int i = 0; i < _monitors.Count; i++)
                    {
                        var m = _monitors[i];
                        _monitorItems.Add(new MonitorItem { Index = i, Name = $"Display {i + 1} ({m.Width}x{m.Height})" });
                    }

                    // try to restore previous selection if still valid
                    if (PART_MonitorCombo != null)
                    {
                        if (prevSelected >= 0 && prevSelected < _monitorItems.Count)
                        {
                            PART_MonitorCombo.SelectedIndex = prevSelected;
                        }
                        else
                        {
                            PART_MonitorCombo.SelectedIndex = -1;
                        }
                    }
                }, DispatcherPriority.Normal);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RefreshMonitors: failed: {ex}");
            }
        }

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

            // Wire playback controls to VideoService
            _vm.PlayPauseRequestedAsync += async () =>
                  {
                try
            {
                 // Toggle playback: after VM toggles IsPlaying, run the desired state
                    if (_vm.IsPlaying)
       {
       Debug.WriteLine("VideoService: Resuming all layers");
            await _videoService.ResumeAllAsync();
 Debug.WriteLine("VideoService: Resume completed");
      }
 else
             {
       Debug.WriteLine("VideoService: Pausing all layers");
     await _videoService.PauseAllAsync();
              Debug.WriteLine("VideoService: Pause completed");
     }
     }
          catch (Exception ex)
{
  Debug.WriteLine($"VideoService: PlayPause operation failed: {ex}");
                }
          };

     _vm.RestartRequestedAsync += async () =>
     {
    try
  {
  Debug.WriteLine("VideoService: Restarting all layers");
   await _videoService.RestartAllAsync();
  Debug.WriteLine("VideoService: Restart completed");
 }
        catch (Exception ex)
             {
          Debug.WriteLine($"VideoService: Restart operation failed: {ex}");
   }
            };

            // populate monitor list for UI using Win32 EnumDisplayMonitors and store monitor info
            try
            {
                _monitors = EnumerateMonitors();
                for (int i = 0; i < _monitors.Count; i++)
                {
                    var m = _monitors[i];
                    _monitorItems.Add(new MonitorItem { Index = i, Name = $"Display {i + 1} ({m.Width}x{m.Height})" });
                }
            }
            catch { }

            // assign itemsource for combo (ComboBox defined in XAML with x:Name PART_MonitorCombo)
            Loaded += (_, __) =>
            {
                if (PART_MonitorCombo != null)
                {
                    PART_MonitorCombo.ItemsSource = _monitorItems;
                    PART_MonitorCombo.SelectionChanged += PART_MonitorCombo_SelectionChanged;
                    // Re-scan displays each time the drop-down is opened so available monitors are up-to-date
                    try { PART_MonitorCombo.DropDownOpened += PART_MonitorCombo_DropDownOpened; } catch { }
                }
                // We prefer a single monitor combo for selecting the output monitor for the selected source.
                // Hide the per-mesh combo to avoid duplicate controls; the PART_MonitorCombo will be used to
                // assign the host and all its mesh layers to the chosen monitor.
                try { if (PART_MeshMonitorCombo != null) PART_MeshMonitorCombo.Visibility = Visibility.Collapsed; } catch { }

                // Wire up audio control event handlers
                HookAudioControls();

                // Wire mesh overlay checkbox
                try
                {
                    if (PART_ShowMeshOverlayCheckbox != null)
                    {
                        PART_ShowMeshOverlayCheckbox.Checked += (s, e) => { _rendererManager.ShowMeshOverlay = true; };
                        PART_ShowMeshOverlayCheckbox.Unchecked += (s, e) => { _rendererManager.ShowMeshOverlay = false; _rendererManager.ClearAllOverlays(); };
                        // default checked
                        PART_ShowMeshOverlayCheckbox.IsChecked = _rendererManager.ShowMeshOverlay;
                    }
                    if (PART_GlobalShowMeshOverlayCheckbox != null)
                    {
                        PART_GlobalShowMeshOverlayCheckbox.Checked += (s, e) => { _rendererManager.ShowMeshOverlay = true; };
                        PART_GlobalShowMeshOverlayCheckbox.Unchecked += (s, e) => { _rendererManager.ShowMeshOverlay = false; _rendererManager.ClearAllOverlays(); };
                        PART_GlobalShowMeshOverlayCheckbox.IsChecked = _rendererManager.ShowMeshOverlay;
                    }
                    // Show grid checkbox - call SetCoordinateGrid on output host when checked
                    if (PART_ShowGridCheckbox != null)
                    {
                        PART_ShowGridCheckbox.Checked += (s, e) =>
                        {
                            try
                            {
                                // display grid every 100 renderer pixels by default
                                PART_OutputHost?.SetCoordinateGrid(100);
                            }
                            catch { }
                        };
                        PART_ShowGridCheckbox.Unchecked += (s, e) =>
                        {
                            try { PART_OutputHost?.ClearGridOverlay(); } catch { }
                        };
                        PART_ShowGridCheckbox.IsChecked = false;
                    }
                }
                catch { }
            };

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

            // Hook mesh layer created event so we can register with VideoService
            _vm.MeshLayerCreated += async mesh =>
            {
                if (mesh != null && !string.IsNullOrEmpty(mesh.Id))
                {
                    try
                    {
                        // If the user has selected a monitor for the imported video, ensure the new mesh
                        // inherits that target so overlays and compositor mapping apply to the same output host.
                        var imported = _vm.SelectedImportedVideo;
                        if (imported?.HostLayer != null)
                        {
                            mesh.TargetMonitorIndex = imported.HostLayer.TargetMonitorIndex;
                        }
                    }
                    catch { }

                    await _videoService.RegisterMeshLayerAsync(mesh);

                    // Also update all mesh overlays for the imported video
                    UpdateAllMeshOverlaysForImportedVideo(_vm.SelectedImportedVideo);
                }
            };
        }

        private void HookAudioControls()
        {
            try
            {
                // Wire PlayAudio checkbox
                if (PART_PlayAudioCheckbox != null)
                {
                    PART_PlayAudioCheckbox.Checked += PlayAudioCheckbox_CheckedChanged;
                    PART_PlayAudioCheckbox.Unchecked += PlayAudioCheckbox_CheckedChanged;
                }

                // Volume slider removed - volume is now fixed at 100%
      // Mute checkbox removed - audio is off by default until "Play Audio" is checked
            }
     catch { }
        }

        private void PlayAudioCheckbox_CheckedChanged(object sender, RoutedEventArgs e)
        {
try
{
          var imported = _vm.SelectedImportedVideo;
  if (imported?.HostLayer == null) return;

        var playAudio = imported.PlayAudio; // This reads from the bound property

       if (playAudio)
     {
        // Start audio for host layer without re-registering the decoder (prevents duplicate decoders)
         if (!string.IsNullOrEmpty(imported.HostLayer.Id))
       {
      _videoService.StartAudioForLayer(imported.HostLayer.Id);
         }
     }
    else
      {
       // Stop audio for host layer
      if (!string.IsNullOrEmpty(imported.HostLayer.Id))
     {
     _videoService.StopAudioForLayer(imported.HostLayer.Id);
           }
        }
  }
      catch { }
  }

        private void PART_MeshMonitorCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            var combo = sender as ComboBox;
            if (combo == null) return;
            var item = combo.SelectedItem as MonitorItem;
            var meshVm = _vm.SelectedMeshLayer;
            if (meshVm == null) return;

            // set target monitor on underlying model
            if (meshVm.Model != null)
            {
                meshVm.Model.TargetMonitorIndex = item != null ? item.Index : -1;

                // show fullscreen for this mesh if assigned
                if (meshVm.Model.TargetMonitorIndex >= 0 && meshVm.Model.TargetMonitorIndex < _monitors.Count)
                {
                    CreateOrShowFullScreenForMonitor(meshVm.Model.TargetMonitorIndex);
                }
            }
        }

        private void CreateOrShowFullScreenForMonitor(int monitorIndex)
        {
            if (monitorIndex < 0 || monitorIndex >= _monitors.Count) return;
            var mon = _monitors[monitorIndex];

            // create fullscreen window and position it using monitor bounds converted to DIPs
            var win = new FullScreenOutputWindow
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true
            };

            // Convert monitor pixel bounds to WPF device-independent units
            var source = PresentationSource.FromVisual(this);
            double dpiX = 1.0, dpiY = 1.0;
            if (source != null && source.CompositionTarget != null)
            {
                // TransformFromDevice is a Matrix, but CompositionTarget has TransformFromDevice
                dpiX = source.CompositionTarget.TransformFromDevice.M11;
                dpiY = source.CompositionTarget.TransformFromDevice.M22;
            }

            win.Left = mon.Left * dpiX;
            win.Top = mon.Top * dpiY;
            win.Width = mon.Width * dpiX;
            win.Height = mon.Height * dpiY;

            win.WindowStartupLocation = WindowStartupLocation.Manual;

            // Ensure native handle exists so we can position the window before showing it
            try
            {
                var helper = new WindowInteropHelper(win);
                var hwnd = helper.EnsureHandle();

                const uint SWP_SHOWWINDOW = 0x0040;
                var flags = SWP_SHOWWINDOW;

                Debug.WriteLine($"CreateOrShowFullScreenForMonitor: calling SetWindowPos for monitor {monitorIndex} -> rect {mon.Left},{mon.Top} {mon.Width}x{mon.Height}");
                bool ok = SetWindowPos(hwnd, HWND_TOPMOST, mon.Left, mon.Top, mon.Width, mon.Height, flags);
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                Debug.WriteLine($"CreateOrShowFullScreenForMonitor: SetWindowPos returned {ok}, GetLastError={err}");

                if (!ok)
                {
                    try
                    {
                        const int GWL_STYLE = -16;
                        const int WS_POPUP = unchecked((int)0x80000000);
                        var prev = GetWindowLong(hwnd, GWL_STYLE);
                        SetWindowLong(hwnd, GWL_STYLE, prev | WS_POPUP);
                        ok = SetWindowPos(hwnd, HWND_TOPMOST, mon.Left, mon.Top, mon.Width, mon.Height, flags);
                        err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                        Debug.WriteLine($"CreateOrShowFullScreenForMonitor: retry SetWindowPos returned {ok}, GetLastError={err}");
                    }
                    catch (Exception ex) { Debug.WriteLine($"CreateOrShowFullScreenForMonitor: retry SetWindowPos exception: {ex}"); }
                }

                // Show the window after native positioning
                win.Show();

                try { _rendererManager.ShowFullScreenWindow(monitorIndex, win); Debug.WriteLine($"CreateOrShowFullScreenForMonitor: ShowFullScreenWindow success for monitor {monitorIndex}"); } catch (Exception ex) { Debug.WriteLine($"CreateOrShowFullScreenForMonitor: ShowFullScreenWindow failed: {ex}"); }
                try { _rendererManager.AttachHost(monitorIndex, win); Debug.WriteLine($"CreateOrShowFullScreenForMonitor: AttachHost success for monitor {monitorIndex}"); } catch (Exception ex) { Debug.WriteLine($"CreateOrShowFullScreenForMonitor: AttachHost failed: {ex}"); }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CreateOrShowFullScreenForMonitor: placement/show failed: {ex}");
                try { win.Show(); _rendererManager.ShowFullScreenWindow(monitorIndex, win); _rendererManager.AttachHost(monitorIndex, win); } catch (Exception ex2) { Debug.WriteLine($"CreateOrShowFullScreenForMonitor: fallback show failed: {ex2}"); }
            }
        }

        private void PART_MonitorCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            var combo = sender as ComboBox;
            if (combo == null) return;
            var item = combo.SelectedItem as MonitorItem;
            var imported = _vm.SelectedImportedVideo;
            if (imported?.HostLayer != null && item != null)
            {
                // Apply selection to host layer
                imported.HostLayer.TargetMonitorIndex = item.Index;

                // Apply same monitor to all mesh layers of this imported video so they collectively display on the chosen monitor
                foreach (var meshVm in imported.MeshLayers)
                {
                    try { if (meshVm.Model != null) meshVm.Model.TargetMonitorIndex = item.Index; } catch { }
                }

                // Create or show fullscreen window on the selected monitor
                if (item.Index >= 0 && item.Index < _monitors.Count)
                {
                    _rendererManager.HideFullScreenWindow(item.Index);
                    CreateOrShowFullScreenForMonitor(item.Index);
                }
                else
                {
                    // hide any existing fullscreen for this index
                    _rendererManager.HideFullScreenWindow(item.Index);
                }

                // Update all mesh overlays for the new monitor
                UpdateAllMeshOverlaysForImportedVideo(imported);
            }
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
                    // play immediately for isolated preview only; do not submit frames to main renderer until mesh created
                    PreviewOnly = true
                };

                // Determine default decode size from input host if available
                var hostForLayer = PART_InputHost ?? PART_OutputHost;
                var w = (int)Math.Max(1, hostForLayer.ActualWidth);
                var h = (int)Math.Max(1, hostForLayer.ActualHeight);
                if (w == 0 || h == 0) { w = 1280; h = 720; }
                hostLayer.X = 0; hostLayer.Y = 0; hostLayer.Width = w; hostLayer.Height = h;

                // Register decoder for the imported video so MeshEditor can show isolated preview and so output is submitted to renderer
                await _videoService.RegisterLayerAsync(hostLayer);
                importedVm.HostLayer = hostLayer;

                // Notify the ImportedVideoViewModel that HostLayer changed so bindings update
                importedVm.NotifyHostLayerChanged();

                // Add to view model collection (parent node). Do NOT create mesh layer automatically.
                // Ensure we marshal back to UI thread
                await Dispatcher.InvokeAsync(() =>
                {
                    _vm.ImportedVideos.Add(importedVm);
                    // Select the imported video in the VM
                    _vm.SelectedImportedVideo = importedVm;

                    // update monitor combo selection if available
                    if (PART_MonitorCombo != null && importedVm.HostLayer != null)
                    {
                        PART_MonitorCombo.SelectedIndex = importedVm.HostLayer.TargetMonitorIndex >= 0 ? importedVm.HostLayer.TargetMonitorIndex : -1;
                    }
                }, DispatcherPriority.Normal);
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
            await Dispatcher.InvokeAsync(() =>
            {
                _vm.ImportedVideos.Remove(imported);

                // Clear selection if it was the deleted one
                if (_vm.SelectedImportedVideo == imported) _vm.SelectedImportedVideo = null;
                if (_vm.SelectedMeshLayer != null && imported.MeshLayers.Contains(_vm.SelectedMeshLayer)) _vm.SelectedMeshLayer = null;
            });
        }

        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            // If a mesh LayerViewModel is selected, set SelectedMeshLayer on VM
            if (e.NewValue is LayerViewModel layerVm)
            {
                _vm.SelectedMeshLayer = layerVm;
                // reflect current mesh monitor selection in UI
                if (PART_MeshMonitorCombo != null && layerVm.Model != null)
                {
                    PART_MeshMonitorCombo.SelectedIndex = layerVm.Model.TargetMonitorIndex >= 0 ? layerVm.Model.TargetMonitorIndex : -1;
                }
                
                // Update all mesh overlays for the target monitor
                UpdateAllMeshOverlaysForImportedVideo(_vm.SelectedImportedVideo);
                return;
            }

            // If an imported video (parent) is selected, set SelectedImportedVideo
            if (e.NewValue is ImportedVideoViewModel imported)
            {
                _vm.SelectedImportedVideo = imported;
                // Optionally select nothing for mesh layer
                _vm.SelectedMeshLayer = null;

                // update monitor combo selection
                if (PART_MonitorCombo != null && imported.HostLayer != null)
                {
                    PART_MonitorCombo.SelectedIndex = imported.HostLayer.TargetMonitorIndex >= 0 ? imported.HostLayer.TargetMonitorIndex : -1;
                }

                // Ensure PlayAudio checkbox reflects state; it's bound in XAML so this is just to ensure service state
                try
                {
                    // If PlayAudio is true and no audio player exists, start audio
                    if (imported.HostLayer != null && imported.HostLayer.PlayAudio)
                    {
                        _ = _videoService.RegisterLayerAsync(imported.HostLayer, playAudio: true);
                    }
                }
                catch { }

                // Update all mesh overlays for this imported video
                UpdateAllMeshOverlaysForImportedVideo(imported);
                return;
            }

            // Otherwise clear selections
            _vm.SelectedImportedVideo = null;
            _vm.SelectedMeshLayer = null;
        }

        private void UpdateAllMeshOverlaysForImportedVideo(ImportedVideoViewModel? imported)
        {
            if (imported == null) return;

            try
            {
                var targetMonitor = imported.HostLayer?.TargetMonitorIndex ?? -1;
                
                // Update overlays for all mesh layers of this imported video
                foreach (var meshVm in imported.MeshLayers)
                {
                    if (meshVm?.Model == null) continue;
                    
                    var layerId = meshVm.Model.Id;
                    if (string.IsNullOrEmpty(layerId)) continue;
                    
                    var showOverlayPref = meshVm.Model.ShowOverlay;
                    if (!showOverlayPref) continue;

                    try
                    {
                        // Map normalized output mesh points to renderer coordinates
                        Point[]? quadForRenderer = null;
                        try
                        {
                            quadForRenderer = _rendererManager?.MapNormalizedToRendererPoints(meshVm.OutputMeshPoints, targetMonitor >= 0 ? targetMonitor : null);
                        }
                        catch { quadForRenderer = null; }

                        if (quadForRenderer != null && quadForRenderer.Length >= 4)
                        {
                            _rendererManager?.AddMeshOverlayForMonitor(targetMonitor >= 0 ? targetMonitor : null, quadForRenderer, true, layerId);
                        }
                    }
                    catch { }
                }
            }
            catch { }
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

        private void ImportedContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            var cm = sender as ContextMenu;
            if (cm == null) return;
            var fe = cm.PlacementTarget as FrameworkElement;
            var imported = fe?.DataContext as ImportedVideoViewModel;
            if (imported == null) return;

            // find hide/show menu item by Tag
            var mi = cm.Items.OfType<MenuItem>().FirstOrDefault(m => m.Tag as string == "HideShowToggle");
            if (mi == null) return;

            // If host layer is paused/hidden, show 'Show', otherwise 'Hide'
            if (imported.HostLayer == null)
            {
                mi.Header = "Hide Source Output/Meshes";
                return;
            }

            var visible = imported.HostLayer.Visible;
            mi.Header = visible ? "Hide Source Output/Meshes" : "Show Source Output/Meshes";
        }

        private async void HideSourceOutputMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var mi = sender as MenuItem;
            var cm = FindParentContextMenu(mi);
            var imported = (cm?.PlacementTarget as FrameworkElement)?.DataContext as ImportedVideoViewModel;
            if (imported != null)
            {
                try
                {
                    if (imported.HostLayer != null)
                    {
                        // toggle
                        if (imported.HostLayer.Visible)
                        {
                            // hide: update UI immediately
                            await Dispatcher.InvokeAsync(() =>
                            {
                                foreach (var meshVm in imported.MeshLayers.ToList()) meshVm.Visible = false;
                                imported.HostLayer.Visible = false;
                            }, DispatcherPriority.Normal);

                            // pause decoding and hide via service
                            await _videoService.HideSourceOutputAndMeshesAsync(imported.HostLayer.Id);

                            // hide any fullscreen assigned to host
                            if (imported.HostLayer.TargetMonitorIndex >= 0)
                            {
                                _rendererManager.HideFullScreenWindow(imported.HostLayer.TargetMonitorIndex);
                            }

                            // hide any fullscreen assigned to mesh layers
                            foreach (var meshVm in imported.MeshLayers)
                            {
                                if (meshVm.Model?.TargetMonitorIndex >= 0)
                                {
                                    _rendererManager.HideFullScreenWindow(meshVm.Model.TargetMonitorIndex);
                                }
                            }
                        }
                        else
                        {
                            // show: resume decoding via service first
                            await _videoService.ShowSourceOutputAndMeshesAsync(imported.HostLayer.Id);

                            // update UI on UI thread
                            await Dispatcher.InvokeAsync(() =>
                            {
                                imported.HostLayer.Visible = true;
                                foreach (var meshVm in imported.MeshLayers.ToList()) meshVm.Visible = true;
                            }, DispatcherPriority.Normal);

                            // restore fullscreen for host if assigned
                            if (imported.HostLayer.TargetMonitorIndex >= 0)
                            {
                                CreateOrShowFullScreenForMonitor(imported.HostLayer.TargetMonitorIndex);
                            }

                            // restore fullscreen for meshes if assigned
                            foreach (var meshVm in imported.MeshLayers)
                            {
                                if (meshVm.Model?.TargetMonitorIndex >= 0)
                                {
                                    CreateOrShowFullScreenForMonitor(meshVm.Model.TargetMonitorIndex);
                                }
                            }
                        }
                    }
                }
                catch { }
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

        private void RenameMeshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var mi = sender as MenuItem;
            LayerViewModel? layerVm = null;

            // Try command parameter first (safer when context menu is separated)
            try
            {
                layerVm = mi?.CommandParameter as LayerViewModel;
            }
            catch { }

            // Fallback: attempt to find the placement target's DataContext
            if (layerVm == null)
            {
                try
                {
                    var cm = FindParentContextMenu(mi);
                    layerVm = (cm?.PlacementTarget as FrameworkElement)?.DataContext as LayerViewModel;
                }
                catch { }
            }

            // Final fallback: use currently selected mesh in VM
            if (layerVm == null) layerVm = _vm.SelectedMeshLayer;
            if (layerVm == null) return;

            // Simple prompt dialog
            var prompt = new Window
            {
                Title = "Rename Mesh",
                Width = 400,
                Height = 140,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var sp = new StackPanel { Margin = new Thickness(10) };
            sp.Children.Add(new TextBlock { Text = "Enter new name:", Margin = new Thickness(0,0,0,6) });
            var tb = new TextBox { Text = layerVm.Name ?? string.Empty };
            sp.Children.Add(tb);
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,10,0,0) };
            var ok = new Button { Content = "OK", Width = 80, IsDefault = true };
            var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true, Margin = new Thickness(8,0,0,0) };
            btnPanel.Children.Add(ok); btnPanel.Children.Add(cancel);
            sp.Children.Add(btnPanel);
            prompt.Content = sp;

            ok.Click += (_, __) => prompt.DialogResult = true;

            if (prompt.ShowDialog() == true)
            {
                var newName = tb.Text?.Trim();
                if (!string.IsNullOrEmpty(newName))
                {
                    // Ensure uniqueness - if name exists, append numeric suffix
                    var baseName = newName;
                    int suffix = 1;
                    bool exists;
                    do
                    {
                        exists = _vm.ImportedVideos.SelectMany(iv => iv.MeshLayers).Any(m => m != layerVm && string.Equals(m.Name, newName, StringComparison.OrdinalIgnoreCase));
                        if (exists)
                        {
                            newName = baseName + " " + (++suffix).ToString();
                        }
                    } while (exists);

                    // Update viewmodel property which updates underlying model and notifies UI
                    layerVm.Name = newName;

                    // Ensure selection reflects renamed item
                    try { _vm.SelectedMeshLayer = layerVm; } catch { }
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

        private record MonitorInfo(int Width, int Height, int Left, int Top);

        private static List<MonitorInfo> EnumerateMonitors()
        {
            try
            {
                var list = new List<MonitorInfo>();

                MonitorEnumDelegate del = (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
                {
                    try
                    {
                        var mi = new MONITORINFOEX();
                        mi.cbSize = Marshal.SizeOf<MONITORINFOEX>();
                        if (GetMonitorInfo(hMonitor, ref mi))
                        {
                            var w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                            var h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                            var l = mi.rcMonitor.Left;
                            var t = mi.rcMonitor.Top;
                            list.Add(new MonitorInfo(w, h, l, t));
                        }
                    }
                    catch { }
                    return true;
                };

                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, del, IntPtr.Zero);

                return list;
            }
            catch
            {
                return new List<MonitorInfo>();
            }
        }

        private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll", SetLastError = false)]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        // P/Invoke for positioning windows
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }
    }
}