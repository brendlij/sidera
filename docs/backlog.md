# Backlog

State: 29 Oct 2026. Priority 1 is what should be fixed before the first real imaging night. Priority 2 are the gaps found while testing.

Legend: `[x]` done · `[~]` partly done · `[ ]` open.

## Priority 1

### Images and display

- [~] **Save the frames of a session.** Built: one FITS file per exposure of a session, a folder and file name pattern, the header, background writing (see [Saving frames](saving-frames.md)). Open:
  - [ ] **JPG** as a second format, with a choice of which formats are written.
  - [ ] **Bayer pattern** (`BAYERPAT`) in the header for colour cameras, and the other keywords stackers like (`ROTATANG` and so on).
  - [ ] **Separate folders** for darks, flats and bias.
  - [ ] **Automatic backup** to a second location (a network drive or a second disk).
  - [ ] **Move after the session** (from a fast local folder to the archive), checking that the files arrived complete before the original is deleted.
  - [ ] A **low disk space** warning before the drive is full.
- [x] **Download marker** while the image is read from the camera (ASCOM cameras).
- [x] **Latest image on the dashboard** is stretched like the viewer of the Imaging page.
- [x] **Manual capture is off** while a session runs, with the reason.
- [ ] **Imaging page shows the current frame of a running session**, not only the dashboard. The Imaging page shares the frame of the dashboard, but this has not been checked in the running application.

### Dashboard

- [x] **Mount on the dashboard:** state and tracking rate, position, altitude and azimuth, pier side, sidereal time and UTC, the site, when the pointing crosses the meridian, the flip time of the session.
- [x] **Panels can be switched on and off** in Settings → Appearance.
- [ ] **Order and size** of the dashboard panels adjustable.
- [ ] **Other devices on the dashboard:** camera (temperature, cooling), focuser (position, temperature), filter wheel (current filter), rotator (angle), dome (azimuth, shutter).

### Feedback and navigation

- [~] **More feedback for running steps and exposures.** Built: how long the current step has been going on (status bar, after 20 s), *Waiting for the camera* when an exposure is overdue, the download state. Open: what comes next, an estimate of how long a step should take.
- [x] **Session progress in exposure time**, not only frames (*21 min of 1 h 40 min · 1 h 19 min left*).
- [ ] **Modal for the meridian flip** (like NINA): when the flip comes, a dialog with the steps and progress and a way to abort. Today it is a text line in the session automation card and a message when it fails.
- [ ] **Smarter navigation from warnings.** Example: *The meridian flip needs a setup with a mount* should take you to Equipment → Imaging Setups, where the mount is assigned. In general every warning and problem gets a button to the place where it is fixed (messages and problem lines).
- [ ] **The layout jumps** when messages appear or disappear. Keep it stable (reserve the space, move nothing).
- [ ] **Equipment page** is hard to find your way in (for example how to reach *all devices*). Make the structure clearer: the entry, the way between the overview, the kinds of device and the imaging setups.
- [ ] **Better connection between the pages** (Framing, Session, Plate Solve, Dashboard). Example: a target from Framing can **update an existing target of the session** instead of always adding a new one (an option: *add new* or *replace existing*).
- [ ] **Split view:** open a second page beside the current one (for example the session on the left and the mount on the right), choose the page, move the divider, close it, remember it for the next start.

### Autofocus

- [~] **Smarter autofocus.** Built: the search that starts at the current focus and turns round, positions remembered per setup, a blurred sample is not fatal, the fit through the widest run the hyperbola explains (or trend lines), the fall back to the best sample, settings (see [Autofocus](autofocus.md)). **Not tested on real stars.** Open:
  - [ ] Remembered positions by **temperature** and **filter**.
  - [ ] **Backlash** compensation for the focuser.
  - [ ] Review the **timeouts** of focuser moves and exposures during a run on real equipment.

### Location

- [ ] **Finish the location mismatch card.** The card and its three buttons (*Use Mount Location in Sidera*, *Send Sidera Location to Mount*, *Keep Both Unchanged*) exist. What is still missing needs to be clarified: when it is shown on connecting a real mount, how a change is confirmed, the feedback afterwards, and how an OnStep behaves.

## Priority 2

### Meridian flip

- [x] **The pier side must change** after the flip slew; otherwise it is a failure.
- [x] **Report a failed flip** as an error message with a way to the session.
- [x] **Stop tracking after a flip that failed** before the mount changed sides; a retry starts it again.
- [ ] **Tracking pause around the meridian, adjustable** (some mounts hit their pier otherwise and must stop tracking earlier). Settings next to the other flip settings (Settings → Meridian Flip and the session automation, same card, same unit: minutes from the meridian):
  - Pause tracking **before** and **after** the meridian, each adjustable and switchable.
  - It must fit **Flip after** and **Latest allowed flip** (a warning when tracking would stop before the flip is due; the flip wins). Without a flip: end imaging after the limit, stop tracking, optionally park.
  - Optionally **park** after a failed or overdue flip.
  - Show the tracking state in the status bar and on the dashboard.
- [~] **Centering after the flip.** Built: settle time after a slew, a lost solve is tried again, tracking is started when the mount does not track, a retry keeps the corrected position. Open:
  - [ ] **Sync after a successful solve** as an option (with confirmation), so that the model of the mount follows; otherwise every slew repeats the same offset.
  - [ ] **Warn about a large pointing error** (for example more than 0.5°): check the site, the time and the alignment of the mount instead of trying on quietly.
  - [ ] Show the **error of each centering attempt** in the session.
- [~] **Coordinates of date (precession).** Done for the meridian time. Open: a slew to a topocentric mount sends the J2000 position as it is (about 22′ off; Slew & Center corrects it).
- [ ] **OnStep:** the auto-flip and the preferred pier side of the firmware must not interfere with the flip of Sidera; Sidera should show or check them.
- [ ] Check how the flip steps look in the **tree view**.

### Hardware

- [ ] Never run on real hardware: guiding and dither through PHD2, domes and roll-off roofs, the ASCOM filter wheel, the new flip safeguards, the autofocus search, abort and tracking commands of a real mount beyond the first tests.
- [ ] The dome does not follow the mount (no slaving).

### Miscellaneous

- [ ] Help: a few markers sit slightly on text; the Red Night and Light themes have not been looked at.
- [ ] Hardware tests: read the ProgIds from `equipment.json` instead of setting `SIDERA_ASCOM_*`.
- [ ] A test OmniSim listening on `0.0.0.0:32323` should be bound to `127.0.0.1` only.
- [ ] A **logo** (the current one in the README is a placeholder) and an installer or a release download.
