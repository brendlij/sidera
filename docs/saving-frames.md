# Saving frames

Every frame that a session takes is written to disk as a **FITS** file, in folders and under a name that you choose. It is on by default.

**Settings → Imaging** has the controls:

| Setting | Meaning |
|---|---|
| **Save every frame of a session as FITS** | Switches saving on or off |
| **Save folder** | The folder frames go to. Empty means the `Sidera` folder in your Pictures |
| **Folders inside the save folder** | A pattern, by default `{target}/{night}/{type}` |
| **File name** | A pattern without the extension, by default `{target}_{type}_{filter}_{exposure}_{n}` |

A line under the fields shows an example of the path while you type, for example
`D:\Astro\M31\2026-10-12\Light\M31_Light_Ha_300s_0007.fits`. A pattern that cannot be used (an unknown part, a stray brace) is refused with the reason.

## The parts of a pattern

| Part | Value | Example |
|---|---|---|
| `{target}` | The name of the target of the session | `M31` |
| `{setup}` | The imaging setup | `Newton` |
| `{camera}` | The camera | `CameraA` |
| `{filter}` | The filter in the light path when the exposure started | `Ha` |
| `{type}` | Light, Dark, Flat or Bias | `Light` |
| `{exposure}` | The exposure time | `300s` |
| `{night}` | The date of the evening the night began, so frames after midnight stay with their night | `2026-10-12` |
| `{date}` / `{time}` | The date and the start time of the exposure | `2026-10-13` / `014530` |
| `{n}` | A number that counts up in the folder (4 digits) | `0007` |
| `{gain}` `{offset}` `{temp}` `{bin}` | Gain, offset, sensor temperature, binning | `gain100` `offset50` `-10C` `bin1x1` |

A part whose value is not known (no filter wheel, no target) is left out of the name together with its separator. Characters a file system does not accept are replaced, and a pattern can never lead out of the save folder.

## What is written

- The frame as it was taken, never stretched: 16-bit, with the header below.
- **Header:** `OBJECT`, `FILTER`, `EXPTIME`, `DATE-OBS` (UTC), `GAIN`, `OFFSET`, `XBINNING`, `YBINNING`, `CCD-TEMP`, `IMAGETYP`, `INSTRUME`, `TELESCOP` (the imaging setup), `FOCALLEN`, `XPIXSZ`, `YPIXSZ`, the approximate pointing of the mount (`RA`, `DEC`) and the site (`SITELAT`, `SITELONG`, `SITEELEV`). Nothing that is not known is written, and no WCS: an image that has not been solved has none.
- Files are written **in the background**, so saving never holds up the next exposure. A file is named `.part` until it is complete, and **a file that already exists is never replaced**.
- A frame that cannot be written (full disk, a folder that is not there) is told to you as an error message, once.

## Not there yet

Colour cameras do not get a `BAYERPAT` header yet. JPG, a second copy of the frames (backup) and moving the frames after a session are not built; frames of an autofocus or a plate solve are not saved. The images of the Imaging page are saved by hand with **Save FITS** or **Save PNG**.
