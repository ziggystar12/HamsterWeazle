#!/bin/bash
# build_mac.sh — build HamsterWeazle.app bundle and zip for distribution
set -e

DOTNET="${DOTNET:-/opt/homebrew/bin/dotnet}"
RID="${1:-osx-arm64}"
VERSION="1.5.0"
APP_NAME="HamsterWeazle"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
DIST_DIR="$SCRIPT_DIR/../dist"
OUT_DIR="$SCRIPT_DIR/HamsterWeazle"
APP_BUNDLE="$OUT_DIR/$APP_NAME.app"
MACOS_DIR="$APP_BUNDLE/Contents/MacOS"
RES_DIR="$APP_BUNDLE/Contents/Resources"

echo "==> Cleaning previous build..."
rm -rf "$OUT_DIR"

echo "==> Publishing ($RID)..."
PUBLISH_TMP="$(mktemp -d)"
export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"
"$DOTNET" publish -c Release -r "$RID" --self-contained -o "$PUBLISH_TMP"

echo "==> Creating .app bundle structure..."
mkdir -p "$MACOS_DIR" "$RES_DIR"

# Move binary and all dylibs into Contents/MacOS/ (same dir — no DYLD_LIBRARY_PATH wrapper needed)
mv "$PUBLISH_TMP/$APP_NAME" "$MACOS_DIR/$APP_NAME"
for f in "$PUBLISH_TMP"/*.dylib; do
    [ -f "$f" ] && mv "$f" "$MACOS_DIR/"
done
chmod +x "$MACOS_DIR/$APP_NAME"
rm -rf "$PUBLISH_TMP"

echo "==> Converting icon to .icns..."
TMP_PNG="$(mktemp /tmp/hw_icon_XXXX.png)"
TMP_ICONSET_BASE="$(mktemp -d /tmp/hw_iconset_XXXX)"
ICONSET_DIR="$TMP_ICONSET_BASE/AppIcon.iconset"
mkdir -p "$ICONSET_DIR"
# sips converts .ico → png
sips -s format png "Assets/icon.ico" --out "$TMP_PNG" 2>/dev/null || cp "Assets/logo24.png" "$TMP_PNG"
for size in 16 32 64 128 256 512; do
    sips -z $size $size "$TMP_PNG" --out "$ICONSET_DIR/icon_${size}x${size}.png" 2>/dev/null || true
    double=$((size * 2))
    sips -z $double $double "$TMP_PNG" --out "$ICONSET_DIR/icon_${size}x${size}@2x.png" 2>/dev/null || true
done
iconutil -c icns "$ICONSET_DIR" -o "$RES_DIR/AppIcon.icns" 2>/dev/null || true
rm -f "$TMP_PNG"
rm -rf "$TMP_ICONSET_BASE"

echo "==> Writing Info.plist..."
cat > "$APP_BUNDLE/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key>
    <string>com.meanhamster.hamsterweazle</string>
    <key>CFBundleName</key>
    <string>HamsterWeazle</string>
    <key>CFBundleDisplayName</key>
    <string>HamsterWeazle</string>
    <key>CFBundleExecutable</key>
    <string>HamsterWeazle</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSAppTransportSecurity</key>
    <dict>
        <key>NSAllowsArbitraryLoads</key>
        <true/>
    </dict>
    <key>LSMinimumSystemVersion</key>
    <string>13.0</string>
</dict>
</plist>
PLIST

echo "==> Ad-hoc signing .app bundle..."
codesign --deep --force --sign - "$APP_BUNDLE" 2>/dev/null && echo "   Signed OK" || echo "   codesign not available — skipping"

echo "==> Creating inbox folder..."
mkdir -p "$OUT_DIR/inbox"

echo "==> Zipping to dist/..."
ARCH_LABEL=$([ "$RID" = "osx-arm64" ] && echo "AppleSilicon" || echo "Intel")
ZIP_NAME="${APP_NAME}-Mac-${ARCH_LABEL}.zip"
mkdir -p "$DIST_DIR"
rm -f "$DIST_DIR/$ZIP_NAME"
(cd "$SCRIPT_DIR" && zip -r "$DIST_DIR/$ZIP_NAME" "HamsterWeazle" -x "*.DS_Store" -x "HamsterWeazle/inbox/*")
echo ""
echo "==> App bundle:"
ls -lh "$MACOS_DIR/"
echo ""
echo "Zip: dist/$ZIP_NAME  ($(du -sh "$DIST_DIR/$ZIP_NAME" | cut -f1))"
echo ""
echo "Done. Unzip dist/$ZIP_NAME and double-click HamsterWeazle.app to run."
echo "(First launch: right-click → Open to clear Gatekeeper)"
