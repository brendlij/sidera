# Dashboard

![The dashboard](images/dashboard.png)

The dashboard shows what Sidera is doing now: the session at the top, the imaging setups on the left, the mount, the guiding graph and the latest image on the right, and the strip with what the session shares at the bottom.

| Panel | Shows |
|---|---|
| **Session** | Open Session when none is active; while a run goes on, the step that runs, the frame counter and the elapsed time |
| **Imaging setups** | For each setup its camera, focuser and filter wheel and whether they are connected. While a session runs, a lane for each setup: its step, **Frame 22 / 100**, the same in exposure time (**21 min of 1 h 40 min · 1 h 19 min left**), and its autofocus |
| **Mount** | State and tracking rate (Tracking · Sidereal, Slewing, Parked, Not tracking), where it points (right ascension and declination, altitude and azimuth), the **side of the pier**, the sidereal time and UTC of the mount, the site it works with, and **when its pointing crosses the meridian** (*In 41 min*, *6 min ago*). With a target in the session it also says when the flip is due |
| **Guiding** | The guide error of the last two minutes with RMS and SNR, once a guider measures |
| **Latest image** | The last frame, from a capture or from the session, stretched like the viewer of the Imaging page, with its analysis |
| **Shared** | The mount and the guider the session uses, with their state |

A device that is not connected shows its state and nothing stale: the mount of an unconnected mount says *Not connected* and no position.

## Choosing the panels

**Settings → Appearance → Dashboard** switches each panel on or off: the setups, the mount, the guiding graph, the latest image and the shared strip. A panel that is off is not drawn; what it shows is still on its own page. Save applies it at once. The order and size of the panels are not adjustable yet.

## The status bar

The bar at the bottom of the window shows the same on every page: the state of the run, the step and frame of each setup, and the elapsed time. When a step has been the same for 20 seconds, the bar says how long (*for 1 min 32 s*), which tells a slow step (a slew, a solve, a focus) from one that hangs. A camera whose exposure is over and whose image has not arrived says *Waiting for the camera*; while the image is read from the camera it says *Downloading the frame…* (ASCOM cameras).
