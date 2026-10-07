# Sequencer / setup UX redesign: inspection and design

This document records what was found before the redesign started, and the decisions that follow from it. The runtime (resource claims, safe points, multi-device execution, meridian flip, autofocus, dither,
plate solving, conditions) is kept as it is; the redesign changes the model the user edits, how it is saved, and how it is shown.

## 1. What is there today

**Session page.** Two editors over one `SequenceDraft`: *Workflow* (`WorkflowEditorViewModel`, 1.9k lines, `WorkflowEditorView`: target panel, a table of imaging rows, dither panel, target-stop panel,
meridian-flip panel, an inspector for the selected row) and *Advanced* (`SequenceDraftViewModel` + `WorkflowView` + `StepInspectorView`: the explicit tree of steps). The workflow is the source of truth in
Workflow mode; its compiled steps (the draft) are derived and shown read-only in Advanced; "Convert to Advanced" is one-way.

**Models.** `WorkflowDefinition` = one target, Prepare steps, Imaging *blocks* (setup, filter slot, exposure, frames, start/stop conditions), Finish steps, one dither policy ("frames counted on" one setup),
autofocus policies *per setup*, one meridian-flip setting, target-stop conditions. `WorkflowCompiler` turns it into draft steps: Prepare steps, one `MultiRigStepDraft` (a `RigTrackDraft` per setup holding, per
block, a filter change and a `Repeat` of exposures; the dither policy; the flip policy; the target stop), Finish steps. Draft steps are built into runtime steps by `SequenceDraftBuilder` (2k lines).

**Persistence.** `.astraseq` v8: `steps` (the compiled draft, written for older readers) and an optional `workflow` object (`WorkflowJson`). A reader compiles the workflow again and does not trust `steps`.
Document types in `Documents/`; `SequenceDocumentViewModel` loads/saves through `IWorkflowSource`.

