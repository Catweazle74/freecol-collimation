# FreeCol

Geführte Newton-Kollimation in zwei Etappen: Grobjustage mit OCAL-Kamera im OAZ, dann Feinjustage am echten Stern mit Astrokamera (defokussierter Sterntest).

![FreeCol-UI: Markierungen mit Workflow-Leiste](docs/anleitung/bilder/uebersicht.png)

---

## Features

**Zwei-Etappen-Workflow**
- **Etappe 1 — Grobjustage**: OCAL-Justagekamera im Okularauszug, geführte Phasen 0–3 mit Live-Overlay und Drehempfehlungen
- **Etappe 2 — Feinjustage**: Defokussierter Sterntest (Donut) mit Astrokamera, automatische Versatzmessung und Schrauben-Drehempfehlungen

**Automatische Markierungserkennung**
- Detektiert OAZ-Rand, Hauptspiegel-Marker, Sekundärspiegel und Linsenstrahler im Live-Bild (OpenCV-Pipeline)
- Manuelle Platzierung per Klick (falls nötig)
- Pro Kamera gespeichert und wiederhergestellt

**Schrauben-Kalibrierung**
- Speichert, wie viel Drehung nötig ist, um den Versatz um 1 Pixel zu korrigieren (pro Justage-Achse)
- Einmal pro Teleskop-Baugruppe, dann Empfehlungen in Umdrehungen statt Pixeln

**Sterntest-Modus**
- Live-ASI-Kamera oder Netzwerk-Alpaca/INDIGO
- Dateibasiert: einzelne FITS/PNG/JPG/TIFF laden
- Ordner-Überwachung für kontinuierliche Aufnahmen
- Donut-Versatz automatisch gemessen, Overlay + Zoom

**Persistenz pro Kamera**
- Markierungen, Kalibrierungen und Belichtungs-/Fokus-Werte per Kamera (Seriennummer + Name)
- Automatisches Laden beim nächsten Start

---

## Plattformen

| Plattform | Status | Format | Besonderheiten |
|-----------|--------|--------|-----------------|
| **Windows x64** | Beta | ZIP, self-contained | Native ASI-Kamera: `ASICamera2.dll` nötig (siehe [BETA-WINDOWS.md](BETA-WINDOWS.md)) |
| **Linux x64** | Beta | tar.gz, self-contained | V4L2-Backend, Benutzer in Gruppe `video` |
| **macOS Apple Silicon** | Beta | tar.gz, self-contained | Native ASI-Kamera: `./scripts/setup-asi-macos.sh` (siehe [MACOS-BUILD.md](MACOS-BUILD.md)) |

Alle Plattformen: UVC-Webcams (z. B. OCAL), Alpaca/INDIGO-Netzwerkverbindung, dateibasierte Sterntest-Aufnahmen.

---

## Quick Start

