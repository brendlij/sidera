using System.Collections.Generic;
using Sidera.Desktop.ViewModels;

namespace Sidera.Desktop.Help;

public static partial class HelpCatalog
{
    private static IEnumerable<HelpArticle> ObservatoryArticles() =>
    [
        new("domes", ObservatorySection, "Domes and Roll-off Roofs", "A dome (or a roll-off roof) belongs to the observatory, not to an imaging setup. Sidera talks to it over ASCOM Alpaca, on the network.")
        {
            Steps =
            [
                "Open Equipment → Domes and choose ASCOM Alpaca (network) as the driver.",
                "Enter the host (a name or an address), the port of the Alpaca server and the number of the device, 0 for the first dome. Or press Ask this host to see the domes of that host, and choose one.",
                "Press Use this dome. This sets the dome up and sends nothing: nothing is connected and nothing moves.",
                "Press Connect. The page reads what the device supports and shows only that.",
                "Open and Close move the shutter and wait until the dome reports it open or closed. Turn, Park and Find home appear for a dome that can do them. Halt stops everything that moves.",
            ],
            Notes =
            [
                "Ask this host sends one discovery message to that host only. Search the network sends one broadcast message to your local network, and only when you press that button.",
                "A roll-off roof is a dome with a shutter and no azimuth. It is set up the same way and shows only the shutter and Halt.",
                "Opening the shutter of real equipment asks every time. Turning, parking and going home ask once per dome. Closing and halting never ask.",
                "A shutter that is stopped halfway reports an error in most drivers. It can then be closed, not opened.",
                "While it is connected the state is read every two seconds, so a roof that closes by its own rain sensor shows as closed. A dome that stops answering is shown as faulted.",
                "Port 11111 is the usual port of an ASCOM Remote server; some simulators use 32323.",
            ],
            Screenshot = Shot("equipment-domes.png", "Equipment → Domes, with the Alpaca driver chosen.",
                (1, "The Alpaca driver", 0.60, 0.282), (2, "Host, port and device number", 0.60, 0.355), (3, "Use this dome", 0.185, 0.385), (4, "Ask this host", 0.29, 0.385), (5, "Search the network", 0.375, 0.385)),
            Actions = [Equipment("Open Domes", EquipmentSection.Domes)],
            Related = ["dome-actions", "dome-wont-connect", "equipment-setup"],
            Keywords = ["dome", "roof", "roll-off", "shutter", "alpaca", "observatory", "ascom remote"],
        },

        new("dome-actions", ObservatorySection, "Dome Actions in a Session", "A session can open the dome at the start and close it at the end.")
        {
            Steps =
            [
                "In Start add Open Dome. In End add Close Dome. They are in the group Dome of the library of actions.",
                "Turn Dome has an azimuth in degrees (north is 0, east 90). Park Dome and Dome Home are there too.",
                "Run the session. A session that opens a real shutter asks before it runs, every time.",
            ],
            Notes =
            [
                "The dome actions work with the domes of the observatory, not with an imaging setup.",
                "A roof refuses Turn, Park and Home when the step runs, because it has no azimuth.",
                "A dome that is not connected keeps the run from starting: the Run button says Connect … to run the sequence.",
                "An Open Dome waits until the dome reports the shutter open, and fails if it does not start to move or reports an error.",
            ],
            Screenshot = Shot("dome-workspace.png", "A connected dome (the simulator).",
                (1, "The state of the dome", 0.19, 0.43), (2, "The shutter", 0.52, 0.435), (3, "Turn, park and home", 0.19, 0.745), (4, "Halt", 0.52, 0.745)),
            Actions = [Page("Open Session", AppPage.Session), Equipment("Open Domes", EquipmentSection.Domes)],
            Related = ["domes", "actions", "sequencer-basics"],
            Keywords = ["open dome", "close dome", "park dome", "roof", "shutter"],
        },

        new("observing-site", ObservatorySection, "Observing Site", "Where you observe. Sidera needs it for altitudes, darkness and the meridian.")
        {
            Steps =
            [
                "Open Settings → Observatory.",
                "Enter the latitude and the longitude: north and east are positive, or write N, S, E and W. Enter the elevation in meters. The name is optional.",
                "Press Save site.",
            ],
            Notes =
            [
                "Nothing is known about the site until you enter it. Calculations that need a location say so.",
                "When a mount connects its site is compared with yours. A difference of more than 100 m asks what to do: use the location of the mount, send yours to the mount, or keep both. Nothing is written to a mount by itself.",
            ],
            Actions = [Setting("Open Observatory settings", "observatory")],
            Related = ["meridian-flip", "limits", "repeat"],
            Keywords = ["site", "latitude", "longitude", "location", "elevation"],
        },
    ];

