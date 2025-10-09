# Migration & Implementation Plan
Purpose: produce a detailed, actionable plan to make the new .NET 9 C# application (ProjectionMapper) match the GUI look-and-behave of the forked C++/Qt app (ProjectionMapper-v1 / MapMap fork), replacing Qt with Windows-native libraries and FFmpeg-based video handling.

Reference repositories (top-level snapshot used for this plan)
- ProjectionMapper-v1 (C++ / Qt) — examined top-level entries:
  - .github/, .gitignore, .travis.yml, CHANGELOG.md, CODE_OF_CONDUCT.md, CONTRIBUTING.md, HACKING, INSTALL.md, LICENSE, OSC, README.md, TODO, VERSION.txt, appveyor.yml
  - main.qrc, mapmap.pro
  - directories: docs/, examples/, prototypes/, resources/, scripts/, src/, tests/, translations/
- ProjectionMapper (C#, .NET) — examined top-level entries:
  - .gitattributes, .gitignore
  - App.xaml, App.xaml.cs
  - AssemblyInfo.cs
  - LICENSE.txt
  - MainWindow.xaml, MainWindow.xaml.cs
  - ProjectionMapper.csproj, ProjectionMapper.slnx
  - README.md

Summary of intent
- Recreate the visual structure, UX flow, and editor behaviors of ProjectionMapper-v1's Qt UI in the new .NET 9/WPF (or WinUI/WPF hybrid) application while using FFmpeg (for decoding) and Windows-native rendering (Direct3D 11/12 interop) for performant real-time previews and projector-accurate mapping.
- Aim for feature parity for the user-facing GUI (menus, toolbars, dockable panels, mesh editing, layer/property inspectors, preview), parity for core workflows (import media, create surfaces, edit meshes, save/load projects), and robust background media processing pipelines (FFmpeg-based decode + GPU upload).

High-level architecture & design choices
- UI framework: WPF (XAML) with MVVM pattern. Rationale: current C# app already uses MainWindow.xaml; leverage WPF's mature tooling and data binding. Where necessary for high-performance rendering, host a native D3D11/12 surface via a D3DImage interop control or use a swap-chain panel hosted inside WPF.
- Rendering backend:
  - Video decoding: FFmpeg (use a maintained, non-deprecated wrapper such as FFmpeg.AutoGen).
  - GPU interop: Vortice.Windows (modern, actively maintained .NET bindings for Direct3D / DXGI) or Silk.NET for lower-level control. Avoid deprecated SharpDX.
  - Frame handoff: decode frames in background threads, push to GPU textures with proper synchronization primitives; present textures to WPF via D3DImage or shared swapchain approach.
- Concurrency & threading:
  - All long running tasks (decoding, texture uploads, heavy math) will run on background threads; UI thread only handles event marshaling and light UI operations.
  - Use CancellationToken + Task-based asynchronous code + ConcurrentQueue or Channel<T> for producer-consumer.
  - Use lock-free or minimal-lock synchronization; when GPU resources are shared use fences or dispatcher marshalling per renderer thread.
- App model: MVVM with clear division:
  - Models: ProjectModel, SurfaceModel, LayerModel, MeshModel, ResourceModel
  - ViewModels: MainWindowViewModel, ProjectViewModel, SurfaceEditorViewModel, PreviewViewModel, SettingsViewModel
  - Views: MainWindow.xaml, MeshEditorControl.xaml, LayerListControl.xaml, PropertiesPanel.xaml, PreviewControl.xaml
- Configuration & secrets:
  - appsettings.json for configurable values (FFmpeg path, GPU device ID, decoder thread count, default window layout).
  - Environment-override supported (for CI/automation).
- Logging:
  - Microsoft.Extensions.Logging with an app-local rotating file logger + console.
  - Provide a diagnostics view in-app (log viewer) for users to copy/paste logs.

Functional UI mapping (v1 -> .NET)
- Menus: File, Edit, View, Project, Tools, Help — implement using WPF Menu + Command bindings. Keep keyboard accelerators consistent with v1.
- Toolbars: Main quick tool buttons (Import, New Surface, Fit, Snap toggles, Preview) — implement as Ribbon or ToolBar.
- Docking panes: Project/Layer list (left), Properties inspector (right), Central canvas (mesh editor), Preview window (floatable/dockable). Use a docking library (AvalonDock) for WPF to reproduce dockable panels and persistence of layout.
- Mesh editor (central canvas):
  - Support selection / dragging of mesh control points, multiple selection, snapping to grid, rotate/scale transform, numeric input in properties panel.
  - Implement interactive handles with hit-testing and keyboard modifiers (Shift, Ctrl).
  - Implement mouse wheel zoom, middle-button pan.
- Layer list & properties:
  - Layer ordering, visibility toggles, opacity slider.
  - Per-layer properties: transform, blend mode, projection settings (target device / screen), keyframe/timeline basic controls if v1 had them.
- Preview:
  - Real-time preview using GPU-accelerated rendering; show outlines, wireframes, and final composited image.
  - Real preview window that can be sent full-screen to an output display (multi-monitor support).
- Drag & Drop:
  - Support drag/drop of media files into project window and OS-level file-open dialogs.
- Shortcuts:
  - Maintain most common MapMap shortcuts (Ctrl+O, Ctrl+S, Ctrl+Z, etc).

Compatibility & data
- Project file compatibility:
  - Implement a converter module (MapMapCompat) that can import v1 project files (if v1 uses JSON/XML or a known format). If format is binary/custom, implement a best-effort parser or provide an import path for assets and basic mesh/layer properties.
  - Save format: use a modern JSON or XML schema with versioning and backward compatibility.
- Resource management:
  - Central ResourceManager that tracks loaded files, reference counting, caching decoded thumbnails.
  - Use file watchers for missing external resources and graceful re-linking.

FFmpeg integration & video pipeline
- Decoding:
  - FFmpeg.AutoGen wrapper to achieve fine-grained control. Open media in a background worker per media resource; decode frames into system memory or directly to GPU (if possible) with hw-accel (DXVA2 / D3D11VA) when available.
  - Provide configurable fallback to CPU decode when hardware acceleration unavailable.
- Frame pipeline:
  - Decoder thread(s) produce frames into a bounded ConcurrentQueue/Channel. A single Render thread consumes frames, uploads to GPU textures and presents.
  - Use proper timestamps, frame queuing with jitter compensation, and a clock/timestamp system to maintain AV sync for playback (if timeline features present).
- Hardware acceleration:
  - Use DXVA/D3D11 VA via ffmpeg API if feasible, or use system-supported Media Foundation interop for Windows if FFmpeg hwaccel is complex. Provide configuration for preferred path: "Auto", "FFmpeg HW accel", "CPU".
- Native binaries:
  - Provide a Native/ffmpeg/ folder or instruction to install FFmpeg in PATH. Do not hardcode paths — use appsettings.json and registry/environment overrides.

Rendering details
- Use Direct3D 11 as the primary renderer for stability and broad compatibility.
- Implement a renderer abstraction layer:
  - IRenderer interface with backends: D3D11Renderer (primary), SoftwareRenderer (fallback).
  - Renderer responsibilities: composite layers, render wireframes, apply transforms and masks, upload video textures, render to preview window and projector output.
- Coordinate space:
  - Central canvas should be resolution-agnostic, with a scene graph that maps normalized coordinates to projector coordinates.

Accessibility, UX, and polish
- Provide contrast-aware themes (light/dark) and scalable fonts.
- Persist UI layout and last-used project.
- Provide meaningful error messages, graceful fallbacks, and log collection options.

Security and stability
- Validate untrusted media filenames and paths; avoid executing shell commands.
- Sandbox native interop when possible and check return codes of native APIs.
- Wrap all native calls with try/catch and ensure leaking resources are disposed via SafeHandle or using-blocks.
- Avoid UI thread blocking; use ConfigureAwait(false) where appropriate on background tasks.

Testing approach
- Unit tests:
  - Model serialization, MapMap compatibility parser, configuration parsing.
- Integration tests:
  - FFmpeg decoder unit test using a small test file.
  - Rendering smoke test that initializes renderer and uploads a test image.
- UI tests:
  - Basic smoke tests for window creation and dock layout (WinAppDriver or Playwright for desktop).
- CI:
  - Add builds to run unit tests and basic integration tests on Windows runners; artifact storage for debug logs.

Files to add/modify (high-level)
- Add folders:
  - /Native/FFmpeg/ (binaries and native interop helpers, excluded from source if large; provide downloader script)
  - /Src/Services/FFmpegDecoder.cs
  - /Src/Rendering/D3D11Renderer.cs
  - /Src/Rendering/RendererAbstractions.cs
  - /Src/ViewModels/... (MainWindowViewModel.cs, ProjectViewModel.cs, SurfaceEditorViewModel.cs)
  - /Src/Views/Controls/MeshEditorControl.xaml (+ code-behind)
  - /Src/Models/ProjectModel.cs, SurfaceModel.cs, LayerModel.cs, MeshModel.cs
  - /Src/Utilities/MapMapProjectImporter.cs
  - /Src/Config/appsettings.json
  - /Src/Logging/LoggingSetup.cs
  - /Tests/Unit/ (models, importer)
  - /Docs/ (migration notes & known differences)
- Modify:
  - MainWindow.xaml / MainWindow.xaml.cs — implement the dock layout and host for mesh editor / preview.
  - ProjectionMapper.csproj — add package references (FFmpeg.AutoGen, Vortice.Windows, Microsoft.Extensions.*).
  - App.xaml — register services for DI (HostBuilder pattern) and configure logging.

Configuration & packaging
- appsettings.json (keep defaults minimal, allow user override); example keys:
  - "FFmpeg:Path"
  - "Renderer:Backend" ("D3D11")
  - "Renderer:DeviceIndex"
  - "Decoder:ThreadCount"
  - "Logging:LogFolder"
- Provide a script to fetch FFmpeg builds for Windows into Native/ffmpeg (or instruct the user to put ffmpeg.dll in PATH).
- Produce a single MSIX/installer that includes native DLLs or an installer script that validates FFmpeg presence.

Risk assessment & mitigation
- Risk: Recreating advanced Qt behaviors (dockables, mesh editor ergonomics) may be time-consuming.
  - Mitigation: Use an existing WPF docking library (AvalonDock) and implement MeshEditor as a single reusable custom control; iterate quickly with user testing.
- Risk: FFmpeg hwaccel on Windows can be complex/fragile.
  - Mitigation: Provide robust CPU fallback, and document hwaccel as opt-in with detection logic.
- Risk: GPU interop synchronization issues (race conditions).
  - Mitigation: Constrain all GPU texture creation and presentation to a dedicated renderer thread; use thread-safe queues; ensure fence/wait semantics.

Backward compatibility & migration notes
- If v1 uses custom project format: attempt to import geometry, layer order, transform matrices and asset paths. Where exact parity is not possible, import assets and approximate transforms; provide an import log and manual editor to correct mismatches.
- Preserve keyboard shortcuts and user workflows where possible; create a "MapMap compatibility" mode that toggles behaviors to match v1 more closely.

Deliverables in this plan (what this document describes)
- A complete work breakdown to implement parity between ProjectionMapper-v1 and the new .NET 9 app.
- Concrete file-level plan (see "Files to add/modify" above).
- Technical choices and rationales for rendering and FFmpeg integration.
- Test plan, configuration approach, and risk mitigation steps.

Notes and assumptions
- Assumed WPF on Windows as primary desktop UI toolkit because the existing .NET project already contains MainWindow.xaml. If you prefer WinUI 3 (for better composition integration), the approach is similar but porting of XAML and some hosting details will differ; the renderer layer abstraction will help with either choice.
- Exact details of all Qt widgets from v1 (names and behaviors) were not enumerated file-by-file in this plan; the plan focuses on the canonical UI surfaces and behaviors that MapMap-like apps provide. A follow-up pass that inspects src/ in ProjectionMapper-v1 will produce a precise control mapping (menu entries, exact property sets, and keyboard shortcut lists).
- Native binary packaging and FFmpeg licensing/copyright should be handled and documented.

If you want, I will produce the initial code skeletons (ViewModels, renderer abstraction interfaces, FFmpeg service scaffolding, and updated MainWindow.xaml layout) and a concrete package.json/appsettings.json plus CI job changes next. This will be implemented so the solution compiles on .NET 9, and tests run (unit tests for models/importer). The created code will follow MVVM, proper async/background threading, safe native interop patterns, and a clear separation of responsibilities for easy iterative improvement.