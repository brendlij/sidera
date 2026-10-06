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

## Order of work (each step keeps the whole suite green)

1. Core claims + manager shared/exclusive + actions declare claims + `ResourceClaimMatrix` (commit: coordinate sequencing through resource claims).
2. Resolver, implicit setup, usable setups, strict defaults, workflow/Advanced validation, conditional workflow UI (commit: multi-device execution tracks).
3. Equipment navigation by device kind, "+ Add", single-device simplification, wording, persistence tests (commit: replace rig-centric equipment model).
