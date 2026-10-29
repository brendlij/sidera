# Sidera feature reference

> The long reference: every behaviour of Sidera in one page, as it was written while the application grew. The shorter topic pages are in [docs/README.md](README.md).
> **Sidera is in development and not reliable for imaging sessions**, see the [status](hardware-status.md).

## What it does today

- **Equipment:** the page starts with the current imaging setup: its camera, focuser, filter wheel and rotator with their state, its optics (focal length, aperture, pixel scale, field of view) and
  the mount and guider it shares with other setups. **Manage all devices** opens the management of every device by kind (cameras, focusers, filter wheels, rotators, mounts, guiders) and of the
  imaging setups: add, rename, remove, choose a driver (none, simulator, or an installed ASCOM driver), open its setup dialog (also while the device is disconnected), connect. A setup owns its camera,
  focuser, filter wheel and rotator; its mount and guider are optional, and two setups that name the same mount or guider share that one device. A disconnected device shows its name, its state and
  Connect, nothing stale. Camera settings apply as you change them.
- **Camera:** exposures with gain, offset, binning, subframe and readout mode where the camera supports them; stop and abort;
  cooling controls; frames shown on the Imaging page.
- **Mount:** slew, tracking, sync, park, hold-to-move pad, and a Stop that is always there. A large slew asks for confirmation.
- **Focuser:** absolute and relative moves, halt, temperature.
- **Dome and roll-off roof (ASCOM Alpaca):** Equipment → Domes. A dome is a shared device of the observatory, not part of an imaging setup. Choose *ASCOM Alpaca (network)*, enter the host, the port and the number of the
  device, or ask a host (one discovery message to that host only, then its management API) or the whole local network (one broadcast; only when you press that button) and choose the dome that answers. Sidera speaks
  Alpaca directly over HTTP (`src/Sidera.Alpaca`): no ASCOM Platform and no COM, and it contacts exactly the address you gave it. Creating the device and connecting it move nothing. The page shows only what the
  device supports: a roll-off roof (a shutter and no azimuth) has the shutter and a halt; a dome also turns, parks and finds home. Opening the shutter of real equipment asks every time; turning, parking and going
  home ask once per dome; closing the shutter and halting never ask. A shutter that was stopped halfway reports an error in most drivers: it can then be closed, not opened. While connected the state is read every
  two seconds (a roof that closes by its own rain sensor shows as closed), and a dome that stops answering is shown as faulted.
  In the sequencer the group *Dome* has Open Dome, Close Dome, Turn Dome (an azimuth), Park Dome and Dome Home, for the start and the end of the session and the preparation of a target. They work with the domes of
  the observatory, not with an imaging setup, and a roof refuses Turn, Park and Home when the step runs. A session that opens a real shutter asks before it runs, every time; a dome that is not connected stops the run from starting.
- **Rotator:** absolute and relative moves, halt, sync and reverse where the driver supports them (the page shows only what the rotator can do). A rotator belongs to
  an imaging setup, which is optional. Its position is the mechanical angle of the driver and is never taken for the rotation of the sky in the image: that is a calibration of the
  setup, measured by one plate solve and kept with it. Rotating to a sky angle, verifying it with a solve and centering while rotating are described below.
- **Guider:** PHD2 as the guiding backend (host and port are the settings of the device; its equipment, calibration and star stay in PHD2).
  Start, stop, pause; live guide graph in arcseconds, rolling RMS, star SNR, the settle after a dither, and what PHD2 reports about its setup.
- **Observing site:** one place for the whole application (Settings): latitude north and longitude east positive (or write N, S, E, W),
  elevation in meters. Unknown until you enter it. When a mount connects its site is compared with it; a real difference (more than 100 m)
  asks what to do (use the mount's location, send Sidera's to the mount, or keep both). Nothing is written to a mount by itself.
- **Optical train:** the focal length (and optionally aperture, pixel size, sensor pixels) of a camera's imaging setup, on the camera page. Pixel scale,
  sensor size and field of view are derived from it and from what the camera reports, never stored. Each value is shown with its source, resolved one value at a time:
  what you entered (Manual), else what the camera reports (Device), else the small built-in camera database for a camera it knows (Sidera Camera Database, a versioned
  JSON resource in `Sidera.Core`), else unknown. The database is never copied into the setup, a manual value always wins and can be reverted, and a difference between the
  camera and the database is shown as a warning, not replaced. The optics belong to the imaging setup, not to an action: Framing, Plate Solve and the pixel scale use them.
