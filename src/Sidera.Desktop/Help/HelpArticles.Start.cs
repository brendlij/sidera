using System.Collections.Generic;
using Sidera.Desktop.ViewModels;

namespace Sidera.Desktop.Help;

public static partial class HelpCatalog
{
    private static IEnumerable<HelpArticle> StartArticles() =>
    [
        new(StartArticle, StartSection, "Getting Started", "From an empty installation to your first frame, in six steps. You can do all of it with the built-in simulators if you have no equipment at hand.")
        {
            Steps =
            [
                "Connect your camera: open Equipment, go to Cameras, choose the driver of your camera (or Simulator to try Sidera) and press Connect. Nothing moves when a device connects.",
                "Add the rest of your equipment the same way: focuser, mount, guider and, if you have one, a dome or a roll-off roof.",
                "Enter the focal length of your telescope on the camera page, under Optical train. Framing, Plate Solve and the pixel scale use it.",
                "Set up plate solving in Settings → Plate solving. Sidera uses ASTAP: it needs the program and a star database such as D50.",
                "Enter your observing site in Settings → Observatory. Altitudes, darkness and the meridian are worked out from it.",
                "Open Session, press + Add target, set the imaging block (filter, exposure, frames) and press Run.",
            ],
            Notes =
            [
                "With one camera there is nothing to create: Sidera uses the only device of each kind. With several cameras it asks you to create an imaging setup first.",
                "Real equipment is asked about before it moves. The simulators never ask.",
            ],
            Screenshot = Shot("equipment-camera.png", "Equipment → Cameras: the driver, Connect and the optics of the telescope.",
                (1, "The driver of the camera", 0.60, 0.298), (2, "Connect", 0.98, 0.296), (3, "Optical train: focal length and aperture", 0.19, 0.418), (4, "Add another camera", 0.615, 0.245)),
            Actions = [Page("Open Equipment", AppPage.Equipment), Setting("Open Plate Solving settings", "platesolving"), Setting("Open Observatory settings", "observatory"), Page("Open Session", AppPage.Session)],
            Related = ["first-session", "equipment-setup", "plate-solving"],
            Keywords = ["start", "begin", "first", "new", "install"],
        },

        new("dashboard", StartSection, "The Dashboard", "The state of your equipment and your session at a glance.")
        {
            Steps =
            [
                "Open Session starts or opens a session when none is active.",
                "Imaging setups lists each setup with its camera, its focuser and its filter wheel, and whether they are connected.",
                "Mount shows what the mount is doing (tracking and its rate, slewing, parked), where it points, the side of the pier, the sidereal time and the site it works with, and when its pointing crosses the meridian. With a session that has a target it also says when the flip is due.",
                "Guiding shows the guide error over the last two minutes, with the RMS and the SNR of the star, once a guider is connected and guiding.",
                "Latest image shows the last frame that was taken, by a capture or by the session.",
                "Shared lists the mount and the guider that the setups share, with their state.",
            ],
            Notes =
            [
                "While a session runs the dashboard also shows what it is doing. The bar at the bottom of the window shows that on every page.",
                "A device that is not connected shows its state and nothing stale.",
                "Settings → Appearance chooses which panels the dashboard shows: the setups, the mount, the guiding graph, the latest image and the strip of shared equipment.",
            ],
            Screenshot = Shot("dashboard.png", "The dashboard.",
                (1, "Open a session", 0.47, 0.235), (2, "The imaging setups", 0.15, 0.33), (3, "Guiding", 0.81, 0.27), (4, "The latest image", 0.70, 0.50), (5, "The shared mount and guider", 0.15, 0.80)),
            Actions = [Page("Open Dashboard", AppPage.Dashboard), Page("Open Session", AppPage.Session)],
            Related = ["getting-started", "first-session", "shared-mounts"],
            Keywords = ["dashboard", "overview", "status", "home"],
        },

        new("first-session", StartSection, "First Imaging Session", "A session with one target and one block of exposures. It takes five minutes to set up.")
        {
            Steps =
            [
                "Connect what the session uses. The Run button says what is missing, for example: Connect ASI2600MM to run the sequence.",
                "Open Session and press + Add target. You can also choose an object in Framing and press Add to Session.",
                "Enter the coordinates of the target as hours and degrees (5.588 and -5.39) or as 05:35:17 and -05:23:28.",
                "The shared preparation starts with Slew & Center, Autofocus and Start Guiding. Switch off with the checkbox what you do not use.",
                "Open the imaging block and set the filter, the exposure and the number of frames.",
                "Add limits if you want the target to stop by itself: altitude below, dawn, a time or a duration.",
                "Press Run. Real equipment asks once per device: Ensure the equipment can move safely.",
            ],
            Notes =
            [
                "Pause holds the sequence at the next safe point and Resume carries on. Cancel stops it.",
                "Save the session with Save or Save As… to keep it as a .sideraseq file.",
            ],
            Screenshot = Shot("session-empty.png", "A new session: the steps are on the page.",
                (1, "Add a target", 0.195, 0.245), (2, "Or choose it in Framing", 0.29, 0.245), (3, "The four steps", 0.40, 0.185), (4, "Run, Pause and Cancel", 0.815, 0.05)),
            Actions = [Page("Open Session", AppPage.Session), Page("Open Framing", AppPage.Framing)],
            Related = ["sequencer-basics", "blocks", "limits"],
            Keywords = ["target", "run", "block", "exposure"],
        },

        new("equipment-setup", StartSection, "Equipment Setup", "Every device is listed once, under its kind. Choosing the driver is the whole of setting a device up.")
        {
            Steps =
            [
                "Open Equipment and choose the page of the kind: Cameras, Focusers, Filter Wheels, Rotators, Mounts, Guiders or Domes.",
                "Choose the driver in the list: an installed ASCOM driver (for cameras, mounts, focusers and rotators), the Simulator, PHD2 for a guider, or ASCOM Alpaca for a dome. Rescan looks for installed drivers again.",
                "Press Setup to open the setup dialog of an ASCOM driver. It also works while the device is not connected.",
                "Press Connect. A device that is not connected shows its name and state and nothing else.",
                "For an extra device of a kind press + Add, for example + Add Camera.",
            ],
            Notes =
            [
                "A device that is connected cannot be swapped for another driver: disconnect it first.",
                "An imaging setup is optional. It groups a camera with its focuser, filter wheel, rotator and optics, and can name a mount and a guider that other setups share.",
                "With one camera and no imaging setup Sidera does not need one. With several cameras it never guesses: the Imaging page says that multiple imaging paths are available and offers to create an imaging setup.",
            ],
            Screenshot = Shot("equipment-overview.png", "The overview of the current imaging setup.",
                (1, "The devices of the setup", 0.185, 0.20), (2, "Its optics", 0.185, 0.375), (3, "The mount and the guider it shares", 0.185, 0.625), (4, "Every device by kind", 0.985, 0.03), (5, "The imaging setup of the application", 0.15, 0.10)),
            Actions = [Page("Open Equipment", AppPage.Equipment)],
            Related = ["getting-started", "shared-mounts", "multiple-imaging-paths"],
            Keywords = ["driver", "ascom", "connect", "simulator", "device"],
        },
    ];

