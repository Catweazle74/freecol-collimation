#!/usr/bin/env bash
#
# Macht die native ASI-Anbindung auf macOS (Apple Silicon) lauffähig: legt
# libASICamera2.dylib (arm64) samt passender libusb neben das FreeCol-Binary.
#
# Warum dieses Script überhaupt nötig ist:
#   1. Die libASICamera2.dylib im ASIStudio.app-Bundle ist i386/x86_64 — ASI
#      Studio läuft unter Rosetta. FreeCol ist osx-arm64 und kann sie
#      grundsätzlich nicht laden ("incompatible architecture"). arm64 liefert
#      nur das separate ZWO-Camera-SDK (lib/mac_arm64, verifiziert ab V1.41).
#   2. Genau diese arm64-dylib linkt gegen den ABSOLUTEN Pfad
#      /opt/homebrew/opt/libusb/lib/libusb-1.0.0.dylib — ein Packaging-Fehler
#      von ZWO (die x86_64-Variante nutzt sauber @loader_path). Ohne Homebrew
#      inklusive libusb schlägt das Laden auf JEDEM fremden Rechner fehl.
#      Deshalb bauen wir libusb selbst und biegen die Referenz auf
#      @loader_path um — danach ist das Paar self-contained.
#
# Die ZWO-Bibliothek wird aus Lizenzgründen NICHT im Repo mitgeliefert; das
# Script lädt sie bei Bedarf direkt von ZWO.
#
# Aufruf:
#   ./scripts/setup-asi-macos.sh [ZIEL]
#     ZIEL   Verzeichnis für die beiden dylibs.
#            Default: artifacts/macos/FreeCol.app/Contents/MacOS (falls gebaut),
#            sonst der Debug-Build-Ordner von FreeCol.Ui.
#
# Umgebungsvariablen:
#   ASI_SDK_TAR   bereits heruntergeladenes ASI_linux_mac_SDK_*.tar.bz2
#                 (überspringt den Download)
#   LIBUSB_VER    libusb-Version (Default 1.0.30)

set -euo pipefail

LIBUSB_VER="${LIBUSB_VER:-1.0.30}"
SDK_URL="https://dl.zwoastro.com/software?app=DeveloperCameraSdk&platform=windows86&region=Overseas"
LIBUSB_URL="https://github.com/libusb/libusb/releases/download/v${LIBUSB_VER}/libusb-${LIBUSB_VER}.tar.bz2"

cd "$(dirname "$0")/.."
REPO="$PWD"

if [ "$(uname -s)" != "Darwin" ] || [ "$(uname -m)" != "arm64" ]; then
  echo "Dieses Script ist für macOS auf Apple Silicon gedacht." >&2
  exit 1
fi

# --- Zielverzeichnis bestimmen ------------------------------------------------
TARGET="${1:-}"
if [ -z "$TARGET" ]; then
  if [ -d "$REPO/artifacts/macos/FreeCol.app/Contents/MacOS" ]; then
    TARGET="$REPO/artifacts/macos/FreeCol.app/Contents/MacOS"
  else
    TARGET="$(find "$REPO/src/FreeCol.Ui/bin" -maxdepth 3 -type d -name "osx-arm64" 2>/dev/null | head -1)"
    [ -z "$TARGET" ] && TARGET="$(find "$REPO/src/FreeCol.Ui/bin" -maxdepth 2 -type d -name "net*" 2>/dev/null | head -1)"
  fi
fi
if [ -z "$TARGET" ] || [ ! -d "$TARGET" ]; then
  echo "Zielverzeichnis nicht gefunden — erst bauen (./scripts/build-macos.sh) oder ZIEL angeben." >&2
  exit 1
fi
echo "==> Ziel: $TARGET"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# --- 1) ZWO-SDK besorgen und die arm64-dylib herausziehen ---------------------
echo "==> 1/3  ZWO-Camera-SDK"
if [ -n "${ASI_SDK_TAR:-}" ]; then
  cp "$ASI_SDK_TAR" "$WORK/asi_sdk.tar.bz2"