**Runtime facts that shape the redesign.**
- A Rig Track may hold: exposures, delays, focuser moves, filter changes, autofocus, wait-until, and repeats of leaf steps. A dither step *inside* a track is refused ("not available inside Multi-Rig
  Imaging yet"); dither is a *policy* of the Multi-Rig block with **one** trigger setup that counts frames. Autofocus policy (start / interval / after filter change) is per track.
- Orchestration (safe points, dither, meridian flip) is derived per mount from `ResourceClaimMatrix`; autofocus holds the mount only when `HoldMountStable` is on.
- Multi-Rig blocks live at the top level of a sequence; several in a row are fine (targets run one after another).
- Camera cooling (`CameraControl`), mount park / tracking (`MountControl`) exist as device control but not as sequence steps.

**Context.** `ImagingSetupCatalog` resolves setups (implicit setup for one camera, `ImagingBindingId` paths, legacy ids). `Framing`, `PlateSolve`, `ManualAutofocus` and `Imaging` each keep their own
`Rigs` / `SelectedRig` over `RigRegistry.GetAll()`: **only explicit setups**, so a one-camera user without a setup gets "Select a rig" there. The Equipment page is organised by kind with an optional
"Imaging Setups" section. Wording still says "Rig" in several normal views.

## 2. Problems

1. The Session page is a settings screen: target, table, dither, target-stop and flip panels and an inspector are all on screen at once; the order of execution is not readable.
2. Device/setup choices are asked per row/step (Rig pickers on every Advanced step, "Setup", "Frames counted on", "Pointing setup").
3. No place for several targets, no shared preparation per target, no block-local automation (autofocus/dither are per setup/session), no actions inside a block beyond filter + exposure.
4. Pages disagree about which setup is "current".
5. Wording: Rig, Track, Binding ids leak into normal UI.

## 3. Decisions

### 3.1 One setup-centric domain model (no Avalonia, no devices)

`Sidera.Desktop/Sessions/`: plain records, the single source of truth for a structured session.

```
SessionDefinition  { Automation, Start[], Targets[], End[] }
SessionAutomation  { Flip settings | use application defaults }
SessionTarget      { Id, Name, RA, Dec, Rotation, Enabled, SharedPreparation[], Lanes[], Limits[] }
SetupLane          { Id, Setup (ImagingBindingId?, null = the only setup), Blocks[] }
SequenceBlock      { Id, Name, Enabled, Actions[], Repeat, Automation, Limits[] }
Repeat             { Count? , Until[] }          // 40 times | for 4 h | until 03:30 | while altitude>30 | any of
BlockAutomation    { DitherEvery?, Autofocus { AtStart, EveryMinutes, AfterFilterChange } }
Action             typed: Exposure, SetFilter, Autofocus, MoveFocuser, Wait, WaitUntil, StartGuiding, StopGuiding,
                   Dither(Now), SlewAndCenter, Slew, PlateSolve, Center, CenterAndRotate, SyncMount,
                   CoolCamera, WarmCamera, Park, Unpark, SetTracking
```

Conditions are the existing Conditions V1 types. Lanes bind with `ImagingBindingId` (the stable identity fix stays valid). The model never mentions rigs, tracks or claims.

### 3.2 Compiler, not a second engine

`SessionCompiler` compiles the model to the existing draft steps (and from there the existing builder/runtime). Per target: shared preparation steps once per mount group, one `MultiRigStepDraft`
(one track per lane), the session's flip policy attached with the target's coordinates, the target's limits as target-stop. Mapping rules (deterministic, ids derived from model ids):

- **Block** → inside the lane's track: leading *setup* actions (Set Filter, Wait Until, Move Focuser, Autofocus) run **once per block**, the actions from the first Exposure on are the **repeated body**.
  The editor shows the point where repeating starts. Repeat count / rule and block limits become the `Repeat` count and its stop conditions.
- **Automation** → *autofocus*: "at block start" and "after filter change" become explicit autofocus steps in the track (exact); "every N minutes" becomes the lane's interval policy (shortest of the
  lane's blocks; a note says so). *Dither every N* becomes the target's dither policy counted on the first dithering lane (the runtime counts on one setup); the shared mount/guider coordination of all
  affected lanes is the existing one. Differing intervals are told in a note, never silently changed.
- **Resolution**: a lane without a binding means "the only usable setup"; with several usable setups it must be chosen (validation, with a "create imaging setup" hint). Devices of an action come from the
  lane's setup; no action stores a device unless an Advanced override is set.
- **Shared mount**: lanes that share a mount share the target coordinates by construction (they belong to one target); preparation is emitted once per mount group.

### 3.3 Persistence: `.astraseq` v9

`session` object (typed, versioned names; no UI state). v8 files: `workflow` is migrated to a `SessionDefinition` deterministically (Prepare → shared preparation, imaging blocks → one lane per setup in
order of first use, per-setup autofocus policies and the dither policy → block automation, finish → End, flip and target stop → session automation / target limits). v8 sessions without a workflow
(Advanced) stay tree documents. The compiled steps are still written for tree readers. Reading never rewrites a file.

### 3.4 Setup context

`ImagingSetupContext` (view models) over `ImagingSetupCatalog`: the usable setups (the implicit setup of one camera included), the current one (kept by the imaging path of its camera, so a setup made, renamed
or made again for the same camera keeps the choice), and what to say when none can image ("Multiple imaging paths are available. Create or choose an Imaging Setup."). The sidebar shows the setup's name
quietly with one setup and a selector with two or more. Imaging (capture and autofocus), Framing, Plate Solve, the Equipment page, the dashboard and new targets/blocks follow it; their own rig pickers are gone.
The Session lane tabs are separate: selecting a tab does **not** change the global context and the switcher does not move the lane that is shown (predictable, tested).

### 3.5 Session page

Top to bottom: Session automation (flip summary + Edit), **Start**, **Target** cards (Shared preparation, then the lane(s) with blocks, target limits), **End**. Collapsed blocks summarise
(`300 s × 40 · Dither 3 · AF 60 m · Dawn / Alt > 25°`). Selecting a block opens one drawer (Actions / Repeat / Automation / Limits, each with its own add button); nothing selected → no inspector. An
Action Library (searchable, categories, click-to-add, keyboard) adds actions. With one usable setup there are no tabs, no setup pickers and no shared-resource panel; with several, tabs appear and a
subtle summary says which resources are shared. The run shows what waits and why (existing `ExecutionOverview`, status bar).

### 3.6 Structured / Tree

One model, two views. *Structured* is the editor above. *Tree* is the existing explicit-step editor over the compiled steps: opening it shows the compiled tree; editing it converts the session to a tree
document (one-way, asked first, as today). Tree device pickers default to the inherited setup context and show an explicit override only on request.

### 3.7 Wording

"Rig" → Imaging Setup / Setup / Camera in normal UI; Track → lane/setup sequence; "Frames counted on", "Pointing setup", ids and groups are not shown. Diagnostics may.

## 4. Out of scope (stated limits)

Scripting (YAML/Lua/C#), adaptive scheduling, per-block dither intervals that differ on the same mount, simultaneous different targets on independent mounts, temperature/HFR autofocus triggers.

## 5. What was built, and where it deviates

- The editor is model-first: every edit is an immutable function of `SessionEdits` over the `SessionDefinition`; the editor compiles, replaces the steps of the draft and maps the problems of the compiled steps back
  to the block, lane or action they came from (`SessionCompilation.Origins`). Cards are rebuilt on each edit; the drawer is rebuilt only on structural edits, so typing does not lose the focus.
- `.astraseq` v9 writes `session` next to the compiled `steps`; v8 `workflow` files are migrated by `WorkflowMigration` when read and never rewritten by opening them. The old `WorkflowCompiler` lives in the tests as the
  reference of what a v8 workflow meant (`LegacyWorkflowCompiler`); `WorkflowMigrationTests` compares the migrated session's compile result with it.
- Tree: one-way from the session (asked first), back only where the steps are exactly what a session compiled to (compared by fingerprint). With one usable setup the Tree shows no setup picker (its steps start
  with the current setup); with several the picker stays where a step needs a setup: the explicit "override on request" button of the spec is not built. Wording of the Tree: Parallel Imaging, Setup Sequence.
- Dither is counted on the first lane that dithers (the runtime counts on one setup); lanes with different intervals or several dithering lanes get a note, never a silent change. Autofocus "every N minutes" is the clock
  of the lane (the shortest of its blocks). An autofocus trigger by N exposures, temperature or HFR is not supported.
- Target status shows Pending, Running, Completed, Skipped, Failed (no separate "Waiting"; what a block waits for is said on its card and in the status bar).
- Not built: scripting, adaptive scheduling, different simultaneous targets on independent mounts, per-block dither intervals on one mount, Safety and Recovery under Session Automation.
- The settings file keeps `defaultSessionMode` = `workflow` | `advanced`; the Settings page calls them Blocks and Tree.