- **Framing:** search an object (M31, NGC 7000, IC 434), see the field of the current imaging setup on a sky survey (HiPS tiles from the CDS, cached on your disk),
  drag and turn the frame, then Slew & Center on it or add it to the session as a target (with a Slew & Center, or Center & Rotate where the setup has a rotator, as its preparation). With a rotator in the setup the page offers Center & Rotate instead, and a setup without one
  compares the rotation of a plate solve with the plan and says by how many degrees to change it (Solve Again after you turned the camera). The mount is never synchronized. The field of the current scope is drawn in red: the position of the mount, live, or the solved position and rotation while they are current; a rotation that is not known is drawn unrotated and dashed. No survey is bundled; the rights of each survey are shown with it.
- **Imaging:** manual capture with the camera of the current imaging setup through the same acquisition pipeline as the sequence (exposure, frame type, gain, offset, binning); nothing is chosen on the page:
  the setup is the one of the sidebar. With several cameras and no imaging setup nothing is guessed: the page says so and offers to create one. A viewer: wheel to zoom around the pointer, drag to pan, Fit, 1:1 and Auto Stretch (display only, the data is never changed). Save FITS writes the data as taken with its
  metadata; Save PNG writes what you see, and says so. A manual Autofocus runs the sequence's autofocus on the current setup and shows its samples, curve and result; a setup without a focuser says "No focuser configured" and offers to configure one.
- **Imaging setup (sidebar):** one imaging setup is current for the application. With one camera it is just there (its name is shown quietly); with several usable setups the sidebar has the selector. Imaging,
  Autofocus, Framing, Plate Solve, the equipment view and new targets of the session follow it; switching it changes what the pages work with and nothing else (the mount and the guider of the setups stay shared,
  and the lane that the session shows is not moved by it, nor does selecting a lane change it).
- **Session:** a session reads from top to bottom: **Session Automation** (the meridian flip), **Start**, one or more **Targets**, **End**. A target has its coordinates and rotation, a **Shared Preparation**
  (Slew & Center or Center & Rotate with the coordinates of the target, Autofocus, Start Guiding: done once, for every setup on the mount), limits of its own, and one sequence for each imaging setup: with one usable
  setup just "Imaging", with several a tab for each setup (`Main 750 · Wide 400`) that run side by side wherever their devices allow, with a quiet "Parallel imaging" and "Shared: mount · guider". A sequence is made of **blocks**.
  A block has **Actions** (Set Filter, Exposure, Autofocus, Move Focuser, Dither Now, Wait, Wait Until, and for the start, the end and the preparation Cool and Warm Camera, Park, Unpark, Set Tracking, Slew, Plate Solve,
  Sync Mount and more; they run in order, those before the first exposure once, from it on repeated), **Repeat** (a number of times, until something holds, or both), **Automation** (dither every N exposures;
  autofocus at the start of the block, every N minutes and after a filter change, done at safe points and never as steps to place again and again) and **Limits** (stop at dawn, below an altitude, at a time, after a duration; an
  exposure that runs is finished first). A closed block is one line: `300 s × 40 · Dither 3 · AF 60 m · stops: dawn, alt<25°`. Selecting something opens it in a drawer; nothing detailed is on the page all the time.
  Blocks, actions and targets can be added from a library with a search, moved, duplicated, switched off and removed. While a session runs every block shows its frame and what it waits for. Devices come from the imaging setup of
  the sequence: no action asks for a camera, focuser, mount or guider again (Advanced actions can say "use setup"). Dither and autofocus are coordinated with the setups that share the mount or the guider (see below).
  A session is saved in the `.astraseq` (document version 9) as typed structures (`session`: start, targets with their preparation, sequences, blocks, and end), next to the steps it compiles to.
  **Tree** shows the explicit steps the session compiles to (setup sequences, Parallel Imaging, Repeat and every action) and asks first: the session is not kept, and going back is offered only where it is exact.