else
  # Der als "windows86" ausgelieferte Download ist das kombinierte Archiv und
  # enthält ASI_linux_mac_SDK_*.tar.bz2 (~107 MB).
  echo "    lade von ZWO (~107 MB) …"
  curl -fsSL -o "$WORK/sdk.zip" "$SDK_URL"
  unzip -q -o "$WORK/sdk.zip" -d "$WORK/sdk"
  INNER="$(find "$WORK/sdk" -name "ASI_linux_mac_SDK_*.tar.bz2" | head -1)"
  [ -z "$INNER" ] && { echo "ASI_linux_mac_SDK_*.tar.bz2 nicht im Download gefunden." >&2; exit 1; }
  cp "$INNER" "$WORK/asi_sdk.tar.bz2"
fi
mkdir -p "$WORK/asi" && tar xjf "$WORK/asi_sdk.tar.bz2" -C "$WORK/asi"

ASI_DYLIB="$(find "$WORK/asi" -path "*mac_arm64*" -name "libASICamera2.dylib" | head -1)"
[ -z "$ASI_DYLIB" ] && { echo "lib/mac_arm64/libASICamera2.dylib nicht im SDK — Version zu alt?" >&2; exit 1; }
echo "    gefunden: ${ASI_DYLIB#$WORK/asi/}"

# --- 2) libusb (arm64) bauen --------------------------------------------------
echo "==> 2/3  libusb $LIBUSB_VER bauen (arm64)"
curl -fsSL -o "$WORK/libusb.tar.bz2" "$LIBUSB_URL"
tar xjf "$WORK/libusb.tar.bz2" -C "$WORK"
(
  cd "$WORK/libusb-$LIBUSB_VER"
  ./configure --prefix="$WORK/libusb-install" --disable-dependency-tracking >"$WORK/libusb-build.log" 2>&1
  make -j"$(sysctl -n hw.ncpu)" >>"$WORK/libusb-build.log" 2>&1
  make install >>"$WORK/libusb-build.log" 2>&1
) || { echo "libusb-Build fehlgeschlagen — Log: $WORK/libusb-build.log" >&2; cp "$WORK/libusb-build.log" /tmp/ 2>/dev/null; exit 1; }

# --- 3) Zusammenstellen: absolute Homebrew-Referenz → @loader_path ------------
echo "==> 3/3  dylibs ablegen und verlinken"
cp -f "$ASI_DYLIB" "$TARGET/libASICamera2.dylib"
cp -f "$WORK/libusb-install/lib/libusb-1.0.0.dylib" "$TARGET/libusb-1.0.0.dylib"
chmod u+w "$TARGET/libASICamera2.dylib" "$TARGET/libusb-1.0.0.dylib"

install_name_tool -id @loader_path/libusb-1.0.0.dylib "$TARGET/libusb-1.0.0.dylib"
install_name_tool -id @loader_path/libASICamera2.dylib "$TARGET/libASICamera2.dylib"
# Der Homebrew-Pfad steht so im ZWO-Binary; er ändert sich womöglich mit
# künftigen SDK-Versionen — deshalb dynamisch ermitteln statt hart kodieren.
OLD_USB="$(otool -L "$TARGET/libASICamera2.dylib" | awk '/libusb-1\.0/ {print $1; exit}')"
if [ -n "$OLD_USB" ] && [ "$OLD_USB" != "@loader_path/libusb-1.0.0.dylib" ]; then
  install_name_tool -change "$OLD_USB" @loader_path/libusb-1.0.0.dylib "$TARGET/libASICamera2.dylib"
fi

# Signaturen sind nach install_name_tool ungültig → ad-hoc neu signieren.
codesign --force --sign - "$TARGET/libusb-1.0.0.dylib" 2>/dev/null || true
codesign --force --sign - "$TARGET/libASICamera2.dylib" 2>/dev/null || true

echo
echo "Fertig. Verknüpfung:"
otool -L "$TARGET/libASICamera2.dylib" | sed 's/^/    /'
echo
echo "In FreeCol: Sterntest → „Live (ASI)“ → Suchen."