    private static IEnumerable<HelpArticle> SequencingArticles() =>
    [
        new("sequencer-basics", SequencingSection, "Sequencer Basics", "A session reads from top to bottom. What you see on the page is what runs.")
        {
            Steps =
            [
                "Session Automation is at the top: the meridian flip of the session. It is set once for the whole session.",
                "Start has the actions that run once, before the first target: wait for darkness, unpark, cool the camera.",
                "Each target has its coordinates, a shared preparation (done once for every setup on the mount), limits and one sequence for each imaging setup.",
                "A sequence is made of blocks. A block is one line: for example 300 s × 40 · Dither 3 · AF 60 m.",
                "End has the actions that run once after the last target: park, warm the camera, close the dome.",
            ],
            Notes =
            [
                "Blocks shows the session in the words above. Tree shows the explicit steps it compiles to. Opening the tree asks first, because the blocks, the automation and the limits are no longer editable afterwards.",
                "Selecting something opens it in a drawer on the right; nothing detailed is on the page all the time.",
                "Blocks, actions and targets can be added from a library, moved, duplicated, switched off and removed.",
            ],
            Screenshot = Shot("session-blocks.png", "The session page.",
                (1, "Session automation", 0.183, 0.17), (2, "Shared preparation", 0.18, 0.50), (3, "The block", 0.19, 0.743), (4, "The drawer: limits of the target", 0.63, 0.45), (5, "Run", 0.815, 0.05)),
            Actions = [Page("Open Session", AppPage.Session)],
            Related = ["blocks", "actions", "automation", "limits"],
            Keywords = ["session", "target", "tree", "start", "end"],
        },

        new("blocks", SequencingSection, "Blocks", "A block is what one setup does in one go: actions, a repeat, automation and limits.")
        {
            Steps =
            [
                "Press + Imaging block under a setup to add a block. Press its row to open it in the drawer.",
                "Actions run in order. The actions before the first exposure run once; from the exposure on they are repeated.",
                "Repeat says how many times: a number of times, until something holds, or both.",
                "Automation adds the dither and the autofocus that belong to the block (see Automation).",
                "Limits stop the block: stop at dawn, below an altitude, at a time or after a duration.",
            ],
            Notes =
            [
                "A closed block is one line: 300 s × 40 · Dither 3 · AF 60 m · stops: dawn, alt<25°.",
                "While a session runs every block shows its frame and what it waits for.",
                "Duplicate a block with the copy button on its row, then change what differs (for example the filter).",
            ],
            Screenshot = Shot("session-blocks.png", "A block on its row, with the buttons to move, duplicate and remove it.",
                (1, "The block", 0.19, 0.743), (2, "Add a block", 0.185, 0.835), (3, "Limits of the target", 0.63, 0.45)),
            Related = ["actions", "repeat", "automation", "limits", "lrgb-session"],
            Keywords = ["block", "frames", "duplicate", "filter"],
        },

        new("actions", SequencingSection, "Actions", "An action is one thing the sequence does, in your words. It never asks for a device: it works with the imaging setup it is in.")
        {
            Steps =
            [
                "Press + Add action where you want the action: in a block, in the preparation of a target, or in Start or End.",
                "Type to search, choose a group, or use the arrow keys. Enter adds the action; Escape closes the library.",
                "Open the action to set it: an exposure in seconds, a filter, the size of a dither.",
                "Switch an action off with its checkbox: it stays where it is and is left out of the run.",
            ],
            Notes =
            [
                "The library shows only the actions that can go where you are adding. Exposure and Set Filter belong in a block; Cool Camera, Park and the dome actions belong in Start, End and the preparation.",
                "The groups are Camera, Filter wheel, Focus, Guiding, Mount, Dome, Astrometry and Control (Wait and Wait Until).",
                "Advanced: an action can name another setup (Use setup). Normally it uses the setup of the sequence it is in.",
            ],
            Screenshot = Shot("session-library.png", "The library of actions.",
                (1, "Add an action here", 0.51, 0.32), (2, "Search", 0.79, 0.267), (3, "Groups", 0.945, 0.325), (4, "Enter adds the chosen action", 0.88, 0.41)),
            Related = ["blocks", "dome-actions", "keyboard-shortcuts"],
            Keywords = ["library", "add", "wait", "park", "cool"],
        },

        new("repeat", SequencingSection, "Repeat", "How long a block goes on.")
        {
            Steps =
            [
                "Open the block and set Repeat: a number of frames, until something holds, or both.",
                "Wait Until is an action that holds the sequence until all of its conditions are true: the target is above an altitude, darkness, a time, a duration.",
                "Repeat until and Limits stop when any of their conditions is true: the frames of the block, the target below an altitude, dawn, a time of day, a duration.",
            ],
            Notes =
            [
                "A stop never cuts an exposure: the exposure that runs is finished, then the block ends.",
                "Altitudes are worked out from the coordinates of the target, your observing site and the time. They are never read from the mount.",
                "Dusk means darkness. Dawn means the Sun coming up through the chosen darkness after the block started. Where it never gets that dark within 48 hours there is no dawn, and a stop at dawn simply does not trigger.",
                "A time of day is the next time the clock reads it, in the time zone shown on the page.",
            ],
            Related = ["limits", "blocks", "observing-site"],
            Keywords = ["until", "condition", "dawn", "altitude", "wait until"],
        },

        new("automation", SequencingSection, "Automation", "Things that happen by themselves during a run, at safe points. You switch them on; they never become steps to place again and again.")
        {
            Steps =
            [
                "Session automation: the meridian flip. Press Edit next to it, or leave it on the defaults of Settings → Meridian Flip.",
                "Block automation, dither: dither every N exposures, with the guider.",
                "Block automation, autofocus: at the start of the block, every N minutes and after a filter change.",
            ],
            Notes =
            [
                "A dither or an autofocus starts only when every setup on the mount or the guider is at a safe point.",
                "When several things are due at once the order is: stop, then meridian flip, then autofocus, then dither, then the next exposure. No flip, autofocus or dither starts for imaging that is over.",
                "A dither asked for by a block runs once for every setup on that mount or guider.",
            ],
            Actions = [Setting("Open Meridian Flip settings", "meridianflip"), Setting("Open Autofocus settings", "autofocus"), Setting("Open Guiding settings", "guiding")],
            Related = ["meridian-flip", "dithering", "autofocus", "shared-mounts"],
            Keywords = ["flip", "dither", "autofocus", "safe point"],
        },

        new("limits", SequencingSection, "Limits", "Limits end the imaging of a block or of a whole target by themselves.")
        {
            Steps =
            [
                "Select the target and look at Limits in the drawer. Tick the ones you want.",
                "Target altitude below: ends when the target sinks under an altitude.",
                "Dawn: ends when the Sun comes up through the chosen darkness (astronomical, nautical or civil).",
                "Time: ends at a time of day. Duration: ends after a number of hours, counted from when the block began imaging.",
            ],
            Notes =
            [
                "A limit of a target ends every setup under it. A block that reaches its own frames does not stop the other setups.",
                "A stop never cuts an exposure. The end of the session follows as after a normal end.",
                "A duration counts the wall-clock time from the start of imaging. A pause counts; the time before the start does not.",
                "Not yet: weather, the Moon, adaptive scheduling.",
            ],
            Screenshot = Shot("session-blocks.png", "Limits in the drawer of a target.",
                (1, "The limits of the target", 0.63, 0.45), (2, "A block on its own line", 0.19, 0.743)),
            Related = ["repeat", "blocks", "observing-site"],
            Keywords = ["stop", "dawn", "altitude", "duration", "time"],
        },

        new("multi-target", SequencingSection, "Multi-Target Sessions", "A session can have several targets, imaged one after the other.")
        {
            Steps =
            [
                "Press + Add target under the last target, or send a target from Framing with Add to Session.",
                "Give each target its coordinates (and a rotation if you use a rotator), its preparation, its blocks and its limits.",
                "Move a target up or down with its arrows, duplicate it, or switch it off with its checkbox.",
            ],
            Notes =
            [
                "The preparation of a target (Slew & Center or Center & Rotate, Autofocus, Start Guiding) is done once for every setup on the mount, before its blocks.",
                "The setups of one target share its coordinates: setups on one mount cannot be pointed at different targets at the same time.",
                "The limits of a target end the imaging of that target; the next target follows.",
            ],
            Related = ["sequencer-basics", "framing", "limits"],
            Keywords = ["targets", "several", "multiple", "order"],
        },

        new("multi-camera", SequencingSection, "Multi-Camera Sessions", "Two cameras can image the same target at the same time, each with its own sequence.")
        {
            Steps =
            [
                "Add the second camera in Equipment → Cameras, and create an imaging setup for each camera in Equipment → Imaging Setups.",
                "In the session press the + next to the setup tab of a target (Add imaging setup sequence) and choose the second setup.",
                "Each setup gets its own tab and its own blocks. They run side by side wherever their devices allow it.",
                "The line under the tabs says Parallel imaging and Shared: for example Shared: AM3 · PHD2.",
            ],
            Notes =
            [
                "Setups on one mount share it. Their exposures run together; a slew, a dither or a flip waits for the exposures that run and keeps new ones from starting.",
                "A dither asked for by one block runs once for every setup on that mount or guider, so the others wait for it.",
                "Setups on different mounts never wait for each other. A meridian flip is per mount.",
                "The setup selector in the sidebar, the lane tabs and Parallel imaging appear only when two or more setups are usable (their camera is connected).",
            ],
            Actions = [Equipment("Open Imaging Setups", EquipmentSection.ImagingSetups), Page("Open Session", AppPage.Session)],
            Related = ["shared-mounts", "dithering", "multiple-imaging-paths"],
            Keywords = ["two cameras", "parallel", "lane", "second", "imaging setup"],
        },

        new("lrgb-session", SequencingSection, "Build an LRGB Session", "One target, four filters, with dither, autofocus and a stop at dawn. Every step uses things described in the other articles.")
        {
            Steps =
            [
                "Add target: press + Add target and enter the coordinates, or add the target from Framing.",
                "Add the L block: press + Imaging block, open it and set the filter to L, the exposure and the number of frames.",
                "Duplicate the block three times with the copy button on its row. Change the filter of the copies to R, G and B.",
                "Add dither: open a block and turn on Dither every 3 exposures in its automation. Do the same for the others.",
                "Add autofocus: in the automation of the first block choose Autofocus at the start and after a filter change.",
                "Add a dawn limit: select the target and tick Dawn in its limits.",
                "Press Run. The status bar at the bottom says what the session is doing and for how long.",
            ],
            Notes =
            [
                "Filters are chosen by their slot of the filter wheel and shown by their name where the wheel reports it.",
                "If you shoot all the L frames first, the focus may drift between the blocks. Autofocus after a filter change and every N minutes keeps it in step.",
            ],
            Screenshot = Shot("session-library.png", "Adding the actions of a block from the library.",
                (1, "Add an action", 0.51, 0.32), (2, "Search for Set Filter or Exposure", 0.79, 0.267)),
            Related = ["blocks", "automation", "limits", "filters"],
            Keywords = ["lrgb", "luminance", "tutorial", "filters", "worked example"],
        },
    ];
}
