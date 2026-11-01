# Goggle 3 SuperView — Windows

Windows x64 only. Extract the **whole** ZIP and open **Goggle 3 LiveView.exe**. The .NET runtime and the video decoder are included. Portable: move the folder if needed. No service, no automatic startup.

## First USB connection

1. Connect the goggles with a USB data cable.
2. On the goggles, turn on **OTG Wired Connection** and **LiveView sharing** (the in-app **Tutorial** shows where).
3. Click **Connect**. Windows should use Microsoft's RNDIS adapter driver. **Do not replace it with WinUSB / Zadig.**

The app finds the goggles by VID `2CA3` and PID `0020`. If the adapter is not set up yet, **Connect** asks first, then asks Windows for administrator permission. It adds `192.168.60.1/24` to that adapter only. Other networks, DNS, the default gateway and the firewall are not changed. Video requests go to `192.168.60.2:9003` from local port `12346`.

A backup of the adapter's DHCP state is kept in `%ProgramData%/Goggles3Mac` (historical folder name, kept so existing backups still work). To undo the setup, open PowerShell as administrator in the app folder and run `./Prepare-Usb.ps1 -Restore`. The script refuses to change an adapter that has another static IP or an address conflict.

## Using it

- **Connect** keeps trying: if the goggles are unplugged or LiveView stops, it reconnects by itself. **Disconnect** stops that.
- **Record** saves an MP4 without sound in **Videos → Goggle 3 LiveView**. The video is copied as it arrives (no re-encoding). Click **Stop**, or close the window, and the file is finished first. A killed recording is still playable.
- **F11** toggles fullscreen, **Esc** leaves it.
- Right-click the picture for **Diagnostics**. The same lines are in `%LocalAppData%/Goggle 3 LiveView/diagnostic.txt`. Read them before sharing.

## Command line

| Option | Effect |
|---|---|
| `--connect` | Connect at launch and keep reconnecting. No dialogs. A second launch only brings the window to the front (superflash uses this). |
| `--replay C:\path\capture.h264` | Play a recorded H.264 file instead of the goggles (testing). |
| `--software-render` | Software rendering, for graphics drivers that cannot start WPF. Does not change USB. |

## Build

Needs the .NET 10 SDK. The FFmpeg binary is in the source tree.

```powershell
dotnet test Windows/Goggles3.Core.Tests
./Windows/scripts/Publish.ps1 -Runtime win-x64 -Zip     # outputs/Goggle-3-LiveView-Windows-x64(.zip)
```

On Linux the Windows app builds but cannot run: `dotnet build Windows/Goggles3.Windows -c Release -r win-x64`.
`Goggles3.Bench` (not shipped) runs fake goggles and soak tests on Linux. To rebuild FFmpeg see `scripts/build-windows-decoder.sh` and `THIRD_PARTY_NOTICES.md`.

## References

- [Microsoft RNDIS driver](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/remote-ndis--rndis-2)
- [Official DJI Goggles 3 manual](https://dl.djicdn.com/downloads/DJI_Goggles_3/DJI_Goggles_3_User_Manual_EN.pdf)
