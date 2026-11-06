# ProjectionMapper development roadmap

Status: proposed for owner review; implementation has not been authorized by this document.
Date: November 6, 2026.

## Product goal

Make projection mapping approachable for small installations and events: photograph a scene, define and align projection surfaces, create coordinated artwork, save the complete installation, and run it reliably offline.

The first delivery target is a dependable two-projector show. AI assists setup and artwork creation; saved shows must never depend on an AI service during playback.

## Evidence and planning limits

This roadmap follows review of the README, project configuration, models, persistence service, rendering interfaces, main view model, and existing task notes. It is not a runtime audit or a hardware test report.

- The README reports audio-related playback problems and successful short two-projector tests.
- [Current task notes](Current.md) report content appearing on both projectors despite distinct assignments. Reproduce this before selecting a fix.
- [Mesh implementation notes](FIX-MESH-LAYERS.md) say quad warping was implemented. Validate its accuracy rather than treating it as missing.
- The reviewed Direct3D renderer is a placeholder; software rendering is the present baseline.
- Existing playlists, mesh editing, and project persistence should be extended, not replaced without evidence.

No delivery dates or cost estimates are promised. Establish those after baseline measurements and selection of AI providers and hardware targets.

## Priority map

### Five fixes

1. Reliable playback, audio synchronization, seeking, and looping.
2. Complete project persistence, recovery, and missing-media handling.
3. Strict projector output isolation and predictable display assignment.
4. Accurate, performant rendering, followed by measured GPU acceleration.
5. Clear service boundaries, actionable errors, and meaningful regression coverage.

Output isolation is scheduled first because the repository records a concrete failure.

### Five projection features

1. Calibration tools: patterns, markers, precise nudging, snapping, and locking.
2. Polygon masks and, subsequently, subdivided meshes for curved surfaces.
3. Performance mode: cues, transitions, next-cue preview, and blackout.
4. Portable installation packaging and rehearsal checks.
5. Per-output color correction and, subsequently, edge blending.

### Two AI features

1. Photo to map: editable surface proposals with an explicit calibration step.
2. Surface-aware image generation: project-owned artwork with previews and generation history.

## Milestone 0 — Establish a reproducible baseline

Goal: agree on what works, reproduce known defects, and measure the current application.

Work:
- Record the Windows version, .NET/native FFmpeg setup, display resolutions, DPI settings, and hardware used.
- Build the solution on Windows and document repeatable launch instructions.
- Reproduce the output-leak scenario from Current.md using two distinct sources and duplicate imports of one source.
- Exercise import, mesh editing, save/reopen, play/pause, seek, looping, and playlist transitions using real media.
- Record CPU, memory, frame timing, dropped frames, and audiovisual drift under representative load.
- Create a concise issue list separating reproduced failures from unverified risks.

Acceptance:
- A second developer can follow the setup instructions.
- Each confirmed defect has steps, expected/actual behavior, and relevant logs.
- A baseline report identifies agreed media formats, output count, and performance targets.

Dependency: none.

## Milestone 1 — Trustworthy output and playback

Goal: every projector shows only its assigned content, with stable playback.

Work:
- Trace source, mesh, monitor, and renderer routing; define how source assignments and mesh overrides interact.
- Fix cross-projector leakage, including after reassignment, deletion, and project reload.
- Resolve saved outputs using persistent display identity where available; require deliberate reassignment when identity is ambiguous.
- Handle disconnect/reconnect, fullscreen placement, resolution, and Windows DPI changes.
- Establish one playback timing authority, bounded queues, cancellation, and deterministic resource cleanup.
- Repair audio synchronization and verify pause, resume, seek, restart, looping, and playlist group transitions.

Acceptance:
- Two projectors show distinct assigned content, including the duplicate-import reproduction.
- Disconnecting an output does not move its content onto an unintended display or crash the application.
- A four-hour representative two-output run completes without crash, unbounded memory growth, or increasing synchronization drift.
- Provisional audiovisual drift target: within 80 ms after settling; confirm or revise against the Milestone 0 hardware report.
- Automated coverage checks routing and playback state transitions; real hardware validates actual output and timing.

Dependency: Milestone 0.

## Milestone 2 — Durable projects and maintainable foundations

Goal: preserve an installation completely and make subsequent changes safer.

Work:
- Audit serialization of meshes, input/output points, assignments, playlist groups, and all editable settings.
- Add atomic saves, recoverable backups/autosaves, schema migration, relative media paths, and missing-media relinking.
- Preserve the last valid project if saving fails; report the failure clearly.
- Separate playback timing, decoding, composition, output management, and editing state incrementally.
- Add structured diagnostic logs and user-facing errors; keep credentials and sensitive provider payloads out of logs.
- Add focused persistence, migration, recovery, and playback regression tests.

Acceptance:
- Save/reopen reproduces the same mapping, artwork, playlists, and output assignments.
- Interrupted or failed saves leave a recoverable valid project.
- Missing media can be relinked without rebuilding mapping.
- Existing supported projects continue to load, or receive an actionable compatibility explanation.

Dependency: Milestone 0; coordinate shared playback work with Milestone 1.

## Milestone 3 — Calibration and rendering quality

Goal: align surfaces precisely and establish a dependable rendering contract.

Work:
- Add numbered output patterns, grids, corner markers, keyboard nudging, numeric coordinates, snapping, and mesh locking.
- Make source cropping and destination warping visibly distinct.
- Validate perspective warping, sampling, layer order, opacity, and degenerate/out-of-bounds geometry.
- Profile copying, allocation, decoding, and composition before choosing optimizations.
- Implement GPU composition after the correct software path provides a comparison baseline; preserve a usable fallback.

