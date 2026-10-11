# HamsterWeazle — Usage Guide

This guide describes the v1.5.10 Workbench interface, published for Windows and Apple Silicon Mac. The Intel Mac download remains v1.5.5. DMK integration is Windows-only.

---

## Workbench layout

Use the left rail for **Read**, **Write**, **Erase**, **Tools**, **Settings** and **Device**. Settings opens directly below Tools as a workspace with appearance, updates and installation paths. Changes save automatically. The main action changes to **Stop** during a job. Editable operation settings are locked until it finishes.

The right library switches between **Inbox** and **Write queue**. Click a name for image details; use **…** or right-click for image actions. Clearing history keeps the image files on disk.

Each workspace has one compact label: **READ**, **WRITE**, **ERASE**, **TOOLS** or **DEVICE**. **Advanced settings** and **Activity & command** start collapsed. Activity has a separate scrolling pane; opening it leaves the operation controls in place. Issues flag **Needs attention** without forcing the pane open. The track map remains optional and off by default. The idle status is **Ready**.

The header appearance button switches Light/Dark. **Settings > Appearance** also offers Amiga. **Buy Me a Coffee** is directly visible in the header. The header **…** menu contains the manual update check.

---

## First launch

On first launch HamsterWeazle will detect whether the GreaseWeazle host tools are installed.

* If found automatically — you are ready to go
* If not found — a setup dialog appears offering to **Download Latest** (automatic) or **Browse to Existing Installation**

HxCFloppyEmulator can be installed the same way from **Settings > Software Updates**.

---

## Reading a disk

1. Connect your GreaseWeazle adapter and floppy drive
2. Choose **Read** in the left operation rail
3. Insert the floppy disk
4. Click **Read disk** — the image saves automatically to the **Inbox folder** as a standard `.img` sector image

The generated destination is always visible. To choose another image container
or location, click the browse button beside it. Choosing a location enables
**Custom output path** in **Advanced settings**. The Save As dialog offers separate
choices for IMG, IMA, DSK, HFE, DMK, MFM, SCP, KryoFlux RAW, ADF, Apple II DO/PO,
Commodore D64/D71/D81, Atari ST/MSA, Acorn SSD/DSD, EDSK, IMD, MGT, NSI, SF7,
and XDF images.

MFM output is captured as raw flux and converted to an HxC MFM bitstream image,
so HxCFloppyEmulator must be installed. KryoFlux `.raw` output is a multi-file
track set; choosing a name such as `disk.raw` saves the set beginning with
`disk00.0.raw`. `.flx` is not offered because it is not supported by the bundled
GreaseWeazle or HxC tools.

### Auto Read — for unknown disks

If you do not know what format the disk is:

1. Click **Auto Read** instead of Read
2. HamsterWeazle probes common IBM PC, Amiga, Atari ST, Commodore, Macintosh and MSX formats, filtered by the drive profile when available
3. Each format is scored by the percentage of sectors decoded — the winner is shown in the log. Empty, failed or mismatched-header probes cannot win; a strong match requires both cylinders and all expected sides to decode completely
4. The full read runs automatically with the best-matched format

Example log output:
```
[auto read] Probing format - testing first 2 tracks with each candidate...

  IBM PC 1.44 MB HD          score:+100  (4 OK, 0 errors)  STRONG MATCH

[auto read] Strong match — skipping remaining candidates.

[auto read] Best match: IBM PC 1.44 MB HD (ibm.1440) - starting full read...
```

### Improving read quality on worn disks

Expand **Advanced** and increase **Revs** to 3-5. This reads multiple flux revolutions per track and improves accuracy on degraded media. Slower but more reliable.

### Rescue a previous read

1. Keep the original image and its `.capture-*.json` and `.txt` reports together.
2. Insert the **same floppy** and choose the correct drive and device.
3. Choose **Advanced > Rescue Read**, or right-click an Inbox image and choose **Rescue Read**.
4. Select the previous capture and confirm the disk. Rescue uses its recorded format and reads a complete new pass.
5. Review the recovered, remaining and conflicting sector counts. A separate `-rescued-...` image is created alongside the original and the new `-pass-...` capture.

Only sectors confirmed missing or unknown in the earlier report can be replaced. Valid older sectors are kept even when they contain all zeros. If two valid reads disagree, earlier data is kept and the disagreement is recorded. You can repeat rescue using the combined image and its report.

