# macOS-Build (Apple Silicon)

Baut FreeCol als self-contained `.app` für **Apple Silicon (osx-arm64)**. Intel-Macs
werden nicht unterstützt. Alle Schritte laufen **auf dem Mac** (nicht cross von Linux).

## Voraussetzungen (einmalig)
- Mac mit **Apple Silicon** (M1+).
- **.NET 10 SDK (arm64)** — Installer von <https://dotnet.microsoft.com> oder `brew install dotnet-sdk`.
- **Xcode Command Line Tools**: `xcode-select --install` (liefert `codesign`, `notarytool`, `stapler`).

## Stufe 1 — Bauen & Smoke-Test (kein Account nötig)
```bash
git clone http://gitlab.local/marc/freecol.git
cd freecol
./scripts/build-macos.sh
open artifacts/macos/FreeCol.app
```
Dann im **Sterntest** eine FITS-Datei laden **oder** eine UVC-Kamera öffnen.
- Bild/Frame erscheint → die OpenCV-Native (4.8.1-rc) funktioniert mit der managed 4.13. ✅
- Crash beim ersten OpenCV-Aufruf (`DllNotFoundException`, „symbol not found", `OpenCvSharpExtern`)
  → Versions-Mismatch; Fehlerzeile melden, dann auf eine andere Runtime schwenken.

> Lokal gebaute Apps unterliegen nicht der Gatekeeper-Quarantäne — der Smoke-Test geht
> auch **unsigniert**. Signieren ist nur fürs Verteilen an andere nötig.

## Stufe 2 — Signieren & Notarisieren (für die Verteilung)

**Einmalige Einrichtung:**
1. „Developer ID Application"-Zertifikat im Schlüsselbund installieren (aus dem Apple
   Developer Portal). Name prüfen: `security find-identity -v -p codesigning`.
2. notarytool-Profil anlegen (speichert die Zugangsdaten im Schlüsselbund):
   ```bash
   xcrun notarytool store-credentials FreeCol-Notary \
     --apple-id "DEINE_APPLE_ID" --team-id "DEINE_TEAM_ID" \
     --password "APP-SPEZIFISCHES-PASSWORT"
   ```
   (App-spezifisches Passwort: appleid.apple.com → Anmeldung & Sicherheit.)

**Signierten + notarisierten Build erzeugen:**
```bash
VERSION=0.1.0-beta \
SIGN_IDENTITY="Developer ID Application: Dein Name (TEAMID)" \
NOTARY_PROFILE=FreeCol-Notary \
./scripts/build-macos.sh --notarize
```
Ergebnis: `artifacts/macos/FreeCol-0.1.0-beta-macos-arm64.zip` — gestaplet, von jedem
Mac ohne Gatekeeper-Warnung zu öffnen.

## Kameras auf dem Mac
| Quelle | Status |
|--------|--------|
| **UVC / Webcam** (OCAL) | läuft direkt |
| **Alpaca / INDIGO** (Netzwerk) | läuft direkt |
| **Live (ASI)** nativ per USB | läuft — einmalig `./scripts/setup-asi-macos.sh` (siehe unten) |

### ASI nativ einrichten

```bash
./scripts/build-macos.sh          # zuerst bauen
./scripts/setup-asi-macos.sh      # dylibs ins .app legen
```

Das Script holt das ZWO-Camera-SDK, zieht `lib/mac_arm64/libASICamera2.dylib`
heraus, baut ein passendes `libusb` und legt beides neben das Binary. Zwei
Fallen, die es abräumt — nicht von Hand versuchen:

1. Die `libASICamera2.dylib` **aus ASI Studio ist i386/x86_64** (ASI Studio läuft
   unter Rosetta). Ein osx-arm64-Prozess kann sie prinzipiell nicht laden. arm64
   gibt es nur im separaten Camera-SDK (verifiziert ab **V1.41**).
2. Genau diese arm64-dylib linkt gegen den **absoluten** Pfad
   `/opt/homebrew/opt/libusb/lib/libusb-1.0.0.dylib`. Ohne Homebrew-libusb an
   exakt dieser Stelle schlägt das Laden fehl — auf jedem Endanwender-Mac. Das
   Script baut libusb daher selbst und biegt die Referenz auf `@loader_path` um.

Verifiziert am 22.07.2026 auf MacBook Air (M-Serie, macOS 15.7) mit einer
ZWO ASI120MC-S: Enumeration, Öffnen, Belichtung und 1280×960-RAW16-Frame.

## Optionen des Scripts
| Aufruf | Wirkung |
|--------|---------|
| `./scripts/build-macos.sh` | Publish + `.app` (unsigniert) — für Smoke-Test |
| `… --sign` | zusätzlich code-signieren (braucht `SIGN_IDENTITY`) |
| `… --notarize` | zusätzlich notarisieren + stapeln + Verteil-ZIP (impliziert `--sign`) |

Variablen: `VERSION`, `BUNDLE_ID` (Default `de.c-trace.freecol`), `SIGN_IDENTITY`, `NOTARY_PROFILE`.
