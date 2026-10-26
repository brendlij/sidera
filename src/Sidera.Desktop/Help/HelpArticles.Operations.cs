using System.Collections.Generic;
using Sidera.Desktop.ViewModels;

namespace Sidera.Desktop.Help;

public static partial class HelpCatalog
{
    private static IEnumerable<HelpArticle> ImagingArticles() =>
    [
        new("manual-capture", ImagingSection, "Manual Capture", "Take a single frame with the camera of the current imaging setup, through the same pipeline as a sequence.")
        {
            Steps =
            [
                "Connect the camera on the Equipment page. The Imaging page says Connect … on the Equipment page to capture until it is.",
                "Set the exposure in seconds and the frame type (Light and so on). Gain, offset and binning are set where the camera supports them.",
                "Press Capture. The frame appears in the viewer.",
                "Zoom with the wheel around the pointer, drag to pan, and use Fit, 1:1 and Auto stretch to look at the frame.",
                "Save FITS writes the data as it was taken, with its metadata. Save PNG writes what you see.",
            ],
            Notes =
            [
                "Auto stretch is for the display only: the data is never changed.",
                "Nothing on the page chooses the camera: it is the imaging setup of the sidebar. With several cameras and no setup the page says so and offers to create one.",
                "Show stars marks the stars that were measured in the frame.",
            ],
            Screenshot = Shot("imaging.png", "The Imaging page.",
                (1, "The exposure", 0.27, 0.12), (2, "Capture", 0.48, 0.12), (3, "The viewer", 0.19, 0.215), (4, "Fit, 1:1, stretch and save", 0.19, 0.87), (5, "Manual autofocus", 0.73, 0.31)),
            Actions = [Page("Open Imaging", AppPage.Imaging), Page("Open Equipment", AppPage.Equipment)],
            Related = ["autofocus", "camera-wont-connect"],
            Keywords = ["capture", "frame", "fits", "png", "stretch", "zoom", "exposure"],
        },

        new("saved-frames", ImagingSection, "Saved Frames", "Every frame that a session takes is written to disk as a FITS file, in folders and under a name that you choose.")
        {
            Steps =
            [
                "Open Settings → Imaging. Save every frame of a session as FITS is on by default.",
                "Choose the save folder. Empty uses the Sidera folder in your Pictures.",
                "Fill in Folders inside the save folder and File name with parts in braces, for example {target}/{night}/{type} and {target}_{type}_{filter}_{exposure}_{n}. The line under the fields shows a file as it would be named.",
                "Run the session. A file is written for every exposure. A frame that cannot be saved is told to you as an error message, once.",
            ],
            Notes =
            [
                "The parts are {target}, {setup}, {camera}, {filter}, {type}, {exposure}, {night}, {date}, {time}, {n}, {gain}, {offset}, {temp} and {bin}. A part that is not known (no filter wheel, no target) is left out of the name.",
                "{night} is the evening that the night began, so frames after midnight stay in the folder of that night. {n} counts up in the folder. A file that is already there is never replaced.",
                "The header says what is known: OBJECT, FILTER, EXPTIME, DATE-OBS, GAIN, OFFSET, XBINNING, YBINNING, CCD-TEMP, IMAGETYP, INSTRUME, TELESCOP, FOCALLEN, the pixel size, the pointing of the mount and the site. Nothing that is not known is written.",
                "Frames are written in the background, so saving never holds up the next exposure. A file is named .part until it is complete.",
                "Frames of an autofocus and of a plate solve are not saved. JPG, a second copy and moving the frames after the session are not there yet.",
            ],
            Actions = [Setting("Open Imaging settings", "imaging")],
            Related = ["manual-capture", "file-locations"],
            Keywords = ["save", "fits", "folder", "name", "header", "files", "frames", "path"],
        },

        new("autofocus", ImagingSection, "Autofocus", "Samples the focus around where it is, fits the curve, moves to the best position and checks it.")
        {
            Steps =
            [
                "Connect the camera and the focuser of the setup and point at stars.",
                "Open Imaging and look at the Autofocus panel on the right: exposure, step size and number of samples.",
                "Press Start Autofocus. The samples, the curve and the result appear as it goes. Cancel stops it.",
                "In a session use the Autofocus action, or the automation of a block (at the start, every N minutes, after a filter change).",
            ],
            Notes =
            [
                "A setup without a focuser says No focuser configured and offers to configure one. The start button says which device is missing: Connect the camera of … or Connect the focuser of … on the Equipment page.",
                "The defaults for a new autofocus (exposure, step size, samples) are in Settings → Autofocus. They apply to what you create afterwards.",
                "An autofocus holds its camera and its focuser. It holds the mount still only if Settings → Autofocus says so.",
            ],
            Screenshot = Shot("imaging.png", "The autofocus panel is on the right of the Imaging page.",
                (1, "Autofocus: exposure, step size and samples", 0.73, 0.31), (2, "Start Autofocus", 0.74, 0.535)),
            Actions = [Page("Open Imaging", AppPage.Imaging), Setting("Open Autofocus settings", "autofocus")],
            Related = ["automation", "focuser-missing", "autofocus-cannot-start"],
            Keywords = ["focus", "hfr", "curve", "focuser"],
        },

        new("filters", ImagingSection, "Filters", "The filter wheel turns to a slot. A block says which filter its frames are taken with.")
        {
            Steps =
            [
                "Connect the filter wheel on Equipment → Filter Wheels. Its filters are listed by slot and name. Choose the driver of your filter wheel, or Simulator to try Sidera. The names of the filters come from the driver: change them in its setup dialog.",
                "In a block add the action Set Filter and choose the filter. Put it before the exposure.",
                "Duplicate the block for the next filter and change the Set Filter action of the copy.",
            ],
            Notes =
            [
                "In a block the filter wheel is the one of the setup of the sequence. You never choose a device for the action.",
                "The automation of a block can run an autofocus after a filter change.",
            ],
            Related = ["lrgb-session", "blocks", "autofocus"],
            Keywords = ["filter wheel", "slot", "luminance", "narrowband"],
        },

        new("dithering", ImagingSection, "Dithering", "A small move of the guide star between frames, so that hot pixels and noise do not stack up in one place.")
        {
            Steps =
            [
                "Connect a guider (PHD2) and start guiding. Without guiding there is no dither.",
                "In the automation of a block turn on a dither every N exposures, with the size and the settle you want.",
                "Dither Now is a single dither as an action, in the preparation of a target.",
            ],
            Notes =
            [
                "A dither waits for the exposures on that mount to end, and keeps new ones from starting until the guider has settled.",
                "The settle is the guider's own: Sidera waits for it and never uses a fixed delay. A settle that takes longer than its timeout is reported.",
                "With several setups on one mount or one guider the dither runs once for all of them.",
            ],
            Actions = [Setting("Open Guiding settings", "guiding")],
            Related = ["guiding", "automation", "multi-camera"],
            Keywords = ["dither", "settle", "amplitude", "guider"],
        },
    ];

