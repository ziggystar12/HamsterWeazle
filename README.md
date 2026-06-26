# HamsterWeazle

**HamsterWeazle** is a clean, modern GUI for [GreaseWeazle](https://github.com/keirf/greaseweazle), built to make reading, writing, and managing floppy disk images easier — on Windows and macOS.

GreaseWeazle is powerful, but working with floppy disks can involve multiple tools, command-line steps, firmware utilities, and separate disk-image viewers. HamsterWeazle brings the common workflow together in one easy-to-use place.

Built by **John Swiderski** / [Mean Hamster Software](https://meanhamster.com)

![HamsterWeazle](HamsterWeazle/Assets/ScreenShot.png)

---

## Installation

### Windows

1. Download `HamsterWeazle-Windows.exe` from the [HamsterWeazle product page](https://meanhamster.com/products/hamsterweazle)
2. Run it

### macOS

1. Download `HamsterWeazle-Mac-AppleSilicon.zip` (M1/M2/M3/M4) or `HamsterWeazle-Mac-Intel.zip` from the [HamsterWeazle product page](https://meanhamster.com/products/hamsterweazle)
2. Unzip and drag **HamsterWeazle.app** to your Applications folder
3. **First launch only:** right-click → Open → Open (macOS security prompt for apps outside the App Store)

That is all. On first launch, HamsterWeazle automatically downloads and installs the GreaseWeazle host tools and HxCFloppyEmulator. HamsterWeazle app updates are delivered from meanhamster.com.

See [USAGE.md](USAGE.md) for a step-by-step guide.

---

## What you need

**Windows**
- Windows 10/11 x64
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (free, one-time install)
- A [GreaseWeazle](https://github.com/keirf/greaseweazle) USB adapter and a floppy drive

**macOS**
- macOS 12 or later (Intel or Apple Silicon)
- A [GreaseWeazle](https://github.com/keirf/greaseweazle) USB adapter and a floppy drive
- GreaseWeazle host tools installed via: `pipx install greaseweazle`

---

## What it does

GreaseWeazle reads and writes floppy disks at the raw magnetic flux level, supporting dozens of vintage computer formats. HamsterWeazle puts a clean interface on top so you can get straight to imaging disks.

- **Read** a physical floppy to an image file
- **Auto Read** an unknown disk — probes common formats, picks the best match automatically
- **Write** an image file back to a floppy
- **Erase** disks
- **Browse** disk image file contents with HxCFloppyEmulator integration
- **Update** GreaseWeazle device firmware and run drive cleaning tools
- **Repeat** previous write jobs from the Write Queue with one click
- **Archive** read images automatically in the Inbox folder
- **Select** PC cable A:/B: or Shugart DS0-DS3 drives without leaving the Advanced panel
- **Save** tested read/write settings as named presets for quick reuse

---

## Features

### Auto Read

Click **Auto Read** and HamsterWeazle probes the disk with multiple common formats, scores each by sectors decoded, picks the best match, and starts the full read — no guessing required.

### Format auto-detection when writing

| Extension / Size | Format detected |
|---|---|
| `.adf` | Amiga AmigaDOS 880 KB or HD |
| `.d64` / `.d71` / `.d81` | Commodore 1541 / 1571 / 1581 |
| `.st` / `.msa` | Atari ST 360/720/1440 KB |
| `.scp` / `.hfe` / `.kf` | Raw flux (no --format flag needed) |
| `.img` by size | IBM PC 160 KB through 2.88 MB |
| 901,120 bytes | Amiga AmigaDOS DD |

### Write Queue and Inbox

The **Write Queue** remembers every previous write job, including vendor, format, image path, drive selection, and write options. One click restores and repeats any past job. The **Inbox** archives every disk you read, organised automatically with rename and delete.

### Drive selection

The **Advanced** panel keeps drive selection compact: use **Auto** for normal setups, **PC cable A:/B:** for standard dual-drive cables, or **Shugart DS0-DS3** for multi-drive Shugart configurations.

### Presets

Save known-good Advanced settings as named presets, then apply them before a read or write. Presets store vendor, format, drive selection, cylinder range, retries, revolutions, and verify settings without tying them to a specific image file.

### COM port / device selection

HamsterWeazle auto-detects the GreaseWeazle device. A dropdown in the title bar lets you override it if needed.

### Automatic setup and updates

HamsterWeazle manages GreaseWeazle host tools, HxCFloppyEmulator, and its own updates automatically.

### Themes

Dark and Amiga Workbench themes. Settings > Theme.

---

## Supported formats

Amiga, IBM PC (all densities), Apple II, Atari 8-bit, Atari ST, Commodore, Macintosh, MSX, ZX Spectrum, Sega, Acorn, DEC, and many more.

---

## Third-party software

- [GreaseWeazle host tools](https://github.com/keirf/greaseweazle) by Keir Fraser (The Unlicense)
- [HxCFloppyEmulator](https://github.com/jfdelnero/HxCFloppyEmulator) by Jean-Francois Del Nero (GPL v3)

---

## License

Copyright (c) 2026 Mean Hamster Software - John Swiderski. All rights reserved.

HamsterWeazle is free to download and use for personal and non-commercial purposes. Modification and redistribution are not permitted without written permission from the copyright holder.

---

## Disclaimer

HamsterWeazle interfaces indirectly with floppy disk hardware via GreaseWeazle at the raw magnetic flux level. While every effort has been made to make it safe and reliable, the authors accept no responsibility for data loss, damaged media, hardware faults, or any other issues that may arise from its use.

Always keep backup copies of disk images you care about. Floppy disks are fragile, and decades-old media may be unreliable regardless of the software used. This software is provided as-is, without warranty of any kind.
