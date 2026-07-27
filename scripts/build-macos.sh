#!/usr/bin/env bash
#
# Baut FreeCol für macOS (Apple Silicon / osx-arm64) als self-contained .app-Bundle.
# Stufen:
#   (immer)        Publish + .app-Bundle (unsigniert) — reicht für den Smoke-Test
#   --sign         zusätzlich code-signieren (Hardened Runtime, Entitlements)
#   --notarize     zusätzlich notarisieren + stapeln + Verteil-ZIP (impliziert --sign)
#
# Konfiguration per Umgebungsvariablen:
#   VERSION         z. B. 0.1.0-beta      (Default: 0.1.0-dev)
#   BUNDLE_ID       Default: de.c-trace.freecol
#   SIGN_IDENTITY   Name des "Developer ID Application"-Zertifikats (für --sign)
#   NOTARY_PROFILE  notarytool-Keychain-Profil (für --notarize)
#
# Beispiele:
#   ./scripts/build-macos.sh
#   VERSION=0.1.0-beta SIGN_IDENTITY="Developer ID Application: … (TEAMID)" \
#     NOTARY_PROFILE=FreeCol-Notary ./scripts/build-macos.sh --notarize

set -euo pipefail

RID="osx-arm64"
APP_NAME="FreeCol"
PROJECT="src/FreeCol.Ui"
VERSION="${VERSION:-0.1.0-dev}"
BUNDLE_ID="${BUNDLE_ID:-de.c-trace.freecol}"
MAIN_EXE="FreeCol.Ui"

OUT="artifacts/macos"
PUBLISH_DIR="$OUT/publish"
APP="$OUT/$APP_NAME.app"

DO_SIGN=0; DO_NOTARIZE=0
for arg in "$@"; do
  case "$arg" in
    --sign) DO_SIGN=1;;
    --notarize) DO_SIGN=1; DO_NOTARIZE=1;;
    *) echo "Unbekanntes Argument: $arg" >&2; exit 1;;
  esac
done

# Ins Repo-Root wechseln (Script liegt in scripts/).
cd "$(dirname "$0")/.."

echo "==> 1/4  Publish  ($RID, self-contained, Version $VERSION)"
rm -rf "$OUT"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained \
  -p:Version="$VERSION" -o "$PUBLISH_DIR"

echo "==> 2/4  .app-Bundle schnüren"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH_DIR/." "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/$MAIN_EXE"
cp -f BETA-WINDOWS.md "$APP/Contents/Resources/" 2>/dev/null || true

ICON_KEY=""
if [ -f "scripts/FreeCol.icns" ]; then
  cp scripts/FreeCol.icns "$APP/Contents/Resources/FreeCol.icns"
  ICON_KEY=$'  <key>CFBundleIconFile</key><string>FreeCol</string>'
fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>$APP_NAME</string>
  <key>CFBundleDisplayName</key><string>$APP_NAME</string>
  <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
  <key>CFBundleExecutable</key><string>$MAIN_EXE</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
$ICON_KEY
</dict>
</plist>
PLIST
echo "    → $APP"

if [ "$DO_SIGN" = "1" ]; then
  : "${SIGN_IDENTITY:?SIGN_IDENTITY muss gesetzt sein (Name des 'Developer ID Application'-Zertifikats)}"
  ENT="scripts/macos-entitlements.plist"
  echo "==> 3/4  Signieren (Hardened Runtime)  mit: $SIGN_IDENTITY"
  # Inside-out signieren: erst alle nativen Dylibs + ausführbaren Mach-O-Dateien,
  # dann das Bundle. (Apple rät von --deep beim Signieren ab.)
  find "$APP/Contents/MacOS" -type f \( -name "*.dylib" -o -perm -111 \) -print0 \
    | while IFS= read -r -d '' f; do
        codesign --force --timestamp --options runtime \
          --entitlements "$ENT" --sign "$SIGN_IDENTITY" "$f"
      done
  codesign --force --timestamp --options runtime \
    --entitlements "$ENT" --sign "$SIGN_IDENTITY" "$APP"
  codesign --verify --deep --strict --verbose=2 "$APP"
  echo "    → signiert & verifiziert."
else
  echo "==> 3/4  Signieren übersprungen (kein --sign)."
fi

if [ "$DO_NOTARIZE" = "1" ]; then
  : "${NOTARY_PROFILE:?NOTARY_PROFILE muss gesetzt sein (notarytool-Keychain-Profil)}"
  ZIP="$OUT/$APP_NAME-$VERSION-macos-arm64.zip"
  echo "==> 4/4  Notarisieren (kann einige Minuten dauern)"
  ditto -c -k --keepParent "$APP" "$ZIP"
  xcrun notarytool submit "$ZIP" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$APP"
  rm -f "$ZIP"
  ditto -c -k --keepParent "$APP" "$ZIP"   # frisches ZIP der gestapleten App
  echo "    → notarisiert, gestapled, Verteil-ZIP: $ZIP"
else
  echo "==> 4/4  Notarisieren übersprungen (kein --notarize)."
fi

echo ""
echo "Fertig."
echo "  App:         $APP"
echo "  Smoke-Test:  open \"$APP\"     (oder direkt: \"$APP/Contents/MacOS/$MAIN_EXE\")"
echo "  Danach im Sterntest eine FITS laden oder eine UVC-Kamera öffnen → prüft die OpenCV-Native."