Acceptance:
- A user can identify each projector, align a quad, lock it, and recover identical alignment after reload.
- Calibration images retain expected geometry and transparency through nonrectangular mappings.
- Software and GPU paths agree within documented visual tolerances.
- Meet the frame-time and memory budgets agreed in Milestone 0 at the selected output count and resolution.

Dependency: Milestones 1–2.

## Milestone 4 — Photo to map, assisted first version

Goal: turn a reference photograph into editable projection surface proposals.

Work:
- Import a scene photograph with orientation and resolution handling.
- Let users mark or describe target objects; request named polygon/mask proposals from a vision provider.
- Validate returned geometry and handle uncertain, missing, or overlapping detections.
- Offer manual polygon creation, correction, rename, split, delete, and undo.
- Keep photo, source, and projector coordinate systems explicit.
- Align proposals manually using calibration tools; do not imply that a single arbitrary photo establishes projector alignment.
- Save the reference photo, accepted geometry, calibration, and necessary provenance.

Acceptance:
- A real scene photo produces useful editable proposals, or a clear recoverable failure.
- Users can complete mapping manually when AI is unavailable.
- Accepted shapes can be aligned on the physical scene and survive save/reopen.
- The interface explains what image will be sent externally before submission.

Dependency: Milestones 2–3 and owner selection of a vision provider.

## Milestone 5 — Generate and retain projection artwork

Goal: create artwork for selected mapped surfaces and keep usable results inside the project.

Work:
- Define an asynchronous provider interface with a first image-generation implementation.
- Support prompts, reference images, palette/style guidance, single-surface generation, and coordinated compositions.
- Build layout guides in unwarped content coordinates; apply projector distortion only in the renderer.
- Preview generated variants through the actual masks and mapping before acceptance.
- Store accepted image files, thumbnails, prompts, provider/model identifiers, generation settings, and surface assignments.
- Show provider-supported cost estimates or clearly labeled pricing uncertainty before submission.
- Support timeout, safe retries, and cancellation where available; account for requests that may still incur charges after cancellation.
- Store API keys in Windows credential protection, outside projects and logs. Never silently retry a potentially billable request.

Acceptance:
- Select surface → generate → compare → preview → accept → save/reopen works end to end.
- Generation does not block editing or interrupt playback.
- Failed requests leave the project usable and do not create broken asset references.
- A copied project plays all accepted artwork offline without credentials or provider access.

Dependency: Milestone 2 and the coordinate/rendering contract from Milestone 3. Photo to map is optional: manually created mappings must also work.

## Milestone 6 — Artist controls and live operation

Goal: shape light accurately and operate an installation confidently.

Deliver independently:
- Polygon masks with feathering; then subdivided meshes with editable control points.
- Named cues extending playlist groups, manual Go, timed advancement, fades, and next-cue preview.
- Performance mode with editing lock and immediate blackout across outputs.
- Per-output brightness, gamma, and RGB correction; then adjustable overlap edge blending.

Acceptance:
- Masks prevent visible spill onto excluded areas, with predictable feathering.
- Cue transitions preserve mapping and output routing.
- Blackout works during playback and generation; restoring output returns to a defined state.
- Color settings persist per output; overlap blending is verified on a physical two-projector setup.

Dependency: Milestones 1–3; polygon mask geometry should be shared with Milestone 4.

## Milestone 7 — Portable installation and release readiness

Goal: transfer, rehearse, and run a complete show on another supported Windows machine.

Work:
- Add Collect Project to bundle media, generated assets, mapping, and settings using portable paths.
- Check missing assets, storage, output availability, decoder dependencies, and project compatibility.
- Provide a rehearsal report and require output reassignment when the destination hardware differs.
- Document supported installation, offline playback, backup, and recovery procedures.
- Run the full workflow on real displays/projectors with real media and generated artwork.

Acceptance:
- A collected project opens on a second machine without references to the original media folders.
- Missing outputs/dependencies are reported before performance.
- A four-hour offline rehearsal passes with cues, masks, generated images, and saved calibration intact.

Dependency: Milestones 1–3 and whichever creative features are selected for release.

## Later work: automated calibration

Goal: reduce manual alignment after assisted photo mapping is proven useful.

Project known markers, photograph them, detect correspondences, estimate a transform, and let users verify or correct it. A planar homography is suitable for a flat surface; distinct depths require separate surface calibration or a more advanced geometric method.

Acceptance: measure alignment error on real installations against an owner-agreed pixel tolerance; reject unreliable calibration instead of silently applying it.

Dependency: Milestones 3–4. Full 3D reconstruction is outside the first release.

## Delivery and owner decision gates

Each selected milestone should produce a focused reviewable change, updated documentation, real verification evidence, and any migration/recovery notes. Build and test code changes on Windows; hardware claims require hardware evidence.

Recommended sequence:
1. Baseline, output isolation, playback, and project safety.
2. Calibration and rendering correctness.
3. Static artwork generation and assisted photo mapping, selectable independently.
4. Artist controls, portable packaging, and advanced calibration.

Before implementation, the owner selects:
- Which milestone or smaller deliverable to start.
- Target hardware, resolutions, media formats, and Windows support range.
- AI providers, account/billing approach, budget limits, and reference-image privacy expectations.
- Whether GPU acceleration, advanced meshes, or blending belongs in the first release.

Cross-platform support, generated video, full 3D reconstruction, and MIDI/OSC/live-video integrations remain later options. This roadmap creates no commitment to implement or purchase any service.
