# Multi-device / resource architecture V2: audit and migration plan

## Audit (what is there today)

- **Rig** (`Sidera.Core.Rigs.Rig`) is already an optical train: camera + optional focuser, filter wheel, rotator, optics, and an optional mount and guider. Sharing is already inferred: two rigs share a
  mount or guider when they name the same `DeviceId`. Persisted as `rigs` in `equipment.json` (`RigConfiguration`), registered in `RigRegistry`. Sessions refer to rigs by `RigId`.
- **Resources**: `ResourceManager` hands out exclusive `ResourceId`s (`device:<id>`) all-or-nothing, first come first served. There is no shared mode and no notion of mount stability. Actions declare
  `RequiredResources` (exclusive) or, for service-backed steps, `ServiceResources`.
- **Coordination**: `SafePointCoordinator` + coordination groups. `SequenceDraftBuilder.Orchestrate` builds one group per **mount** (tracks whose rig sits on it), a dither domain per (mount, guider),
  and a `MeridianFlipGroup` per mount. This is "rig on a mount" logic, not claim logic.
- **Autofocus** is local to a track and, by design and by existing tests, does **not** block other tracks' exposures (`WhileMainFocuses_WideKeepsExposing`).
- **UX**: the Equipment page is rig-centric: contexts `Standalone` + one per rig; a device-kind page has a chooser only in the standalone context. A camera with optics gets a hidden `rig.optics.*` rig.
  The workflow needs a rig for every imaging block, so a one-camera user without a rig cannot use the workflow.
- **Defaults**: `SequenceDraftDefaults.From` silently picks the first device by id when several exist.
- `MultiRigResourceMatrix` does not exist; there is no pure model of "who conflicts with whom".

## Decisions

1. **Imaging Setup = the persisted rig.** Same data, same ids, same file format (`equipment.json` stays version 1; `.astraseq` stays version 8). The user-facing word becomes "Imaging Setup"; the
   internal types keep their names for now. No data is rewritten, so there is nothing to lose; old files are read exactly as before (tested).
2. **Implicit setup**: a session with exactly one camera and no imaging setup uses an *implicit* setup built from the single device of each kind. It is computed, never persisted, never listed as a
   user object. Several cameras and no setup: nothing is guessed; the validation says to create imaging setups or to name the device.
3. **Resource claims** (`ClaimMode.Shared|Exclusive`, `ResourceClaim`): the manager supports shared and exclusive claims with FIFO fairness (a waiting exclusive claim holds back later shared ones).
   A new resource `mountstability:<mount>` expresses "the mount must not move/vibrate".
   - Exposure: camera exclusive, mount stability **shared**.
   - Slew, centering, rotation that moves the mount, meridian flip, dither: mount / guider exclusive and mount stability **exclusive**.
   - Autofocus: camera + focuser exclusive; mount stability is **opt-in** (default off, to keep the existing and tested behavior that one setup focuses while another exposes).
4. **`ResourceClaimMatrix`** (pure, in Core): from the claim profiles of the tracks it answers which tracks conflict with an operation. `Orchestrate` derives the coordination domains of dither, flip and
   claiming autofocus from it instead of hard-coded "rigs on the same mount"; MountGroup / GuiderGroup are the connected sets of tracks by their mount / guider `DeviceId`.
5. **Device resolution** (`DeviceResolver`): explicit device on the instruction > imaging setup binding > the one unambiguous configured device > a validation error naming the candidates. Never first-by-id.
6. **Availability**: setups are *usable* when their camera is connected. With some connected, only those count; with none connected, all configured ones count (planning offline). Multi-device UI appears
   only with two or more usable setups.
7. **UI**: the Equipment page navigates by device kind (Cameras, Focusers, Filter Wheels, Rotators, Mounts, Guiders) with "+ Add" per kind and a list when there are several; Imaging Setups is an optional
   section. "Standalone" is gone. The workflow hides setup selectors, "Parallel imaging" and "Shared resources" unless two or more setups are usable.
8. **Meridian flip** stays session-level and is executed per MountGroup (unchanged semantics, now derived from the claim matrix). Target pointing inside a MountGroup must be one target: a validation error
   otherwise (V1 constraint).

## Imaging binding identity (stable across implicit and explicit setups)

A workflow does not refer to the *setup object* (`RigId`, what `equipment.json` stores) but to an **imaging path** (`ImagingBindingId`, `imaging:auto:<camera id>`). The id is derived from the camera and nothing
else: no name, no list position, no random part. So it is the same on every start, the same for the implicit setup of a single camera and for the explicit setup made for that camera later, and a rename of the
camera or of the setup changes nothing.

- **What holds a path**: blocks of the Imaging section, the autofocus policy of a setup, "Frames counted on" (dither), the pointing setup. The `.astraseq` format is unchanged (version 8, same properties, the
  value is just another string); `equipment.json` stays version 1. Loading never rewrites a file.
- **Resolution** (`ISetupSource.TryResolve`): a path means the setup of that camera, explicit or implicit; any other value is a reference by setup id as older documents wrote it (exact id, then the id the
  implicit setup had: `setup.implicit:<camera>` and the earlier constant `setup.implicit`, which means the setup of the one camera that can be meant). `WorkflowBindings.Canonical` turns every reference that resolves into
  the path of its setup when a workflow is loaded or compiled (not an edit; the document is not marked modified), so from then on bindings are compared as paths.
- **Never guessed**: a reference that does not resolve is reported ("The imaging setup of camera 'x' is not available: the camera was removed or replaced, or it is one of several cameras and has no imaging
  setup") and never bound to another camera. Several cameras with no setup stay ambiguous; no implicit setup is made for them.
- **"Auto"** (a block with no binding) names no path: with one setup to image with, a new block stays Auto and means that setup, whatever its camera is later. With several, new blocks name the path of the chosen
  setup. Editing a row never rewrites its binding; a row whose binding does not resolve shows its setup choice (even with one setup) so that choosing the setup is the repair.
- **Camera replaced in a setup** is another path: bindings and autofocus policy written for the old camera are not carried over to the new one (an Auto block simply follows the only setup; a named block is reported;
  a policy of the old camera is not applied to the new one and is dropped with the next edit). **Camera or setup deleted**: the same message.
- The only exception is a document that still names a setup by its *id* (written before paths existed): while that setup exists it resolves to it, whatever its camera is now.

## Order of work (each step keeps the whole suite green)

1. Core claims + manager shared/exclusive + actions declare claims + `ResourceClaimMatrix` (commit: coordinate sequencing through resource claims).
2. Resolver, implicit setup, usable setups, strict defaults, workflow/Advanced validation, conditional workflow UI (commit: multi-device execution tracks).
3. Equipment navigation by device kind, "+ Add", single-device simplification, wording, persistence tests (commit: replace rig-centric equipment model).