Rescue requires a matching image hash, full image size and supported flat layout: IBM PC, Amiga, Atari ST or MSX in `.img`, `.ima`, `.adf` or `.st`. It does not merge flux, DMK, Macintosh GCR or Commodore captures. Cancelled or failed new passes remain available without creating a combined image.

### Optional track map

Check **Track map** beside the main action to show track results. Uncheck it to reclaim the space. The map defaults **off** and remembers your preference. Hover over a cell for the cylinder, side, decoded sectors and attempts; symbols distinguish successful reads, retries, missing sectors, unexpected headers, writes and verified writes. Flux and DMK tools may provide no decoded map.

### Capture reports and saved logs

Reads and writes save timestamped `.capture-*.json` and `.txt` files beside the image. Erase reports are saved in the local HamsterWeazle diagnostics folder. Reports contain the command, format, timestamps, exit/cancellation state, available image hash, track results and full tool output. They are saved even with the map hidden.

The Inbox lists disk images only. Reports and logs stay out of the list; keep them beside their image so Rescue Read and View capture report can use them.

Click an image name to open its details and capture report, or use its **…** menu and choose **View capture report**. Right-click also opens image actions. Inbox renaming copies matching reports to the new image name and keeps the earlier reports. **Tools > Analyze Saved Log** opens a saved `.txt` report or GreaseWeazle `.log` file without operating the drive; the matching JSON sidecar supplies full capture details when available. For a plain log, choose its disk format before opening it.

Header or side problems are reported as possible causes to investigate, rather than a conclusive hardware diagnosis. Write verification is reported only when the tool confirms it.

---

## Writing a disk

1. Click the **Write** tab
2. Set the image file path — type it, browse, or choose **Use image for writing** from an Inbox image's **…** menu
3. Select the vendor and format — or let HamsterWeazle auto-detect it from the file
4. Insert a blank floppy
5. Click **Write**

### Format auto-detection

When you set a file path on the Write tab, HamsterWeazle checks DOS boot-sector geometry, Macintosh signatures and supported image headers before falling back to extension and size. **Auto-detected** includes the basis for the selection; hover over it to read the full explanation. You can change the format manually, but a standard flat-image write stops before operating the drive if its size or valid DOS geometry conflicts with the selection.

### Write Queue

Every write is saved to the **Write Queue** in the right sidebar. To repeat a write:

* Choose **Repeat write** from a Write Queue entry's **…** menu — HamsterWeazle restores the saved vendor, format, image path, drive selection, and write options before starting

The queue stores the file path (not a copy of the file), so if the source file has been updated it writes the latest version automatically.

---

## Tandy / TRS-80 DMK (Windows)

1. Open **Settings > Software Updates**.
2. Click **Check** beside **gw2dmk (optional)**, then **Install**.
3. For a read, select **Tandy / TRS-80** and the **dmk** format. Inbox captures use the `.dmk` extension; custom output can choose a name and location.
4. For a write, open a `.dmk` image. HamsterWeazle selects the DMK tool automatically, including when you repeat the job from the Write Queue.
5. Choose the COM port and drive selection for your connected hardware before running.

The current DMK read profile captures **one side**, intended for Tandy Model II disks. Auto Read does not probe DMK. Cylinder, retry, revolution and verify controls used by the normal GreaseWeazle workflow are not forwarded to the DMK tools; DMK uses its own capture settings.

DMK reads use `gw2dmk`; writes use `dmk2gw`. A completed write is explicitly **not verified**: the writer has no read-back verification option. Read the disk back separately to check it. Read errors and early tool failures appear in the log panel, and native plus console logs remain in the gw2dmk installation folder.

---

## Erasing a disk

1. Click the **Erase** tab
2. Insert the disk
3. Click **Erase** — all data is permanently removed

---

## Browsing and editing disk image contents

Every image in the **Inbox** and **Write Queue** has an **…** actions menu:

* **Browse DOS files** — opens the built-in browser for DOS FAT12 images, including files in subdirectories
* **List files with HxC** — lists other supported filesystems in Activity
* **Open in HxC** — launches HxCFloppyEmulator with the image loaded for interactive browsing, file extraction, editing and conversion

DOS browsing and extraction do not require HxC. Other filesystems need HxCFloppyEmulator (Settings > Software Updates > HxCFloppyEmulator > Check).

For DOS images, use **Tools > Disk Image Tools** or **Browse DOS files** in an image's actions menu. Select a file and click **Extract file** to save a new host copy. Directories are listed with their full paths.

