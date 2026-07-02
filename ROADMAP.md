# HamsterWeazle Roadmap

This roadmap tracks practical improvements for HamsterWeazle. It is not a promise
of dates; it is a place to keep the next good ideas from getting lost.

## Reliability and Recovery

- Improve adaptive write retry with track-level recovery actions inspired by
  FluxEngine:
  - retry the current failed logical track before resuming the rest of the disk
  - optionally recalibrate or step away/back after repeated verify failures
  - keep recovered failures visible in the log without leaving the drive status
    in an error state after recovery
- Add clearer verify diagnostics:
  - failed track/head
  - missing sector vs checksum/data mismatch when GreaseWeazle output exposes it
  - final result text such as "Completed - no errors" or "Completed after retry"
- Add a per-track result summary for reads and writes so a user can see success,
  retries, recovered errors, and hard failures at a glance.

## Macintosh 400K/800K Handling

- Surface Mac 400K/800K zone information in the UI. FluxEngine models Mac disks
  as five track zones:
  - tracks 0-15: 12 sectors
  - tracks 16-31: 11 sectors
  - tracks 32-47: 10 sectors
  - tracks 48-63: 9 sectors
  - tracks 64-79: 8 sectors
- Add a note or warning for Mac 800K writes that track 48 begins a new zone. This
  is where some drive setups may expose speed/verification problems.
- Consider a Mac-specific write helper that suggests higher retries or adaptive
  retry when failures start near zone boundaries.

## Drive Profiles

- Add named drive profiles for common hardware:
  - standard PC 3.5 inch drive
  - standard PC 5.25 inch drive
  - Shugart DS0-DS3 setup
  - known-tested Teac FD-235HF setup
- Expose drive bus and speed concepts more clearly in Advanced, taking cues from
  FluxEngine's explicit `pc`, `shugart`, `300 rpm`, `360 rpm`, and auto speed
  options.
- Add a quick drive capability check that runs GreaseWeazle info/RPM commands and
  records whether the current drive behaves like the selected profile.

## Format Detection and Image Insight

- Expand `.img` detection beyond file size by inspecting boot blocks, filesystem
  signatures, and saved queue history.
- Show why a format was auto-detected, for example "Mac HFS boot block" or
  "819,200-byte raw sector image".
- Consider direct filesystem hints for formats where image contents are easy to
  recognize before writing.

## Documentation

- Document Mac variable-zone behavior and why Mac 800K disks can fail around
  track 48 on some setups.
- Add a short Shugart/PC bus explainer with practical GreaseWeazle settings.
- Add troubleshooting entries for recovered retry, hard verify failure, wrong
  drive speed, and drive profile mismatch.

