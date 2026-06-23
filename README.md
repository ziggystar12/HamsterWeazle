# HamsterWeazle

**HamsterWeazle** is a clean, modern Windows GUI for [GreaseWeazle](https://github.com/keirf/greaseweazle), built to make reading, writing, and managing floppy disk images easier.

GreaseWeazle is powerful, but working with floppy disks can involve multiple tools, command-line steps, firmware utilities, and separate disk-image viewers. HamsterWeazle brings the common workflow together in one easy-to-use place.

Built by **John Swiderski** / [Mean Hamster Software](https://meanhamster.com)

![HamsterWeazle](HamsterWeazle/Assets/ScreenShot.png)

---

## Why HamsterWeazle?

The goal of HamsterWeazle is simple: make GreaseWeazle easier to use.

Instead of jumping between separate programs and command-line utilities, HamsterWeazle gives you one place to read disks, write images, erase media, update tools, and inspect disk image contents.

---

## Installation

1. Download `HamsterWeazle.exe` from the [latest release](https://github.com/ziggystar12/HamsterWeazle/releases/latest)
2. Run it

That is all. On first launch, HamsterWeazle automatically downloads and installs the GreaseWeazle host tools. It will also offer to download [HxCFloppyEmulator](https://github.com/jfdelnero/HxCFloppyEmulator) for browsing disk image contents. Everything stays up to date automatically.

See [USAGE.md](USAGE.md) for a step-by-step guide.

---

## What you need

* Windows 10/11 x64
* [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (free, one-time install)
* A [GreaseWeazle](https://github.com/keirf/greaseweazle) USB adapter
* A compatible floppy drive

---

## What it does

* **Read** a physical floppy to an image file
* **Auto Read** an unknown disk — probes 6 common formats, picks the best match automatically
* **Write** an image file back to a floppy
* **Erase** disks
* **Browse** disk image file contents with HxCFloppyEmulator integration
* **Update** GreaseWeazle device firmware and run drive cleaning tools
* **Repeat** previous write jobs from the Write Queue with one click
* **Archive** read images automatically in the Inbox folder

---

## Features

### Auto Read

Click **Auto Read** on the Read tab and HamsterWeazle probes the disk with 6 common formats, scores each by how many sectors it successfully decodes, picks the best match, and starts the full read — no guessing required.

### Format auto-detection when writing

| Extension / Size                 | Format detected                                       |
| -------------------------------- | ----------------------------------------------------- |
| `.adf`                           | Amiga AmigaDOS 880 KB or AmigaDOS HD                  |
| `.d64`                           | Commodore 1541                                        |
| `.d71`                           | Commodore 1571                                        |
| `.d81`                           | Commodore 1581                                        |
| `.st` / `.msa`                   | Atari ST 360 KB, 720 KB, or 1440 KB                   |
| `.img` / `.ima` / `.dsk` by size | IBM PC 160/180/320/360/720/800/1200/1440/1680/2880 KB |
| 901,120 bytes                    | Amiga AmigaDOS DD                                     |

### Disk image browsing

**List Files** shows the full directory tree of any supported disk image directly in the log panel — including standard `.img` files, which are automatically converted internally before listing. **Open HxC** launches the full HxCFloppyEmulator GUI for interactive browsing, extraction, and editing.

### Write Queue and Inbox

The **Write Queue** remembers every previous write job. One click repeats any past job without re-selecting the file or format. The **Inbox folder** archives every disk you read, organised automatically.

### Automatic setup and updates

HamsterWeazle manages GreaseWeazle host tools, HxCFloppyEmulator, and its own updates automatically. Check for updates any time from Settings.

### COM port selection

HamsterWeazle auto-detects the GreaseWeazle device in most cases. If you have multiple USB devices or need to target a specific port, a **COM port dropdown** in the title bar lets you override it instantly.

### Themes and session restore

Dark and Amiga Workbench themes (Settings > Theme). Full session restore — reopens exactly where you left off.

---

## Supported formats

Amiga, IBM PC (all densities), Apple II, Atari 8-bit, Atari ST, Commodore, Macintosh, MSX, ZX Spectrum, Sega, Acorn, DEC, and many more — via the GreaseWeazle host tools.

---

## Third-party software

* [GreaseWeazle host tools](https://github.com/keirf/greaseweazle) by Keir Fraser (The Unlicense)
* [HxCFloppyEmulator](https://github.com/jfdelnero/HxCFloppyEmulator) by Jean-Francois Del Nero (GPL v3)

---

## License

Copyright (c) 2026 Mean Hamster Software - John Swiderski. All rights reserved.

HamsterWeazle is free to download and use for personal and non-commercial purposes. Modification and redistribution are not permitted without written permission from the copyright holder.

---

## Disclaimer

HamsterWeazle interfaces indirectly with floppy disk hardware via GreaseWeazle at the raw magnetic flux level. While every effort has been made to make it safe and reliable, the authors accept no responsibility for data loss, damaged media, hardware faults, or any other issues that may arise from its use.

Always keep backup copies of disk images you care about. Floppy disks are fragile, and decades-old media may be unreliable regardless of the software used. This software is provided as-is, without warranty of any kind.