To prepare a DOS data disk, select its capacity and click **New DOS image**, then **Add files**. Host names become DOS 8.3 names; collisions receive a distinct alias. Imports go into the root directory and a failed import batch is rolled back. **Save copy** creates a new image file and refuses to overwrite an existing file or the source image. Unsaved edits remain in memory, with a discard prompt when closing or switching images. New disks contain a FAT12 filesystem and are data disks, without a DOS operating system.

---

## Tools tab

| Tool | What it does |
|------|-------------|
| **Update Device Firmware** | Downloads and flashes the latest GreaseWeazle firmware |
| **Floppy Drive Cleaner** | Runs the drive in a zig-zag cleaning pattern — insert a cleaning disk first |
| **Disk Image Tools** | Browse/extract DOS FAT12 files, create data disks, add files and save a new copy |
| **Analyze Saved Log** | Review captured output and track results without operating hardware |

---

## Advanced options

Expand the **Advanced** panel on the Read or Write tab to access:

| Option | Default | Notes |
|--------|---------|-------|
| Preset | Default | Save and apply named sets of tested format, drive, and read/write options |
| Cyls | 0-79 | Limit the cylinder range — useful for partial images or faster test reads |
| Retries | 3 | Error retries per track. Lower = faster but riskier on worn media |
| Revs | 1 | Flux revolutions per track (Read only). Set 3-5 for unreliable disks |
| Drive | Auto | Physical drive selector: PC cable A:/B: or Shugart DS0-DS3 for multi-drive setups |
| Verify after write | On | Re-reads each track after writing to confirm accuracy |
| Custom output path | Off | Enables editing the visible Read destination; otherwise its name is generated in Inbox |

Use **Save** beside the Preset dropdown after dialing in a working setup. Presets do not store image file paths; use the **Write Queue** when you want to repeat a specific image write.

The **Drive** dropdown sends the selected GreaseWeazle drive number to `gw.exe`. Use **Auto** unless you need a specific cable select or Shugart drive-select line.

Explicit selections use `--drive=A`, `--drive=B`, or `--drive=0` through `--drive=3` in both the command preview and execution. Cleaning uses the same drive and COM-port selection. Device info is controller-wide and does not receive a drive argument.

---

## COM port selection

HamsterWeazle automatically detects the GreaseWeazle USB device in most cases. If you have multiple devices or auto-detect picks the wrong port, choose **Device** in the operation rail and use its **Device port** dropdown.

* **Auto** — let gw.exe detect the device (works for most setups)
* **COM3**, **COM6**, etc. — force a specific port

The selection is saved and applied to all commands until changed.

---

## Settings

Choose **Settings** in the left rail, directly below **Tools**. It opens in the main workspace with its own scroll area. Changes save automatically; choose another rail item to return to your disk work. Software installation temporarily locks disk operations until it finishes.

| Section | What you can do |
|---------|----------------|
| Software Updates | Check for and install updates for HamsterWeazle, gw.exe, and HxCFloppyEmulator |
| Appearance | Switch between Light, Dark and Amiga Workbench themes |
| Installation paths | View and change the paths to gw.exe and HxCFloppyEmulator |
| About HamsterWeazle | View the app version, project links and license |

---

## Troubleshooting

### gw.exe not found on startup

The setup dialog will appear. Click **Download Latest** for automatic setup, or **Browse** if you already have GreaseWeazle host tools installed elsewhere.

If tools are installed but not found: open **Settings > Installation paths** and use **Change** to point to the correct `gw.exe` location.

### Wrong COM port / device not found

1. Check **Device > Device port**
2. If it shows only Auto, click the dropdown — Windows may not have enumerated the device yet
3. Unplug and replug the GreaseWeazle USB cable
4. Reopen the dropdown — the correct port (e.g. COM6) should now appear
5. Select it

### Read errors / bad sectors

* Increase **Revs** to 3-5 in Advanced (multiple flux passes improve accuracy)
* Clean the drive heads with the **Floppy Drive Cleaner** in the Tools tab
* Try a fresh cleaning disk if available
* Use **Rescue Read** with the same floppy and a matching capture report to collect missing sectors across reads

### List Files shows nothing / format not supported

DOS FAT12 images use the built-in browser. Other images use HxC, which may require conversion to HFE internally. For unsupported or unusual filesystems, use **Open HxC**. A corrupt directory or cluster chain is reported instead of being followed indefinitely; differing FAT copies can be browsed but cannot be edited.

### Update not showing after release

Open **Settings > Software Updates** and click **Check** for each tool. HamsterWeazle checks meanhamster.com for app updates, while gw.exe and HxCFloppyEmulator still check their upstream sources. The startup check runs once per session.
