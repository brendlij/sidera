# Troubleshooting

Each entry says what you see, the likely reason and what to check. The **Help** entry in the application has the same entries with a button to the page where it is fixed.

First: the **Diagnostics** page shows the version and the log file of this session. Most reports need the log (`%LOCALAPPDATA%\Sidera\logs`).

## A device does not connect
- **Likely:** the driver is not installed or is already in use by another program (many ASCOM drivers accept one client), or the cable or power is off.
- **Check:** the driver's own setup (Equipment → the device → Setup), that no other program holds it, and the error message at the top right (it names the driver).

## The meridian flip needs a setup with a mount
- **Likely:** the mount is connected but not assigned to the imaging setup.
- **Check:** Equipment → Imaging Setups → the setup → Mount.

## The flip failed: *The mount did not change sides of the pier*
- **Likely:** the mount keeps tracking past the meridian and does not turn for a slew to the same coordinates, or its preferred pier side is fixed.
- **Check:** the pier-side setting and meridian limits of the mount (OnStep: auto-flip, preferred pier side, limits). Its tracking has been stopped for safety.

## The flip failed: *ASTAP found no solution*
- **Likely:** the frame cannot be solved: the mount is not tracking (star trails), is still settling, the sky is cloudy, the focus is off, or the pointing is far from where Sidera thinks (alignment, site or time of the mount).
- **Check:** that the mount tracks, a single solve on the Plate Solve page, the site and the time of the mount (Equipment → Mounts shows the sidereal time and a *Location mismatch* card), and the ASTAP database.

## Plate solving timed out
- **Likely:** no or wrong database, the field of view hint is far off (the focal length in the optical train does not match the telescope), or too few stars.
- **Check:** Settings → Plate Solving, the focal length (the solve reports the scale it found), a longer exposure.

## Slew & Center does not converge
- **Likely:** a large pointing error that changes with the side of the pier (polar alignment, cone error, no pointing model), or a mount that does not reach the position it is told to.
- **Check:** the error of each attempt in the sidebar (it should shrink), a larger tolerance (60″ to 120″), and aligning the mount.

## Autofocus fails
- **Likely:** no star is measurable where the focuser stands (far out of focus), the step is too small for the HFR to change, or the exposure is too short.
- **Check:** focus roughly by hand, a longer exposure, a larger step size; *Settings → Autofocus* has the search settings.

## No frames on disk
- **Likely:** saving is off, or the folder cannot be written (the message says so).
- **Check:** Settings → Imaging: the checkbox, the save folder and the example path under the patterns. Frames of autofocus and plate solves are not saved.

## The mount is not tracking
- A mount may stop tracking at a limit or after an error of its own. Sidera starts tracking before a centering exposure when the mount says it does not track. Look at the status of the mount on the dashboard and in the firmware.

## A dome or roof does not respond
- **Check:** the host, port and device number of the Alpaca device (Equipment → Domes), that the Alpaca server is running, and *Use this dome*, *Ask this host*. Sidera only contacts the address you give it.