- **Meridian flip:** Session Automation, set once for the session (Edit next to it), or following the application defaults of Settings → Meridian Flip; not steps and not per block. Times are minutes from the moment the target is on the meridian, from its hour angle
  (target, observing site, clock), not from the pier side the driver reports. From *hold new exposures before* an exposure only starts when it ends before the flip is due; before that only
  when it ends before the *latest allowed flip*. An exposure that runs is never interrupted. When the flip is due (*flip after*) and every setup on the mount is at a safe point, the mount
  flips once for all of them, in this order: stop guiding, slew to the same target (the driver chooses the pier side), check the slew, plate solve and center, verify the rotation,
  autofocus, restart guiding and wait until it settles, dither, pause. Every step after the flip is switchable; the settle is the guider's own, never a fixed delay, and the pause is
  additional. The mount is never synchronized. The position of the target is brought from J2000 to the date before the hour angle is worked out (about 80 s for a target of today). The flip is only done when the mount changes sides of the pier (a driver that reports the same side before and after did not flip: that is a failure with its reason); a flip that failed before the mount changed sides stops its tracking and says so, a mount that did change sides keeps tracking, and a retry starts tracking again and goes on from the corrected position of the last centering. Centering lets the mount settle after a slew (3 s), tries a solve that found nothing twice more, and starts tracking when the mount says it does not track. A failed flip is also an error message with a button to the session. Setups on other mounts keep imaging and flip on their own. A failed flip (after its attempts) holds the setups of that mount until you retry
  or abort, or ends the session, as chosen. A target that is already past the meridian when imaging starts needs no flip. The status shows the countdown and, during the flip, what it
  is doing. A real flip is only tested by hand (see `MeridianFlipHardwareTests` and `SIDERA_MERIDIAN_FLIP_TARGET`).
- **Sequences:** repeat, group and parallel steps, safe points, pause and resume, dithering with guider coordination, autofocus
  with policies, plate solving, and multiple imaging setups in one sequence. Sequences are saved as `.astraseq` files. Steps of a setup choose an imaging setup, not a camera and a focuser; each step
  uses the mount and guider of its setup, and a new step of the tree starts with the current setup (the choice is shown only where there is one). With more than one setup the tree can be seen as Overview, one tab per setup, or Shared: the same steps, filtered. Setups on different
  mounts work at the same time; setups on one mount or one guider take turns for it (a plate solve or a rotation holds only the devices it uses).
