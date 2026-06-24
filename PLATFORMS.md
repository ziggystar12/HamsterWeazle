# Platform Differences Guide

This repo builds two apps from shared code:

| Project | Platform | Framework |
|---------|----------|-----------|
| `HamsterWeazle/` | Windows | WPF (.NET 10) |
| `Mac/` | macOS / Linux | Avalonia (.NET 8) |
| `Core/` | Shared | .NET 8 class library |

---

## Rule: where does new code go?

```
Pure logic, no UI, same on all platforms  →  Core/
UI only, no platform differences          →  both projects, keep in sync  
Behaves differently per platform          →  implement IPlatform, one impl per project
```

---

## How to handle a platform difference

1. Add a method to `Core/Platform/IPlatform.cs`
2. Implement it in `HamsterWeazle/Platform/WindowsPlatform.cs`
3. Implement it in `Mac/Platform/MacPlatform.cs`
4. Call `App.Platform.YourMethod()` from shared or UI code

### Example: opening a folder

```csharp
// IPlatform
void OpenFolder(string path);

// WindowsPlatform
public void OpenFolder(string path) =>
    Process.Start("explorer.exe", path);

// MacPlatform
public void OpenFolder(string path) =>
    Process.Start("open", path);
```

---

## Known platform differences

| Feature | Windows | Mac |
|---------|---------|-----|
| gw binary name | `gw.exe` | `gw` |
| gw install | Download zip, extract | `pipx install greaseweazle` |
| gw update | Download new zip, swap | `pipx upgrade greaseweazle` |
| HxC install | Download zip from GitHub | Unknown — may not exist |
| Self-update script | `.bat` file | `.sh` shell script |
| Settings location | Next to exe | `~/.config/HamsterWeazle/` |
| Open folder | `explorer.exe path` | `open path` |
| COM port dropdown | `SerialPort.GetPortNames()` | `/dev/cu.*` — different API |
| Serial device flag | `--device COM6` | `--device /dev/cu.usbmodem*` |

---

## Building

```bash
# Windows exe only
dotnet publish HamsterWeazle/HamsterWeazle.csproj -c Release -r win-x64

# Mac Intel
dotnet publish Mac/HamsterWeazle.csproj -c Release -r osx-x64 --self-contained

# Mac Apple Silicon  
dotnet publish Mac/HamsterWeazle.csproj -c Release -r osx-arm64 --self-contained

# Everything (via GitHub Actions on release tag)
gh release create v1.x  # triggers release.yml automatically
```

---

## Release process

1. Bump version in both `HamsterWeazle/HamsterWeazle.csproj` and `Mac/HamsterWeazle.csproj`
2. `git push`
3. `gh release create v1.x "dist/HamsterWeazle.exe#HamsterWeazle.exe" --title "v1.x"`
4. GitHub Actions builds the Mac binary automatically and attaches it to the same release
