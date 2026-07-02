# HamsterWeazle — Usage Guide

This guide reflects HamsterWeazle v1.4.8.

---

## First launch

On first launch HamsterWeazle will detect whether the GreaseWeazle host tools are installed.

* If found automatically — you are ready to go
* If not found — a setup dialog appears offering to **Download Latest** (automatic) or **Browse to Existing Installation**

HxCFloppyEmulator can be installed the same way from **Settings > Software Updates**.

---

## Reading a disk

1. Connect your GreaseWeazle adapter and floppy drive
2. Click the **Read** tab
3. Insert the floppy disk
4. Click **Read** — the image saves automatically to the **Inbox folder** with a timestamp filename

To save somewhere else, expand **Advanced** and tick **Custom output path**.

### Auto Read — for unknown disks

If you do not know what format the disk is:

1. Click **Auto Read** instead of Read
2. HamsterWeazle probes the disk with 6 common formats (IBM PC 1.44 MB HD, IBM PC 720 KB DD, Amiga 880 KB DD, Atari ST 720 KB, IBM PC 1.2 MB HD, IBM PC 360 KB DD)
3. Each format is scored by sectors decoded vs errors — the winner is shown in the log
4. The full read runs automatically with the best-matched format

Example log output:
```
[auto read] Probing format - testing first 2 tracks with each candidate...

  IBM PC 1.44 MB HD          score: +4  (4 OK, 0 errors)
  IBM PC 720 KB DD           score: -2  (0 OK, 2 errors)
  Amiga 880 KB DD            score: -4  (0 OK, 4 errors)
  ...

[auto read] Best match: IBM PC 1.44 MB HD (ibm.1440) - starting full read...
```

### Improving read quality on worn disks

Expand **Advanced** and increase **Revs** to 3-5. This reads multiple flux revolutions per track and improves accuracy on degraded media. Slower but more reliable.

---

## Writing a disk

1. Click the **Write** tab
2. Set the image file path — type it, use **Browse**, or click any item in the **Write Queue** or **Inbox**
3. Select the vendor and format — or let HamsterWeazle auto-detect it from the file
4. Insert a blank floppy
5. Click **Write**

### Format auto-detection

When you set a file path on the Write tab, HamsterWeazle tries to detect the format automatically from the file extension and size. A green **Auto-detected: ibm.1440** indicator appears below the file path when it succeeds. You can always override it manually.

### Write Queue

Every write is saved to the **Write Queue** in the right sidebar. To repeat a write:

* Click **Run** on any Write Queue entry — HamsterWeazle restores the saved vendor, format, image path, drive selection, and write options before starting

The queue stores the file path (not a copy of the file), so if the source file has been updated it writes the latest version automatically.

---

## Erasing a disk

1. Click the **Erase** tab
2. Insert the disk
3. Click **Erase** — all data is permanently removed

---

## Browsing disk image contents (HxCFloppyEmulator)

Every image in the **Inbox** and **Write Queue** has two buttons:

* **List Files** — shows the full directory tree in the log panel. Standard `.img` files are automatically converted internally before listing, so this works for most disk images including DOS floppies
* **Open HxC** — launches the HxCFloppyEmulator GUI with the image loaded for interactive browsing, file extraction, and editing

HxCFloppyEmulator must be installed (Settings > Software Updates > HxCFloppyEmulator > Check).

---

## Tools tab

| Tool | What it does |
|------|-------------|
| **Update Device Firmware** | Downloads and flashes the latest GreaseWeazle firmware |
| **Floppy Drive Cleaner** | Runs the drive in a zig-zag cleaning pattern — insert a cleaning disk first |

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
| Verify after write | Off | Re-reads each track after writing to confirm accuracy |
| Custom output path | Off | Shows the file path field for Read (hidden by default — auto-saves to Inbox) |

Use **Save** beside the Preset dropdown after dialing in a working setup. Presets do not store image file paths; use the **Write Queue** when you want to repeat a specific image write.

The **Drive** dropdown sends the selected GreaseWeazle drive number to `gw.exe`. Use **Auto** unless you need a specific cable select or Shugart drive-select line.

---

## COM port selection

HamsterWeazle automatically detects the GreaseWeazle USB device in most cases. If you have multiple devices or the auto-detect picks the wrong port, use the **COM port dropdown** in the title bar (next to the Settings button).

* **Auto** — let gw.exe detect the device (works for most setups)
* **COM3**, **COM6**, etc. — force a specific port

The selection is saved and applied to all commands until changed.

---

## Settings

Open Settings with the **gear icon** in the title bar.

| Section | What you can do |
|---------|----------------|
| Software Updates | Check for and install updates for HamsterWeazle, gw.exe, and HxCFloppyEmulator |
| Theme | Switch between Dark and Amiga Workbench themes |
| Installation | View and change the paths to gw.exe and HxCFloppyEmulator |

---

## Troubleshooting

### gw.exe not found on startup

The setup dialog will appear. Click **Download Latest** for automatic setup, or **Browse** if you already have GreaseWeazle host tools installed elsewhere.

If tools are installed but not found: open **Settings > Installation** and use **Change** to point to the correct `gw.exe` location.

### Wrong COM port / device not found

1. Check the **COM port dropdown** in the title bar
2. If it shows only Auto, click the dropdown — Windows may not have enumerated the device yet
3. Unplug and replug the GreaseWeazle USB cable
4. Reopen the dropdown — the correct port (e.g. COM6) should now appear
5. Select it

### Read errors / bad sectors

* Increase **Revs** to 3-5 in Advanced (multiple flux passes improve accuracy)
* Clean the drive heads with the **Floppy Drive Cleaner** in the Tools tab
* Try a fresh cleaning disk if available

### List Files shows nothing / format not supported

The CLI file browser works with `.hfe`, `.adf`, `.scp` and most `.img` files. For `.img` files it automatically converts to HFE internally. If it still fails (unusual image size or non-standard format), use **Open HxC** instead — the GUI handles more formats interactively.

### Update not showing after release

Open **Settings > Software Updates** and click **Check** for each tool. HamsterWeazle checks meanhamster.com for app updates, while gw.exe and HxCFloppyEmulator still check their upstream sources. The startup check runs once per session.
