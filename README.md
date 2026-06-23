# HamsterWeazle

A clean, modern Windows GUI for reading and writing floppy disks using the [GreaseWeazle](https://github.com/keirf/greaseweazle) hardware adapter.

Built by **John Swiderski** / [Mean Hamster Software](https://meanhamster.com)

---

## Installation

1. Download HamsterWeazle.exe from the [latest release](https://github.com/ziggystar12/HamsterWeazle/releases/latest)
2. Run it

That is all. On first launch HamsterWeazle automatically downloads and installs the GreaseWeazle host tools. It will also offer to download [HxCFloppyEmulator](https://github.com/jfdelnero/HxCFloppyEmulator) for browsing disk image contents. Everything stays up to date automatically.

---

## What you need

- Windows 10/11 x64
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (free one-time install if not already present)
- A [GreaseWeazle](https://github.com/keirf/greaseweazle) USB adapter and a floppy drive

---

## What it does

GreaseWeazle reads and writes floppy disks at the raw magnetic flux level, supporting dozens of vintage computer formats. HamsterWeazle puts a clean interface on top so you can get straight to imaging disks.

- **Read** a physical floppy to an image file
- **Write** an image file to a blank floppy
- **Erase** disks
- **Update** device firmware and run drive cleaner from the Tools tab
- **Browse** disk image file contents with HxCFloppyEmulator integration

---

## Features

### Format auto-detection
When writing, the format is detected automatically from the file. Supported auto-detection:

| Extension | Format detected |
|-----------|----------------|
| .adf | amiga.amigados (880 KB) or amigados-hd |
| .d64 | commodore.1541 |
| .d71 | commodore.1571 |
| .d81 | commodore.1581 |
| .st / .msa | atarist (360 KB, 720 KB, or 1440) |
| .img / .ima / .dsk (by size) | IBM PC 160/180/320/360/720/800/1200/1440/1680/2880 KB |
| 901,120 bytes | amiga.amigados DD |

### Everything else
- Write Queue remembers every past job - one click to repeat
- Inbox folder archives everything you read
- Dark and Amiga Workbench themes (Settings > Theme)
- Auto-update for HamsterWeazle, GreaseWeazle tools, and HxCFloppyEmulator
- Full session restore - reopens exactly where you left off
- HxCFloppyEmulator integration on every disk image: list files and open in GUI

---

## Supported formats

Amiga, IBM PC (all densities), Apple II, Atari 8-bit, Atari ST, Commodore, Mac, MSX, ZX Spectrum, Sega, Acorn, DEC, and many more.

---

## Third-party software

HamsterWeazle automatically manages these for you:

- [GreaseWeazle host tools](https://github.com/keirf/greaseweazle) by Keir Fraser (The Unlicense)
- [HxCFloppyEmulator](https://github.com/jfdelnero/HxCFloppyEmulator) by Jean-Francois Del Nero (GPL v3)

---

## License

Copyright (c) 2026 Mean Hamster Software - John Swiderski. All rights reserved.
Free to download and use for personal and non-commercial purposes.
Modification and redistribution are not permitted without written permission from the copyright holder.

---

## Disclaimer

HamsterWeazle interfaces directly with floppy disk hardware at the raw magnetic flux level. While every effort has been made to make it safe and reliable, the authors accept no responsibility for data loss, damaged media, hardware faults, or any other issues that may arise from its use.

Always keep backup copies of disk images you care about. Floppy disks are fragile and decades-old media may be unreliable regardless of the software used.

This software is provided as-is, without warranty of any kind.