### Voraussetzungen
- **.NET 10 SDK** ([Download](https://dotnet.microsoft.com/download/dotnet/10.0))
- **OCAL-Kamera** oder vergleichbares UVC-Gerät für Grobjustage
- **Astrokamera** für Feinjustage (Alpaca, native ASI, oder Datei/Ordner)
- **3D-gedruckte Kalibrier-Hilfen** (optional, siehe unten)

### App starten

```bash
# Code herunterladen und öffnen
git clone https://github.com/Catweazle74/freecol-collimation.git
cd freecol-collimation

# Abhängigkeiten installieren und App starten
dotnet run --project src/FreeCol.Ui
```


### Oder: vorgefertigte Beta-Pakete

1. **Releases** (GitHub) öffnen
2. Deine Plattform wählen (Windows ZIP, Linux/macOS tar.gz)
3. Entpacken und Anleitung (freecol-anleitung.pdf) beachten

---

## Workflow — Die fünf Schritte

1. **Kamera verbinden** — OCAL oder Astrokamera aus der Liste wählen, Auflösung ggf. anpassen, **Start** drücken
2. **Kalibrieren** (optional) — Kreisfit über 360°-Drehung, bestimmt das optische Zentrum der Justagekamera
3. **Markieren** — OAZ-Rand, Hauptspiegel-Marker, Sekundärspiegel und Linsenstrahler erkennen oder manuell setzen
4. **Justage** — Geführte Phasen 0–3 mit Drehempfehlungen für die Justierschrauben
5. **Sterntest** — Feinjustage am echten Stern mit Donut-Methode

---

## Dokumentation

- **[Anleitung für Einsteiger](docs/anleitung/freecol-anleitung.md)** (oder PDF aus dem Release) — Schritt-für-Schritt, mit Bildschirmfotos
- **[Windows-Beta-Hinweise](BETA-WINDOWS.md)** — ASI-DLL, SmartScreen
- **[macOS-Build](MACOS-BUILD.md)** — Bauen, Signieren, ASI-Setup

---

## Selbst bauen

### Voraussetzungen
- **.NET 10 SDK**
- **Windows**: 
  - Für native ASI-Kamera: [ASI Camera SDK](https://zwoasi.com/downloads/detail/10) installieren
- **macOS**:
  - `brew install dotnet-sdk xcode-select`
  - Für native ASI: `./scripts/setup-asi-macos.sh` nach dem Build
- **Linux**:
  - `sudo apt install dotnet-sdk-10.0`

### Build & Test

```bash
# Bauen
dotnet build

# Tests (xUnit, headless)
dotnet test

# App starten
dotnet run --project src/FreeCol.Ui

# Offline-Analyse von Snapshots
dotnet run --project tools/FreeCol.Inspect [snapshot.png]
```

**FreeCol.Inspect** ohne Argument analysiert den jüngsten Snapshot aus deinem Bilder-Verzeichnis.

---

## Projektstruktur

```
FreeCol/
├── src/
│   ├── FreeCol.Core/
│   │   ├── Imaging/              OpenCV-Pipeline, Detektion, Clustering
│   │   ├── Calibration/          Kreisfit, Persistenz
│   │   ├── Justage/              Justage-Phasen und Drehempfehlung
│   │   └── Markings/             Markierungserkennung
│   │
│   ├── FreeCol.Camera/
│   │   ├── ICameraSource         Abstraktion: Start/Stop/GrabFrame
│   │   ├── IExposureControl      Auto/Manuell
│   │   ├── IFocusControl         Auto/Manuell
│   │   ├── OpenCvVideoCaptureSource  V4L2-Backend (Linux)
│   │   └── AlpacaCameraSource    INDIGO-Protokoll
│   │
│   └── FreeCol.Ui/
│       ├── MainWindowViewModel   Capture-Loop, Overlay, Snapshot, Persistenz
│       └── CalibrationWizardViewModel  Kalibrierungs-Phasen (Orientierung, Rotation, Review)
│
├── tests/
│   └── FreeCol.Core.Tests        ~135 Tests (xUnit, headless)
│
├── tools/
│   ├── FreeCol.Inspect           CLI zur Offline-Analyse
│   └── FreeCol.MarkGt            Generierung von Trainings-Daten
│
└── 3D/
    ├── Rotatorauflage.stl        Kalibrier-Auflage für 360°-Drehung
    ├── KollimatorKappe.stl       Kappe mit durchsichtigem Boden
    ├── KollimatorDruckprojekt.3mf Farb-Wechsel für zweifarbigen Druck
    └── KollimatorAuflageMitKappe.stl  Kombiniertes Teil
```

**Abhängigkeitsrichtung**: `Ui` → `Camera` → `Core`, `Ui` → `Core`. Core kennt keine Avalonia oder Kamera-APIs.

---

## 3D-gedruckte Kalibrier-Hilfen

Für eine präzise Kreis-Kalibrierung, die mit einer Regel-Fläche und Pufferbahn gedreht werden kann:

1. **Dateien im [3D/-Ordner](3D/)** — STL für FDM-Druck
2. **Aufbau**: OCAL mit 100-mm-T2-Verlängerung in die Rotatorauflage, vorn die Kappe, dahinter Lichtquelle
3. **Feinheit**: Mit Gummiband gegen versehentliches Abheben während der Rotation sichern
4. **Druck-Projekt**: [KollimatorDruckprojekt.3mf](3D/KollimatorDruckprojekt.3mf) mit Farbwechsel (transparente Rückwand für Durchleuchtung)

Detaillierte Fotos und Aufbau-Anleitung in [Kapitel 4 der Anleitung](docs/anleitung/freecol-anleitung.md).

---

## Status

**Beta** — geführte Justage (Phasen 0–3) live am Teleskop erprobt:
- ✅ Grobjustage: Markierungserkennung, Kalibrierung, Justage-Phasen mit Drehempfehlungen
- ✅ Sterntest-Dateibasiert: FITS/PNG/JPG/TIFF laden, Donut-Versatz, Overlay
- ✅ Astrokamera-Anbindung: Alpaca/INDIGO, native ASI (mit Plattform-Setup), Ordner-Watch
- 🔄 Himmelstest: Live-Sterntest noch in Verifikation

---

## Lizenz

MIT — siehe [LICENSE](LICENSE).

---

## Beitrag & Feedback

**Bekannte Limitierungen (Beta)**:
- Windows: SmartScreen-Warnung bei unsigniertem Build
- macOS: ASI-Kamera braucht zusätzliches Setup via `setup-asi-macos.sh`

**Bitte melden**: 
- Schritte zum Reproduzieren
- Erwartetes vs. tatsächliches Verhalten
- Screenshots/Logs (falls möglich)

---

## Abhängigkeiten

- **.NET 10** (Runtime in self-contained Builds enthalten)
- **Avalonia 12** (UI-Framework)
- **OpenCvSharp 4.13** (Bildanalyse)
- **INDIGO-Alpaca** (optional, Netzwerk-Kameraverbindung)
- **ZWO-ASI-SDK** (optional, native ASI-Kamera auf Windows/macOS)

---

**Entwickelt für Amateurastronomen, die ihr Newton-Teleskop präzise und reproduzierbar kollimieren möchten.**