    private static IEnumerable<HelpArticle> TroubleshootingArticles() =>
    [
        new("camera-wont-connect", TroubleshootingSection, "Camera Does Not Connect", "The camera stays Disconnected, or Connect ends with a message.")
        {
            Problem = new("The camera does not connect.", "No driver is chosen, the driver is not installed, or another program holds the camera. Most camera drivers allow one program at a time.",
            [
                "Equipment → Cameras: is a driver chosen? Press Rescan if the list is empty.",
                "Press Setup and look at the dialog of the driver itself.",
                "Close the other programs that use the camera: another imaging program or a hub.",
                "To try Sidera without hardware choose Simulator.",
                "The reason is shown on the page of the device, and in the message that stays at the top right until you close it.",
            ]),
            Actions = [Page("Open Equipment", AppPage.Equipment), Page("Open Diagnostics", AppPage.Diagnostics)],
            Related = ["equipment-setup", "logs-and-diagnostics"],
            Keywords = ["connect", "driver", "ascom", "disconnected", "error"],
        },

        new("focuser-missing", TroubleshootingSection, "Focuser Missing", "Autofocus or the session says it has no focuser.")
        {
            Problem = new("No focuser configured. Give the setup a focuser on the Equipment page.", "The imaging setup of the sequence has no focuser, or the focuser is not connected.",
            [
                "Equipment → Focusers: choose the driver of the focuser and press Connect.",
                "If you use imaging setups, make sure the focuser belongs to the setup of the camera.",
                "The start button of the autofocus says what is missing: Connect the focuser of … on the Equipment page.",
            ]),
            Actions = [Page("Open Equipment", AppPage.Equipment)],
            Related = ["autofocus", "autofocus-cannot-start"],
            Keywords = ["focuser", "no focuser", "autofocus"],
        },

        new("plate-solve-fails", TroubleshootingSection, "Plate Solve Fails", "The solve ends without a position.")
        {
            Problem = new("ASTAP found no solution, did not find enough stars, or ran out of time.", "The frame is out of focus or too short, there are clouds, the hint is far off, or the focal length that Sidera uses is wrong.",
            [
                "Look at the frame: it needs round stars. Use a longer exposure or focus first.",
                "Check the focal length on the camera page. The result compares the solved scale with the expected one; a large difference points at the focal length.",
                "Allow the blind fallback and raise the search radius and the timeout in Settings → Plate solving.",
                "A solve that says The solve ran out of its … s was ended: raise the timeout.",
                "ASTAP could not read the image: capture the frame again.",
            ]),
            Actions = [Setting("Open Plate Solving settings", "platesolving"), Page("Open Plate Solve", AppPage.PlateSolve)],
            Related = ["plate-solving", "astap-database", "slew-center"],
            Keywords = ["solve", "astap", "stars", "no solution", "timeout"],
        },

        new("astap-database", TroubleshootingSection, "ASTAP Database Not Found", "Plate solving says ASTAP or its star database was not found.")
        {
            Problem = new("ASTAP has no star database, or ASTAP was not found.", "ASTAP is not installed, it is not where Sidera looks, or no star database such as D50 is installed. Sidera installs neither.",
            [
                "Install ASTAP and a star database (for example D50) from the ASTAP download page.",
                "Settings → Plate solving: leave the ASTAP executable empty to look in the usual places, or press Browse.",
                "Set the Database folder if the database is somewhere else.",
                "Press Refresh detection. It says ASTAP installed (…) and Star database: D50 when all is well.",
            ]),
            Actions = [Setting("Open Plate Solving settings", "platesolving")],
            Related = ["plate-solving", "plate-solve-fails"],
            Keywords = ["astap", "d50", "database", "not found", "install"],
        },

        new("guiding-unavailable", TroubleshootingSection, "PHD2 Not Connected, Guiding Unavailable", "The guider does not connect, or guiding does not start.")
        {
            Problem = new("Could not connect to PHD2, or The equipment of PHD2 (guide camera and mount) is not connected.", "PHD2 is not running, its server is not enabled, the host or the port is wrong, or PHD2's own equipment is not connected.",
            [
                "Start PHD2. In PHD2 enable its server (Tools → Enable Server).",
                "Equipment → Guiders: check the host and the port. 127.0.0.1 and 4400 is the first PHD2 on this computer; a second one uses 4401.",
                "Connect the guide camera and the mount in PHD2, then press Start Guiding in Sidera.",
                "If the connection was lost, press Connect again.",
            ]),
            Actions = [Page("Open Equipment", AppPage.Equipment), Setting("Open Guiding settings", "guiding")],
            Related = ["guiding", "dithering"],
            Keywords = ["phd2", "guider", "connection lost", "guiding"],
        },

        new("autofocus-cannot-start", TroubleshootingSection, "Autofocus Cannot Start", "The Start Autofocus button is disabled or says what is missing.")
        {
            Problem = new("The autofocus does not start.", "A device of the setup is not connected, there is no setup to work with, or a value is not valid.",
            [
                "Read the line under the button: Connect the camera of … or Connect the focuser of … on the Equipment page.",
                "With several cameras choose the imaging setup in the sidebar, or create one.",
                "Exposure must be more than 0 seconds. Step size and samples must be whole numbers.",
                "An autofocus that is already running has to end, or be cancelled, first.",
            ]),
            Actions = [Page("Open Imaging", AppPage.Imaging), Page("Open Equipment", AppPage.Equipment)],
            Related = ["autofocus", "focuser-missing", "multiple-imaging-paths"],
            Keywords = ["autofocus", "disabled", "start"],
        },

        new("meridian-flip-disabled", TroubleshootingSection, "Meridian Flip Disabled", "The session says Meridian Flip Off.")
        {
            Problem = new("Meridian Flip shows Off · follows Settings → Meridian Flip.", "The flip is switched off in the application defaults, and the session follows them. It is off until you switch it on.",
            [
                "Settings → Meridian Flip: tick Flip at the meridian and press Save.",
                "Or press Edit next to Meridian Flip in the session and set it for this session only.",
                "The flip needs a target with coordinates, your observing site and a connected mount.",
            ]),
            Actions = [Setting("Open Meridian Flip settings", "meridianflip"), Setting("Open Observatory settings", "observatory"), Page("Open Session", AppPage.Session)],
            Related = ["meridian-flip", "observing-site"],
            Keywords = ["flip", "off", "disabled", "meridian"],
        },

        new("dome-wont-connect", TroubleshootingSection, "Dome Does Not Connect", "The Alpaca dome cannot be reached.")
        {
            Problem = new("The Alpaca server at … could not be reached, did not answer within 10 seconds, or did not answer like an Alpaca server.", "The host, the port or the device number is wrong, the Alpaca server is not running, a firewall is in the way, or the address is not an Alpaca server at all.",
            [
                "Check the host, the port and the device number on Equipment → Domes.",
                "Press Ask this host: it lists the domes that the server has, and fills in the address when you choose one.",
                "Make sure the Alpaca server (the controller of the dome, or ASCOM Remote) is running and that this computer can reach it.",
                "An answer that does not look like Alpaca means that something else listens on that port.",
                "The message stays at the top right until you close it; the same text is on the page of the device.",
            ]),
            Actions = [Equipment("Open Domes", EquipmentSection.Domes)],
            Related = ["domes", "logs-and-diagnostics"],
            Keywords = ["alpaca", "dome", "roof", "connect", "network", "unreachable"],
        },

        new("rotator-calibration", TroubleshootingSection, "Rotator Calibration Missing", "Rotating to a sky angle says the rotator has not been calibrated.")
        {
            Problem = new("Sidera does not know how the position of this rotator relates to the sky in the image.", "The relation between the position of the rotator and the rotation of the sky depends on how the camera sits on the rotator. It is measured once, with a plate solve.",
            [
                "Add the rotator to an imaging setup (on the camera page).",
                "Connect the rotator and the camera, and make sure plate solving is set up.",
                "Press Calibrate with a plate solve on the rotator page. Do it again after the camera was turned on the rotator.",
            ]),
            Actions = [Page("Open Equipment", AppPage.Equipment), Setting("Open Plate Solving settings", "platesolving")],
            Related = ["rotation", "plate-solving"],
            Keywords = ["rotator", "calibrate", "angle"],
        },

        new("multiple-imaging-paths", TroubleshootingSection, "Multiple Imaging Paths Detected", "A page says: Multiple imaging paths are available. Create or choose an Imaging Setup.")
        {
            Problem = new("Multiple imaging paths are available. Create or choose an Imaging Setup.", "There are several cameras and no imaging setup. Sidera never guesses which one you mean.",
            [
                "Create an imaging setup for the camera you want to work with, on the Imaging page or in Equipment → Imaging Setups.",
                "With several setups choose the current one in the sidebar.",
                "An existing session keeps referring to its camera: making a setup for a camera changes nothing in it.",
            ]),
            Actions = [Equipment("Open Imaging Setups", EquipmentSection.ImagingSetups), Page("Open Imaging", AppPage.Imaging)],
            Related = ["equipment-setup", "multi-camera"],
            Keywords = ["imaging setup", "several cameras", "ambiguous"],
        },

        new("sequence-wont-run", TroubleshootingSection, "The Sequence Does Not Run", "Run is disabled, and a line says why.")
        {
            Problem = new("The Run button is disabled.", "A device the session uses is not connected or is busy, or the session has a problem.",
            [
                "Connect … to run the sequence: connect that device on the Equipment page (cameras, mounts, guiders and domes the session uses).",
                "Wait for the camera to finish its exposure, for the mount to finish slewing, for the guider to finish its operation, or for the dome to finish moving.",
                "A target, a block or an action with a problem says what is wrong where it is; fix it, and the line goes away.",
            ]),
            Actions = [Page("Open Session", AppPage.Session), Page("Open Equipment", AppPage.Equipment)],
            Related = ["first-session", "sequencer-basics", "dome-actions"],
            Keywords = ["run", "disabled", "cannot start", "readiness"],
        },
    ];

