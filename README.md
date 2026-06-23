# HamsterWeazle

A clean, easy-to-use GUI for reading and writing floppy disks using the [GreaseWeazle](https://github.com/keirf/greaseweazle) hardware adapter.

Built by **John Swiderski** / [Mean Hamster Software](https://meanhamster.com)

---

## Installation

1. Download HamsterWeazle.exe from the [latest release](https://github.com/ziggystar12/HamsterWeazle/releases/latest)
2. Run it

That is all. HamsterWeazle automatically downloads and installs the GreaseWeazle host tools and HxCFloppyEmulator on first launch. It also keeps itself and all tools up to date.

---

## What you need

- Windows 10/11 x64
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (free one-time install if not already present)
- A GreaseWeazle USB adapter and a floppy drive

---

## What it does

GreaseWeazle reads and writes floppy disks at the raw magnetic flux level, supporting dozens of vintage computer formats. HamsterWeazle puts a clean interface on top so you can get straight to imaging disks.

- **Read** a physical floppy to an image file
- **Write** an image file to a blank floppy
- **Erase** disks
- **Update** device firmware and run drive cleaner from the Tools tab
- **Browse** disk image file contents with HxCFloppyEmulator integration

---

## Auto Read

Click **Auto Read** on the Read tab and HamsterWeazle will figure out the disk format automatically. It probes the disk with 6 common formats, scores each by how many sectors it successfully decodes, picks the best match, and starts the full read without you needing to know anything about the disk.

```
[auto read] Probing format - testing first 2 tracks with each candidate...

  IBM PC 1.44 MB HD          score: +4  (4 OK, 0 errors)
  IBM PC 720 KB DD           score: -2  (0 OK, 2 errors)
  Amiga 880 KB DD            score: -4  (0 OK, 4 errors)
  ...

[auto read] Best match: IBM PC 1.44 MB HD (ibm.1440) - starting full read...
```

For known formats, use the **Read** button directly with the format pre-selected.

---

## Features

- Format auto-detected from file extension and size when writing
- Read images save directly to the Inbox folder automatically
- Write Queue remembers every past job - one click to repeat
- Dark and Amiga Workbench themes (Settings > Theme)
- Auto-update for HamsterWeazle, GreaseWeazle tools, and HxCFloppyEmulator
- Full session restore - reopens exactly where you left off
- HxCFloppyEmulator integration on every disk image: list files and open in GUI
- Revolutions option (Advanced) for more reliable reads from worn disks

---

## Format auto-detection (when writing)

| Extension | Format detected |
|-----------|----------------|
| .adf | amiga.amigados (880 KB) or amigados-hd |
| .d64 | commodore.1541 |
| .d71 | commodore.1571 |
| .d81 | commodore.1581 |
| .st / .msa | atarist (360, 720, or 1440) |
| .img by size | IBM PC 160/180/320/360/720/800/1200/1440/1680/2880 KB |

---

## Supported formats

Amiga, IBM PC (all densities), Apple II, Atari 8-bit, Atari ST, Commodore, Mac, MSX, ZX Spectrum, Sega, Acorn, DEC, and many more.

---

## Third-party software

HamsterWeazle automatically manages these for you:

- [GreaseWeazle host tools](https://github.com/keirf/greaseweazle) by Keir Fraser (The Unlicense)
- [HxCFloppyEmulator](https://github.com/jfdelnero/HxCFloppyEmulator) by Jean-Francois Del Nero (GPL v3)

---

## Disclaimer

HamsterWeazle interfaces directly with floppy disk hardware at the raw magnetic flux level. While every effort has been made to make it safe and reliable, the authors accept no responsibility for data loss, damaged media, hardware faults, or any other issues that may arise from its use. Always keep backup copies of disk images you care about. This software is provided as-is, without warranty of any kind.

---

## License

Copyright (c) 2026 Mean Hamster Software - John Swiderski. All rights reserved.
Free to download and use for personal and non-commercial purposes.
Modification and redistribution are not permitted without written permission from the copyright holder.
