# HamsterWeazle

A clean, modern Windows GUI for the [GreaseWeazle](https://github.com/keirf/greaseweazle) floppy disk imaging tool.

Built by **John Swiderski** / [Mean Hamster Software](https://meanhamster.com)

---

## What it does

GreaseWeazle is a USB hardware adapter that reads and writes floppy disks at the raw magnetic flux level, supporting dozens of vintage computer formats. HamsterWeazle wraps the `gw.exe` command-line host tools in a clean Windows GUI so you can read and write disks without memorising command-line syntax.

---

## Requirements

- Windows 10/11 x64
- [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (Desktop Runtime)
- GreaseWeazle hardware + host tools (`gw.exe`)

---

## Quick start

1. **Get GreaseWeazle host tools** — either run HamsterWeazle and use the built-in downloader (first launch will offer this), or download manually from [github.com/keirf/greaseweazle/releases](https://github.com/keirf/greaseweazle/releases)
2. **Place `HamsterWeazle.exe`** in the same folder as `gw.exe` — it auto-detects it on startup
3. **Connect** your GreaseWeazle USB adapter and floppy drive
4. **Select an operation**: Read, Write, Erase, Tools, or Info
5. **Choose the disk format** matching your target computer (e.g. `ibm.1440` for PC 1.44 MB, `amiga.amigados` for Amiga)
6. **Set a file path** or use Browse — format is auto-detected from the file extension and size when writing
7. **Click RUN** — output streams live to the log panel

---

## Features

- **Read** floppies to image files (`.img`, `.adf`, `.hfe`, `.scp`, and more)
- **Write** image files to blank floppies
- **Erase** floppy disks
- **Tools** tab: Update device firmware, Floppy Drive Cleaner mode
- **Info** tab: Show connected device and firmware information
- **Format auto-detect**: pick a `.adf` file and `amiga.amigados` selects itself
- **Write Queue**: every write is remembered — one click to repeat any past job
- **Inbox folder**: reads default-save here, browse your archive from the sidebar
- **Auto-update**: checks GitHub for new releases of both HamsterWeazle and gw.exe
- **First-run setup**: downloads and installs GreaseWeazle host tools automatically
- **Dark and Amiga Workbench themes** (Settings > Theme)
- **Session restore**: reopens exactly where you left off

---

## Supported disk formats (via GreaseWeazle)

Acorn, Akai, Amiga, Apple II, Atari 8-bit, Atari ST, CoCo/Dragon, Commodore, DEC, Ensoniq, Epson, HP, IBM PC, Kaypro, Mac, Micropolis, MSX, NEC PC-98, North Star, Olivetti, Sega, Thomson, TSC, Xerox, ZX Spectrum, and more.

---

## Advanced options

Expand the **Advanced** panel to set:

| Option | Default | Notes |
|---|---|---|
| Cylinder range | 0–79 | Limit tracks to write — useful for partial images |
| Retries | 3 | Error retries per track. Lower = faster but riskier |
| Verify after write | Off | Read back each track to confirm write |
| Drive | Auto | Select A: or B: for dual-drive setups |

---

## Building from source

```
git clone https://github.com/ziggystar12/HamsterWeazle
cd HamsterWeazle\HamsterWeazle
dotnet build
```

To publish a single-file release exe:
```
dotnet publish -c Release -o ..\dist
```

Requires .NET 10 SDK.

---

## License

See [LICENSE](LICENSE) for terms.

---

## Acknowledgements

- [Keir Fraser](https://github.com/keirf) for the GreaseWeazle hardware and host tools
- [Mean Hamster Group](https://meanhamster.com) for HamsterOS