- **Conditions, Repeat, Limits and Wait:** a session can wait for and stop at the sky and the clock. *Wait Until* (all must hold; an action, e.g. before a block): target altitude above N°, astronomical
  (or civil, nautical) darkness, a time, a duration. *Repeat until* and *Limits* (any; a block, or the whole target): the block's frames, target altitude below N°, dawn, a time of day, a duration. Altitudes are computed from the target's coordinates, the observing site (Settings, else the mount's) and
  UTC, never read from a mount; the Sun uses the low-precision solar formula of the Astronomical Almanac (about 0.01°), twilight is the Sun's centre at -6°, -12° and -18°. Dusk means darkness
  (the Sun at or below the altitude); dawn means the Sun coming up through it, the next time after the block starts, never "the Sun is above -18°". Where it never gets that dark within 48 h
  there is no event: a Wait says so and stops, a stop at dawn simply does not trigger. A time of day is the next time the clock reads it in the named zone (the computer's, shown in the UI), and
  is turned into a UTC instant once. A condition that is met stays met (no start/stop flicker around a threshold). A duration counts the wall-clock time of the block from when it began imaging
  (pauses count; the time before it started and the wait for its start conditions do not). A stop never cuts an exposure: the running exposure finishes, then the block ends, and the end of the session follows
  as after a normal end. When several things are due at once the order is: stop, then meridian flip, then autofocus, then dither, then the next exposure; so no flip, autofocus or dither starts
  for imaging that is over. A block that reaches its frames does not stop other setups; the limit of the target ends all of them. Not yet: weather, Moon, adaptive scheduling, nested AND/OR.
- **Devices, imaging setups, resource claims:** the management of the equipment is organised by kind (Cameras, Focusers, Filter Wheels, Rotators, Mounts, Guiders); each kind holds any number of named devices, added
  on its own page ("+ Add Camera"), and one device of a kind is simply the device. An *imaging setup* is optional: a camera with its focuser, filter wheel, rotator, optics and, if you like, its mount and guider.
  With one camera and no setup, Sidera uses an implicit setup made of the only device of each kind (never stored, never listed as something you made); with several cameras and no setup nothing is guessed: the pages say
  "Multiple imaging paths are available. Create or choose an Imaging Setup." and offer to create one. Which device an action uses is decided in this order: the device it names (Advanced), the setup of its sequence, the current setup, the one device that can be meant (the only connected one, else the only configured one), else an
  error naming the candidates. A setup is *usable* when its camera is connected (when none is, all configured ones count, so a session can be planned offline); the setup selector, lane tabs and "Parallel imaging" appear only with two or more usable setups. Sharing is inferred from the device ids: setups on one mount share it, setups on one guider share it. What may run at the same time follows
  from the claims of the actions: an exposure holds its camera and shares the stability of its mount, so cameras on one mount expose together; a slew, centering, dither or flip holds the stability of the mount
  exclusively and so waits for the exposures that run and keeps new ones from starting; unrelated mounts, guiders and cameras never wait for each other. An autofocus holds only its camera and focuser unless
  Settings → Autofocus says to hold the mount still (the block says which of the two it is), in which case it waits for the other exposures on that mount. Meridian flips are per mount group; a dither asked for by a block runs once for
  every setup on that mount or guider. The setups of a target share its coordinates: setups on one mount cannot be pointed at different targets at the same time. The equipment file format is unchanged (version 1): an imaging setup is the rig that was stored, same ids, same shared mounts and guiders.
- **Stable imaging bindings:** the sequence of a setup refers to an imaging *path* (derived from the camera), not to the setup object or its name. Making an explicit
  imaging setup for a camera that was used without one therefore changes nothing: sequences, blocks and their automation carry on with the new setup, with its optics at once. Renaming the camera or the setup changes
  nothing either. A replaced or removed camera is *not* followed silently: what referred to it is reported, a sequence without a path simply means the only setup, and nothing of the old camera is applied to the new one.
  Several cameras and no setup stay ambiguous. `.astraseq` version 9 names the path; version 8 files, which name setups by id, are read and migrated (see below).
- **Session views:** `[Blocks] [Tree]` next to the title. A session opens as blocks unless you choose otherwise (Settings → Sequencer) or open a file that is not a session (an older explicit sequence opens as the tree, as it was).
  Opening the tree asks first, because the blocks, automation and limits of the session are no longer editable afterwards. Back to Blocks is only offered where it is exact (an empty sequence, or the
  untouched steps of a session); otherwise it says "This sequence cannot be shown as blocks: its steps are not those of a session."
- **Version 8 files:** a `.astraseq` of version 8 with a workflow is migrated when it is opened: the target and its Prepare become a target with its shared preparation, the imaging blocks become one sequence for each setup
  with their blocks (a block's start conditions a leading Wait Until, its stop conditions its limits, the autofocus policy of a setup and the dither policy the automation of its blocks), the meridian flip the session automation and the target stop the limits of the target.
  The file is not changed or rewritten by opening it; the session is saved as version 9 when you save it. References to a setup that cannot be found are kept and reported, never given to another setup.
- **Dashboard:** the session at the top, the imaging setups with what each is doing, the mount (state and tracking rate, position, altitude and azimuth, side of the pier, sidereal time and UTC, the site it works
  with, and when its pointing crosses the meridian; with a target in the session also the time to the flip), the guide graph, and the latest image (stretched like the viewer of the Imaging page). Settings →
  Appearance switches each panel on or off.
- **Saved frames:** every frame that a session takes is written to disk as FITS (Settings → Imaging, on by default). The save folder is the one of the Imaging settings, or the Sidera folder in your Pictures. The folders
  and the name of the file come from patterns of parts in braces, by default `{target}/{night}/{type}` and `{target}_{type}_{filter}_{exposure}_{n}` (also `{setup}`, `{camera}`, `{date}`, `{time}`, `{gain}`, `{offset}`,
  `{temp}`, `{bin}`); `{night}` is the evening a night began, so frames after midnight stay together; a part that is not known is left out; the settings show an example of the path while you type. The header holds what is
  known (OBJECT, FILTER, EXPTIME, DATE-OBS, GAIN, OFFSET, binning, CCD-TEMP, IMAGETYP, INSTRUME, TELESCOP, FOCALLEN, pixel size, the pointing of the mount, the site) and nothing that is not. Frames are written in the
  background under the name `.part` until complete, a file that exists is never replaced, and a frame that cannot be written is told as an error message. Autofocus and plate-solve frames are not saved; JPG, a second copy and
  moving the frames after a session are not there yet.
- **Messages:** what happens is said in one place, at the top right of the window: a small banner that slides in over the page and never moves it (a Save in the settings, a device that connected or could not,
  a session that was saved or opened, a run that finished, was stopped or failed, a plate solve, an autofocus, a dome shutter that opened or closed). Success and info close after about four seconds, a warning
  after eight, an error stays until it is closed; at most three are shown, the others wait; the same message while it is shown counts up (×3) instead of stacking; pointing at one holds its clock. A message can
  have a button ("Open Diagnostics" after a failed run), a close button, and Escape closes the one that has the focus. Each severity has its own icon and word, so Red Night (one hue) is still readable. The last
  ten are listed on the Diagnostics page. A value that was typed wrongly is still told next to its field, and the status bar still says what Sidera is doing now.
- **Help:** the Help entry of the sidebar (above Settings) holds the manual of the application: a tree of topics (Start Here, Sequencing, Imaging, Astrometry, Mount & Guiding, Observatory, Troubleshooting, Reference)
  and a search over titles and text. Articles number what they show: a picture of the real page with numbered markers, and a list that says what each number is. An article can link to another article and
  open the page it is about ("Open Equipment"). Every page and every tab of Settings has a "?" that opens the article of what you are looking at; the troubleshooting articles say Problem, Likely reason,
  What to check and a button to the page where it is fixed. Help only describes what Sidera does today. The content is plain data in `Help/` (articles, contexts), the pictures are in `Assets/Help`,
  and `tools/help-screenshots` takes them again.
- **Themes:** Settings → Appearance: Sidera Dark (the default), Midnight, Graphite, Red Night (black and red only, for night vision at the telescope) and Light. A theme is shown as soon as you choose it; Save keeps it
  for the next start, Discard goes back to the saved one. Each theme is one palette file in `Styles/Themes`; views and styles take their colours from the tokens, so a theme changes nothing but colours.
- **Autofocus:** a search (Settings → Autofocus, on by default): it starts where the focuser stands, steps toward the side where the stars get smaller, turns round when they get larger, and takes samples on both
  sides of the smallest stars (as many on each side as the number of samples says, up to a most that the search or the setting decides). A position where no star can be measured ends the search on that side and is not
  fatal; a run of failed samples (3 by default) is. The curve is a hyperbola through the widest run of samples that it explains (a wild sample is left out) or the lines of the two sides; a minimum beyond the samples is
  stepped to; a focus that checks worse than the best sample falls back to that sample. The position where focus was found is remembered for each setup (a file beside the settings): with no star where the focuser
  stands, the search goes there, then a few steps to either side. The old fixed pattern of samples is still there (switch the search off).
- **Settings:** tabs for General, Appearance, Observatory, Imaging, Autofocus, Guiding, Plate solving, Framing, Meridian Flip, Sequencer and Advanced. Each editable tab is saved with its own Save
  button; nothing is applied while you type. Defaults are only used for what is created afterwards: a session with its own settings, or an open session, is not rewritten. A session
  chooses its meridian flip between "Use application defaults" and "Custom for this session". Settings are defaults; what a session does is in the session.
- **Hardware safety:** operations that move real equipment (Slew & Center, Center & Rotate, running a sequence with a real mount or rotator) ask once per device and run:
  "Ensure the equipment can move safely." Cancel moves nothing. The answer is kept only for the running application (Settings → Advanced forgets it). Simulators never ask.
- **Diagnostics:** structured logging to `%LOCALAPPDATA%\Sidera\logs`.

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- For real equipment: the [ASCOM Platform](https://ascom-standards.org/) 7 and the drivers of your devices

## Build and run

```bash
dotnet build
dotnet run --project src/Sidera.Desktop
dotnet test
```

The tests need no hardware: devices are simulators or fakes of the ASCOM drivers.

## Repository layout

| Project | Contents |
|---|---|
| `src/Sidera.Core` | Device abstractions, capability model, acquisition model |
| `src/Sidera.Runtime` | Runtime host, simulators, sequencing, autofocus |
| `src/Sidera.Alpaca` | ASCOM Alpaca over HTTP: the protocol client, discovery (unicast per host, broadcast only on request), `AlpacaDome` |
| `src/Sidera.Ascom` | ASCOM adapters: discovery, one STA thread per device, camera, mount, focuser, rotator, filter wheel |
| `src/Sidera.Sky` | Sky surveys (HiPS), their disk cache and the object catalogs of the framing workspace |
| `src/Sidera.Phd2` | PHD2 as a guider: the TCP event server protocol, `Phd2Guider`; nothing of the protocol leaves this project |
| `src/Sidera.Desktop` | The Avalonia application |
| `tests/` | Unit tests for each project, and opt-in integration tests against ASCOM |

## Environment variables

The application reads only `SIDERA_EQUIPMENT_FILE` and `SIDERA_SETTINGS_FILE` (file locations). Every other `SIDERA_*` variable below belongs to the tests of real devices and is not
read by the application; a test is skipped unless its variable is set.

## Real-hardware tests

A test of a real device runs when you name the device; there is no second switch for moving it. Only run them when the area around the equipment is clear: a test with a gate that is open moves the device
(a small, reversible move). A gate of the `_OK` kind is open unless it is set to `0`, `false`, `no` or `off`, and then the part that moves is left out.

| Variable | Enables |
|---|---|
| `SIDERA_ASCOM_CAMERA`, `SIDERA_ASCOM_FOCUSER`, `SIDERA_ASCOM_MOUNT`, `SIDERA_ASCOM_ROTATOR` | The ProgId of a real device: connects and reads it, short exposures, small focuser moves, a rotator move of two degrees and back, a short slew and stop, tracking and axis moves, stop-exposure, cooling, writing back the site it reports, a real autofocus (needs the camera and a focuser, and a star field) |
| `SIDERA_MERIDIAN_FLIP_TARGET=<ra hours>,<dec degrees>` | With `SIDERA_ASCOM_MOUNT`: the flip test slews to this target (a target just past the meridian, mechanics clear). Without it the test only reads and reports where the meridian is |
| `…_OK=0` | Leaves out what moves: `SIDERA_ASCOM_MOUNT_SLEW_OK`, `_TRACKING_OK`, `_AXIS_OK`, `_SITE_WRITE_OK`, `SIDERA_ASCOM_ROTATOR_OK`, `SIDERA_ASCOM_CAMERA_STOP_OK`, `_COOLING_OK`, `SIDERA_ASCOM_AUTOFOCUS_OK`, `SIDERA_MERIDIAN_FLIP_OK`, `SIDERA_PHD2_GUIDING_OK`, `SIDERA_PHD2_DITHER_OK` |
| `SIDERA_ASCOM_TESTS=0` | Skips the tests against the ASCOM simulators; they run when the ASCOM Platform is installed |
| `SIDERA_ALPACA_SERVER` | The Alpaca simulator (OmniSim) to test against, default `127.0.0.1:32323`; the tests run when it answers, and refuse every host except this computer. Start it bound to the loopback: `ascom.alpaca.simulators.exe --urls=http://127.0.0.1:32323`. `SIDERA_ALPACA_TESTS=0` skips them |
| `SIDERA_PHD2_HOST`, `SIDERA_PHD2_PORT` | A running PHD2 (default 127.0.0.1:4400): the tests run when it answers; they read its state, and start and stop real guiding and one dither (its equipment must be connected, a guide star needed). `SIDERA_PHD2_TESTS=0` skips them |
| `SIDERA_ASCOM_MANUAL=1` | The tests that open a dialog and wait for a person |
| `SIDERA_ASTAP_TESTS=1` | The tests against the real ASTAP of this computer |

## Your data

- Equipment: `%APPDATA%\Sidera\equipment.json` (override with `SIDERA_EQUIPMENT_FILE`). An equipment file from the time the project was
  called Astra (`%APPDATA%\Astra\equipment.json`) is copied on the first start; the old one is left untouched.
- Settings (the observing site): `%APPDATA%\Sidera\settings.json` (override with `SIDERA_SETTINGS_FILE`). Without the file, or without a
  site in it, the site is unknown; Sidera never assumes 0° 0° 0 m. Imaging setups keep their optical inputs in the equipment file.
- Environment variables with the old `ASTRA_` prefix still work when the `SIDERA_` one of the same name is not set.
- The extension `.astraseq` and the format names `astra-sequence` and `astra-equipment` belong to the formats from before the rename.
  They are kept, so existing files keep loading.

### Plate solving and centering

Configure ASTAP in Settings → Plate Solving. Sidera discovers `astap_cli.exe`
and installed database tiles; D50 is supported along with other ASTAP catalogs.
Executable and optional database folders, solve timeout, search radius,
downsample, blind fallback, exposure, centering tolerance and attempt limit
are persisted in the existing `settings.json` alongside the observing site.

The Plate Solve workspace captures a dedicated exposure or solves the last
frame. Hints use the optical geometry of the current imaging setup, camera pixel size,
frame binning and dimensions, and connected mount coordinates. Unknown
coordinates stay unknown. Results include WCS-derived scale/FOV, rotation,
parity, pointing error and diagnostic focal-length estimates; imaging setup optics
are never changed automatically.

Slew & Center holds the camera and mount resources until it finishes, uses
spherical pointing errors and tangent-plane corrections, stops at tolerance
or the attempt limit, and never syncs the mount. There are no environment gates for solving
or centering; moving a real mount or rotator asks for the confirmation described under Hardware safety.
Do not confirm unless physical operation is intended.

The backend-neutral Plate Solve sequence step is persisted as `plateSolve`
in `.astraseq` version 7. Versions 1–6 continue to load; older Sidera versions
reject version 7 rather than silently skipping the new action.

### Rotation

The rotation of the sky in an image is the angle from the top of the image to celestial north, counterclockwise, in (−180, 180] (the `CROTA2` of a plate
solve). A rotator reports a position from 0 to 360. They are related by the calibration of the imaging setup, `sky = ±position + offset`, which Sidera does not guess:
Calibrate (Equipment → Rotator) measures the offset with one plate solve at the current position and keeps it, with the time, for the setup. The direction is a
setting (not reversed unless chosen); a wrong direction shows at the first verified rotation, which fails instead of turning on.

- **Rotate to Angle** turns the rotator by the calibration and does not solve. Without a calibration it fails and says so.
- **Rotate & Verify** turns, solves, compares by the shortest signed angle, corrects, and repeats until the error is within the tolerance (default 0.5°, set
  in Settings → Plate Solving) or the attempts (default 4) are used up. The solve is the authority; an error that grows after a correction stops it.
- **Center & Rotate** centers the target, rotates and verifies, and centers again when turning moved the field; it ends only when both the pointing and the
  rotation tolerances hold, after at most 3 rounds.

They are sequence steps (`rotateToAngle`, `rotateAndVerify`, `centerAndRotate`, `.astraseq` version 7) and are offered by Framing and Add to Session for an imaging setup
with a rotator. A rotation holds the rotator and every camera on it (and the mount when it centers) for its whole duration: the rotator does not move while a camera
exposes, and two rotations do not overlap. Plate Solve, Slew & Center, Rotate & Verify and Center & Rotate never synchronize the mount; only the explicit
Sync step or button does.

Safe installed-ASTAP tests use `SIDERA_ASTAP_TESTS=1`. A known FITS can be
supplied in `SIDERA_ASTAP_TEST_IMAGE`, with optional expected center in
`SIDERA_ASTAP_TEST_RA` (hours) and `SIDERA_ASTAP_TEST_DEC` (degrees). The
pixel round-trip validation supports unsigned 16-bit primary FITS images.
The production backend accepts camera frames and writes its own FITS.

## License

GNU General Public License v3.0, see [LICENSE](../LICENSE).