    private static IEnumerable<HelpArticle> AstrometryArticles() =>
    [
        new("plate-solving", AstrometrySection, "Plate Solving", "Solves a frame to find out where the telescope really points. Sidera uses ASTAP.")
        {
            Steps =
            [
                "Install ASTAP and a star database such as D50. Sidera does not install them.",
                "Open Settings → Plate solving. Leave the ASTAP executable empty to find it, or press Browse. Refresh detection shows what was found.",
                "Press Save plate solving.",
                "Open Plate Solve, connect the camera and press Capture & Solve, or Solve Last Frame for the frame you have.",
                "The result shows the position, the rotation and the scale, and compares the scale with your optics.",
            ],
            Notes =
            [
                "Sync Mount to Solved Position tells the mount where the solve found it. It is only ever done by pressing it: a solve, a sequence or a centering never synchronizes the mount.",
                "A solve starts from a hint when it has one (the mount position) and may fall back to a blind search if you allow it in the settings.",
                "Timeout, search radius and downsample are in Settings → Plate solving.",
            ],
            Screenshot = Shot("settings-platesolving.png", "Settings → Plate solving.",
                (1, "The tab", 0.63, 0.19), (2, "The ASTAP program: empty finds it", 0.47, 0.48), (3, "What was found", 0.70, 0.60), (4, "Timeouts and attempts", 0.74, 0.69), (5, "Save", 0.31, 0.94)),
            Actions = [Setting("Open Plate Solving settings", "platesolving"), Page("Open Plate Solve", AppPage.PlateSolve)],
            Related = ["slew-center", "plate-solve-fails", "astap-database", "rotation"],
            Keywords = ["astap", "solve", "database", "d50", "pointing"],
        },

        new("slew-center", AstrometrySection, "Slew & Center", "Slews to coordinates, solves, corrects and repeats until the target is where you want it.")
        {
            Steps =
            [
                "Connect the camera and the mount. Open Plate Solve.",
                "Enter the right ascension and the declination, the tolerance in arcseconds and the number of attempts.",
                "Press Slew & Center. The status shows the stage, the attempt and the error in arcseconds.",
                "In a session the same thing is the action Slew & Center in the preparation of a target. With a rotator and a rotation on the target it centers and rotates.",
            ],
            Notes =
            [
                "A large slew and real equipment ask first: Ensure the equipment can move safely.",
                "The solve is the authority: an error that grows after a correction stops the centering.",
                "Slew & Center needs a plate solver and a mount that is connected. Without them the button says what is missing, for example Connect the mount to slew.",
            ],
            Screenshot = Shot("plate-solve.png", "The Plate Solve page.",
                (1, "The solve exposure", 0.19, 0.10), (2, "Capture & Solve", 0.185, 0.36), (3, "The solver", 0.40, 0.245), (4, "Slew & Center", 0.18, 0.515), (5, "Tolerance and attempts", 0.50, 0.595)),
            Actions = [Page("Open Plate Solve", AppPage.PlateSolve)],
            Related = ["plate-solving", "framing", "rotation", "plate-solve-fails"],
            Keywords = ["center", "centre", "slew", "pointing", "mount"],
        },

        new("framing", AstrometrySection, "Framing", "Plan the field on a picture of the sky, then send it to the mount or to the session.")
        {
            Steps =
            [
                "Open Framing and search an object, for example M31, NGC 7000 or IC 434.",
                "The field of the current imaging setup is drawn on a sky survey. Drag it to move and turn it to rotate it.",
                "Press Slew & Center to go there now, or Add to Session to make it a target with a preparation.",
                "With a rotator the page offers Center & Rotate. Without one, Solve Again compares the rotation of a solve with the plan and says by how many degrees to turn the camera.",
            ],
            Notes =
            [
                "The survey pictures come from the CDS over the network and are kept in a cache on your disk. No survey is bundled: without a network there is no picture, but the planning still works.",
                "The field of the telescope is drawn in red: the position of the mount, live, or the solved position while it is current.",
                "The field needs the focal length and the camera of the setup (the optics on the camera page).",
            ],
            Actions = [Page("Open Framing", AppPage.Framing), Setting("Open Framing settings", "framing")],
            Related = ["slew-center", "rotation", "multi-target"],
            Keywords = ["survey", "object", "sky", "field of view", "m31"],
        },

        new("rotation", AstrometrySection, "Rotation", "A rotator reports a position. The sky in the image has a rotation. They are two different angles.")
        {
            Steps =
            [
                "Connect the rotator on Equipment → Rotators. Its position is the mechanical angle of the driver, 0 to 360.",
                "Calibrate it once with a plate solve: open the rotator page of the camera's setup and press Calibrate with a plate solve. The camera must be connected and pointed at stars.",
                "Set a Rotation on the target of the session (the field is optional).",
                "Use Center & Rotate (in Framing or as an action) to turn the rotator until the solve shows that rotation.",
            ],
            Notes =
            [
                "Sidera does not know how the position of a rotator relates to the sky until you calibrate it. A rotator that is not calibrated says so.",
                "The calibration is kept with the imaging setup. A new camera angle on the rotator needs a new calibration.",
                "Every rotation is verified with a solve and never assumed.",
            ],
            Related = ["slew-center", "framing", "rotator-calibration"],
            Keywords = ["rotator", "angle", "position", "calibrate", "camera angle"],
        },
    ];

