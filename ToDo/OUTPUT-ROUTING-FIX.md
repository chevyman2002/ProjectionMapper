# Output routing isolation fix

## Cause and change

The routing manager submits new frames to the selected projector, but previously
never removed a retained frame from the old projector when an assignment changed.
A mesh could therefore remain visible on multiple outputs. The monitor dropdown
also refreshed overlays without resubmitting cached video while paused.

- Each routed submission clears that layer from all non-target monitor renderers.
- None (-1) continues to show in the combined preview only.
- Null frames remove software-renderer entries rather than retaining empty records.
- Changing the monitor resubmits the cached frame after reconciling output windows.
- Monitor renderer and size registries use concurrent dictionaries because UI output
  setup and background frame submission access them concurrently. Routed submissions
  are serialized so clearing and submitting do not interleave with another routed submission.

This addresses a code-level cause of the reported duplication; physical reproduction
and verification remain necessary. It does not claim to complete the playback,
audio, persistent display identity, or project recovery milestones.

## Regression checks

On Windows with .NET 9:

```powershell
dotnet build ProjectionMapper.csproj --configuration Release
dotnet run --project tests/OutputRouting.Regression/OutputRouting.Regression.csproj --configuration Release
```

The executable checks the real manager with recording renderer collaborators:
duplicate source-frame isolation, combined preview, reassignment, None, unavailable
targets, null-frame clearing, and software-renderer entry removal. These checks
validate routing decisions, not physical display output or FFmpeg playback.
The Windows desktop CI workflow runs them. The other .NET workflow is updated
from Ubuntu/.NET 8 to Windows/.NET 9 to match this Windows desktop application.

## Hardware acceptance checklist (pending)

1. Connect two projector outputs and import the same real video twice.
2. Assign mesh 1 to output A and meshes 2/3 to output B; verify isolation.
3. Pause and reassign one mesh A → B → None; ensure the previous output clears
   while unrelated meshes and the combined preview remain correct.
4. Repeat while playing, with different display resolutions.
5. Save/reopen and repeat; disconnect/reconnect outputs and record any failures.

No hardware or audiovisual synchronization verification has been performed in
the Linux editing environment.
