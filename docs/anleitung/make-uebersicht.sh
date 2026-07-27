#!/usr/bin/env bash
#
# Erzeugt den annotierten Übersichts-Screenshot für die Anleitung
# (docs/anleitung/bilder/uebersicht.png) reproduzierbar neu.
#
# Voraussetzungen: laufender X-Server (DISPLAY), xdotool, xwd, ffmpeg,
# Flatpak-Chrome (com.google.Chrome) fürs Rendern der Nummern-Badges.
# Die OCAL-Kamera sollte angeschlossen sein, damit der Auto-Start ein
# Livebild mit Markierungs-Ringen zeigt (Markier-Modus = Standardansicht).
#
# Aufruf:  ./make-uebersicht.sh [Pfad-zur-App]
# Default: Debug-Build aus dem Repo.

set -euo pipefail
cd "$(dirname "$0")"
APP="${1:-dotnet ../../src/FreeCol.Ui/bin/Debug/net10.0/FreeCol.dll}"
OUT="bilder/uebersicht.png"
# Temp-Verzeichnis NICHT unter /tmp — die Flatpak-Chrome-Sandbox kann nur
# in per --filesystem freigegebene Home-Pfade schreiben.
TMP=$(mktemp -d "$PWD/.uebersicht-XXXXXX")
trap 'rm -rf "$TMP"; pkill -f "net10.0/FreeCol[.]dll" 2>/dev/null || true' EXIT

echo "Starte App: $APP"
DISPLAY="${DISPLAY:-:0}" $APP >/dev/null 2>&1 &
sleep 10
WIN=$(DISPLAY="${DISPLAY:-:0}" xdotool search --class "FreeCol" | head -1)
[ -n "$WIN" ] || { echo "Kein FreeCol-Fenster gefunden"; exit 1; }
DISPLAY="${DISPLAY:-:0}" xdotool windowsize "$WIN" 1800 1080
sleep 1
DISPLAY="${DISPLAY:-:0}" xwd -id "$WIN" -silent | ffmpeg -y -loglevel error -i - -vf scale=1600:-1 "$TMP/roh.png"
pkill -f "net10.0/FreeCol[.]dll" 2>/dev/null || true

# Nummern-Badges als HTML-Overlay, dann per Chrome zu einem PNG gebacken.
# Positionen in Prozent der Bildfläche — bleiben bei Layout-Feinschliff stabil.
B64=$(base64 -w0 "$TMP/roh.png")
cat > "$TMP/annotate.html" <<HTML
<!doctype html><meta charset="utf-8">
<style>
  body { margin: 0; }
  .wrap { position: relative; display: inline-block; }
  .wrap img { display: block; width: 1600px; }
  .badge {
    position: absolute; width: 34px; height: 34px; border-radius: 50%;
    background: #FF8C42; color: #fff; font: bold 20px/34px sans-serif;
    text-align: center; border: 2px solid #fff; box-shadow: 0 0 6px #000;
    transform: translate(-50%, -50%);
  }
</style>
<div class="wrap">
  <img src="data:image/png;base64,${B64}">
  <div class="badge" style="left:3%;  top:2.5%">1</div>
  <div class="badge" style="left:3%;  top:8%">2</div>
  <div class="badge" style="left:2.5%;top:13%">3</div>
  <div class="badge" style="left:40%; top:50%">4</div>
  <div class="badge" style="left:82%; top:3%">5</div>
  <div class="badge" style="left:82%; top:50%">6</div>
  <div class="badge" style="left:82%; top:92%">7</div>
  <div class="badge" style="left:2.5%;top:97%">8</div>
</div>
HTML
H=$(ffprobe -v error -select_streams v -show_entries stream=height -of csv=p=0 "$TMP/roh.png")
flatpak run --filesystem="$TMP" com.google.Chrome --headless --disable-gpu \
  --window-size=1600,"$H" --screenshot="$TMP/final.png" "file://$TMP/annotate.html" 2>/dev/null
mv "$TMP/final.png" "$OUT"
echo "Fertig: $OUT ($(du -h "$OUT" | cut -f1))"