    private static IEnumerable<HelpArticle> MountArticles() =>
    [
        new("guiding", MountSection, "Guiding", "Sidera guides through PHD2. PHD2 keeps its own equipment, calibration and star.")
        {
            Steps =
            [
                "Start PHD2 and connect its camera and mount in PHD2.",
                "In Equipment → Guiders choose PHD2 as the driver. Host and port are 127.0.0.1 and 4400 for the first PHD2 on this computer; a second PHD2 uses 4401.",
                "Press Connect, then Start Guiding. Stop Guiding, Pause and Resume are next to it.",
                "The page shows the guide graph in arcseconds, the RMS, the SNR of the star and how the last settle went.",
            ],
            Notes =
            [
                "In a session Start Guiding is part of the preparation of a target, and Stop Guiding can go in the End.",
                "A guider is shared by the setups that name it. The defaults for dither and settle are in Settings → Guiding.",
                "A real guided session and a real dither are not yet verified against real equipment.",
            ],
            Actions = [Page("Open Equipment", AppPage.Equipment), Setting("Open Guiding settings", "guiding")],
            Related = ["dithering", "guiding-unavailable", "shared-mounts"],
            Keywords = ["phd2", "guider", "rms", "calibration", "settle"],
        },

        new("meridian-flip", MountSection, "Meridian Flip", "When the target crosses the meridian, the mount flips once for every setup on it.")
        {
            Steps =
            [
                "Open Settings → Meridian Flip and tick Flip at the meridian. Set the minutes before which new exposures are held, the minutes after the meridian to flip, and the latest allowed flip.",
                "Choose what happens after the flip: stop and restart guiding, recenter, verify the rotation, autofocus, dither, pause.",
                "In a session the Session Automation line shows whether the flip is Off or On, and follows these defaults. Press Edit to make it custom for the session.",
                "Run the session. The status shows the countdown, and during the flip what it is doing.",
            ],
            Notes =
            [
                "Times are minutes from the moment the target is on the meridian, worked out from the hour angle (target, observing site, clock), not from the pier side the driver reports.",
                "An exposure that runs is never interrupted. From the hold time on an exposure only starts when it ends before the flip is due.",
                "A failed flip holds the setups of that mount until you retry or abort, or ends the session, as you chose.",
                "The mount is never synchronized by a flip.",
                "A real flip is tested by hand only. Do not leave a first flip unattended.",
            ],
            Screenshot = Shot("settings-meridianflip.png", "Settings → Meridian Flip.",
                (1, "Switch the flip on", 0.18, 0.43), (2, "When: hold, flip, latest", 0.31, 0.50), (3, "What happens after the flip", 0.18, 0.62)),
            Actions = [Setting("Open Meridian Flip settings", "meridianflip"), Page("Open Session", AppPage.Session)],
            Related = ["automation", "meridian-flip-disabled", "observing-site"],
            Keywords = ["flip", "meridian", "pier", "hour angle"],
        },

        new("shared-mounts", MountSection, "Shared Mounts", "Two imaging setups on one mount (or one guider) share it. Sidera works out who may do what at the same time.")
        {
            Steps =
            [
                "Give both imaging setups the same mount. Equipment shows it as shared equipment.",
                "Run a session with both setups. Their exposures run together.",
                "A slew, a centering, a dither or a flip takes the mount alone: it waits for the exposures that run and keeps new ones from starting.",
            ],
            Notes =
            [
                "Setups on different mounts work at the same time and never wait for each other.",
                "An autofocus holds only its camera and its focuser, unless Settings → Autofocus says it holds the mount still too.",
                "Sharing is inferred from the devices the setups name: nothing else to set.",
            ],
            Actions = [Page("Open Equipment", AppPage.Equipment)],
            Related = ["multi-camera", "automation", "meridian-flip"],
            Keywords = ["shared", "mount", "claims", "parallel"],
        },
    ];
}
