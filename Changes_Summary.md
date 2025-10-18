# Changes to Fix Mesh Layer Output to Specific Projectors

## Problem
The user imported the same video twice, created Mesh 1 for projector output 1, and Meshes 2 and 3 for projector output 2. However, all mesh layers were being sent to both projectors instead of only to their assigned ones.

## Root Cause
The system was using a single renderer for all layers, composing all layers into one output and mirroring it to all fullscreen windows. There was no per-monitor rendering or filtering of layers by `TargetMonitorIndex`.

## Solution
Implement per-monitor rendering by creating separate `SoftwareRenderer` instances for each fullscreen window. Modify the `RendererManager` to submit layers to the appropriate renderer based on `TargetMonitorIndex`.

### Key Changes

1. **RendererManager.cs**:
   - Added `Dictionary<int, IRenderer> _monitorRenderers` to store per-monitor renderers.
   - Added `OnMonitorFrameReady` method to handle frame ready events from monitor renderers.
   - Modified `ShowFullScreenWindow` to create a dedicated `SoftwareRenderer` and `RenderLoop` for each monitor.
   - Added `SubmitLayerFrameForMonitor` method to submit frames to the correct renderer based on `targetMonitorIndex`.
   - Removed mirroring of composed frames to fullscreen windows in `OnFrameReady`.

2. **VideoService.cs**:
   - Updated `ProcessDecodedFrameOnUi` to use `SubmitLayerFrameForMonitor` with appropriate `targetMonitorIndex` for host and mesh layers.
   - Updated `RegisterMeshLayerAsync`, `UnregisterMeshLayerAsync`, `HideSourceOutputAndMeshesAsync`, and `ShowSourceOutputAndMeshesAsync` to use the new method.

3. **OutputMeshEditorControl.xaml.cs**:
   - Updated `WriteBackMeshPoints` to use `SubmitLayerFrameForMonitor` with the layer's `TargetMonitorIndex`.

4. **MeshEditorControl.xaml.cs**:
   - Updated `ForwardOutputRect` to use `SubmitLayerFrameForMonitor` with the selected layer's `TargetMonitorIndex`.

5. **MainWindow.xaml.cs**:
   - Updated `HideSourceOutputAndMeshesAsync` and `ShowSourceOutputAndMeshesAsync` to use `SubmitLayerFrameForMonitor`.

## How It Works
- When a fullscreen window is shown for a monitor, a new `SoftwareRenderer` is created and initialized with the output dimensions.
- A `RenderLoop` is started for that renderer to drive rendering.
- Frames are submitted to the renderer corresponding to the `TargetMonitorIndex` of the layer.
- Each monitor renderer composes only the layers assigned to it and displays the result on its fullscreen window.
- Overlays are already handled per-monitor via existing logic.

## Testing
- Build successful.
- Each mesh layer should now only appear on the projector assigned via `TargetMonitorIndex`.
- Host layers (imported videos) are submitted with `targetMonitorIndex = -1`, so they appear on the main output unless assigned otherwise.</content>
<parameter name="filePath">Changes_Summary.md