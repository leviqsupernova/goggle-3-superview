# Goggle 3 SuperView

DJI Goggles 3 USB viewer for the superflash bench (Windows x64). Fork of [knpuma/goggle-3-liveview](https://github.com/knpuma/goggle-3-liveview) (MIT); standalone, no merging with upstream.

Watches the goggles feed over USB (RNDIS) and records MP4 locally. Independent community project, not affiliated with or endorsed by DJI.

## Download

The Windows zip is on the [Releases](https://github.com/leviqsupernova/goggle-3-superview/releases) page. Extract the whole zip and open `Goggle 3 LiveView.exe`.
superflash starts it with `--connect`: it connects by itself and keeps reconnecting.

## Connect

1. Connect the Goggles 3 with a USB data cable.
2. On the goggles: Settings > About > **OTG Wired Connection**, and enable liveview sharing.
3. Open the app and click **Connect**. The first time, it asks before it requests administrator permission to set up the goggles USB network adapter (only that adapter).

## Build

```powershell
dotnet test Windows/Goggles3.Core.Tests
./Windows/scripts/Publish.ps1 -Runtime win-x64 -Zip
```

Details: [Windows/README.md](Windows/README.md). Project plan and status: [CLAUDE.md](CLAUDE.md).

## License

MIT, see [LICENSE](LICENSE). FFmpeg ships as a separate LGPL program, see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