    private static IEnumerable<HelpArticle> ReferenceArticles() =>
    [
        new("keyboard-shortcuts", ReferenceSection, "Keyboard Shortcuts", "Sidera has few shortcuts. These are all of them.")
        {
            Steps =
            [
                "Library of actions: type to search, Down moves to the list, Enter adds the chosen action, Escape closes the library.",
                "Session page: Escape clears the selection.",
                "Messages: Tab reaches a message at the top right, Escape closes the one that has the focus.",
                "Image viewer: the wheel zooms around the pointer, dragging pans.",
                "Mount pad: hold the button to move, let go to stop. Stop is always there.",
            ],
            Related = ["actions", "manual-capture"],
            Keywords = ["keys", "hotkeys", "escape", "enter", "zoom"],
        },

        new("file-locations", ReferenceSection, "File Locations", "Where Sidera keeps what it keeps.")
        {
            Steps =
            [
                "Settings and equipment: two files in the folder %APPDATA%\\Sidera. Settings → Advanced shows the exact files.",
                "Logs: %LOCALAPPDATA%\\Sidera\\logs. The Diagnostics page shows the folder and the file of this session.",
                "Sessions and sequences: the .astraseq files that you save, wherever you choose.",
                "Saved frames: the folder of Settings → Imaging.",
                "Sky survey pictures: the cache folder of Settings → Framing.",
            ],
            Notes =
            [
                "The variables SIDERA_SETTINGS_FILE and SIDERA_EQUIPMENT_FILE point Sidera at other files for settings and equipment.",
                "Whether a device is connected is never stored.",
            ],
            Actions = [Setting("Open Advanced settings", "advanced"), Page("Open Diagnostics", AppPage.Diagnostics)],
            Related = ["logs-and-diagnostics"],
            Keywords = ["settings file", "equipment file", "astraseq", "folder", "path", "appdata"],
        },

        new("logs-and-diagnostics", ReferenceSection, "Logs & Diagnostics", "The log of this session and the facts you quote in a bug report.")
        {
            Steps =
            [
                "Open Diagnostics. It shows the session, the logging level, the version and the current log file.",
                "Open log folder shows the folder. Copy path puts the path of the log file on the clipboard.",
                "Warnings and errors lists how many there were and the latest five. The log file has all of them, with their details.",
                "Recent messages lists the last ten messages that were said at the top right, also those that were closed.",
            ],
            Notes =
            [
                "Attach the log file to a bug report. A failed run offers Open Diagnostics on its message.",
                "The execution id in the message of a failed run finds that run in the log.",
            ],
            Screenshot = Shot("diagnostics.png", "The Diagnostics page."),
            Actions = [Page("Open Diagnostics", AppPage.Diagnostics)],
            Related = ["file-locations", "messages"],
            Keywords = ["log", "bug", "report", "version", "errors"],
        },

        new("messages", ReferenceSection, "Messages", "What happens is said at the top right of the window.")
        {
            Steps =
            [
                "A message slides in over the page and never moves it. It has a word (Success, Info, Warning, Error), an icon and a short text.",
                "Success and info close after about four seconds, a warning after eight. An error stays until you close it.",
                "Pointing at a message holds the clock. The cross closes it; a button on it (Open Diagnostics) does what it says.",
                "At most three are shown; the others wait. The same message while it is shown counts up (×3).",
            ],
            Notes =
            [
                "A value you typed wrongly is told next to its field. The status bar says what Sidera is doing now.",
                "Each severity has its own icon and word, so Red Night, which has one colour, can still be read.",
            ],
            Related = ["logs-and-diagnostics", "themes"],
            Keywords = ["toast", "banner", "notification", "error", "success"],
        },

        new("themes", ReferenceSection, "Themes", "Five looks for the application. A theme changes colours and nothing else.")
        {
            Steps =
            [
                "Open Settings → Appearance and choose a theme: Sidera Dark, Midnight, Graphite, Red Night or Light.",
                "The theme is shown as soon as you choose it. Save keeps it for the next start; Discard goes back to the saved one.",
            ],
            Notes =
            [
                "Red Night uses black and red only, to keep your eyes dark-adapted at the telescope.",
                "Light is for the day. Images and the sky map stay on a dark surface.",
            ],
            Screenshot = Shot("settings-appearance.png", "Settings → Appearance."),
            Actions = [Setting("Open Appearance settings", "appearance")],
            Related = ["messages"],
            Keywords = ["dark", "light", "red", "night vision", "colour", "color"],
        },

        new("about", ReferenceSection, "About Sidera", "An early-stage desktop application for Windows for astrophotography sequencing, built around several cameras, automation and a fast workflow.")
        {
            Steps =
            [
                "Sidera runs sequences across one or more imaging setups and coordinates the devices they share: mount, guider and focusers.",
                "It works with the ASCOM Platform for real equipment, with ASCOM Alpaca for domes, with PHD2 for guiding, with ASTAP for plate solving, and with built-in simulators.",
                "The version is on the Diagnostics page.",
            ],
            Notes =
            [
                "Camera and focuser have been validated against real hardware. The movement of a real mount, a real guided session and a real dither are implemented and tested against simulators but not yet verified on real equipment.",
                "A dome or roof is tested against an Alpaca simulator; test your own with the shutter closed first.",
                "An ASCOM filter wheel is tested against a fake driver only. A filter wheel cannot be stopped: cancelling waits until it has stopped turning.",
            ],
            Actions = [Page("Open Diagnostics", AppPage.Diagnostics)],
            Related = ["getting-started", "logs-and-diagnostics"],
            Keywords = ["version", "status", "license", "about"],
        },
    ];
}
