# Getting started

> Sidera is in development and not reliable for imaging sessions. Start with the simulators, and read the [status](hardware-status.md) before you connect real equipment.

## Run it

You need Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download). For real equipment you also need the [ASCOM Platform](https://ascom-standards.org/) 7 and the drivers of your devices.

```bash
git clone https://github.com/brendlij/sidera.git
cd sidera
dotnet run --project src/Sidera.Desktop
```

### A folder to copy to another PC (for example the observatory PC)

```bash
dotnet publish src/Sidera.Desktop -c Release -r win-x64 --self-contained true -o publish/Sidera-win-x64
```

The folder `publish/Sidera-win-x64` contains `Sidera.Desktop.exe` and everything it needs, including .NET. Copy the whole folder. Your settings and equipment live in `%APPDATA%\Sidera` and are not part of it.

## Try it without hardware

1. Open **Equipment**. With no devices, Sidera offers the **demo equipment**: simulated cameras, focusers, a filter wheel, a mount and a guider, grouped into imaging setups.
2. Connect the devices. Nothing moves when a device connects.
3. Open **Session**, press **+ Add target** (or choose an object in **Framing** and press **Add to Session**).
4. Set the imaging block: filter, exposure and number of frames. Add automation (dither, autofocus) and limits if you want them.
5. Press **Run**. The **Dashboard** and the status bar at the bottom show what happens.

## With your own equipment

1. **Equipment → Cameras** (and Focusers, Filter Wheels, Rotators, Mounts, Guiders, Domes): add a device and choose its driver (an installed ASCOM driver, PHD2 for a guider, or ASCOM Alpaca for a dome). Use **Setup** to open the driver's own dialog, then **Connect**.
2. **Imaging Setups**: group a camera with its focuser, filter wheel, rotator, mount and guider. Setups can share a mount and a guider. Enter the focal length of the telescope under **Optical train**.
3. **Settings → Observatory**: enter your site. Sidera never assumes 0° 0°. The meridian flip and the countdown need it.
4. **Settings → Plate Solving**: point it at ASTAP and a star database.
5. **Settings → Imaging**: choose where frames are saved ([Saving frames](saving-frames.md)).

Operations that move real equipment (a slew, a centering, running a session with a real mount or rotator, opening a dome shutter) ask you to confirm that the equipment can move safely. Cancel moves nothing.

## Where things are

| | |
|---|---|
| Settings and equipment | `%APPDATA%\Sidera\settings.json` and `equipment.json` (Settings → Advanced shows the files) |
| Logs | `%LOCALAPPDATA%\Sidera\logs` (the Diagnostics page opens the folder) |
| Saved frames | the save folder of Settings → Imaging, by default the `Sidera` folder in your Pictures |
| Sessions | `.astraseq` files, wherever you save them |

## Next

[Dashboard](dashboard.md) · [Saving frames](saving-frames.md) · [Autofocus](autofocus.md) · [Meridian flip](meridian-flip.md) · [Troubleshooting](troubleshooting.md)
