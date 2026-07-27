# FreeCol — Windows-Beta (x64)

Newton-Teleskop-Kollimations-Tool. Diese Beta ist **self-contained**: ein installiertes
.NET wird **nicht** benötigt.

## Systemvoraussetzungen
- Windows 10 oder 11, 64-bit
- ~400 MB freier Speicher (entpackt)

## Starten
1. ZIP vollständig entpacken (nicht aus dem ZIP heraus starten).
2. `FreeCol.exe` ausführen.
   - Beim ersten Start kann der SmartScreen-Filter warnen (unsignierte Beta) →
     „Weitere Informationen" → „Trotzdem ausführen".

## Kameras
| Quelle | Status | Hinweis |
|--------|--------|---------|
| **UVC / Webcam** (z. B. OCAL als USB-Kamera) | funktioniert direkt | über Windows-Standardtreiber |
| **Alpaca / INDIGO** (Netzwerk) | funktioniert direkt | Port: INDIGO 7624 · ASCOM-Remote 11111 |
| **Live (ASI)** — native ZWO-ASI per USB | **zusätzliche DLL nötig** | siehe unten |

### Native ASI-Kamera aktivieren
Die native ZWO-ASI-Anbindung braucht die Hersteller-Bibliothek **`ASICamera2.dll` (x64)**:
1. ZWO **ASIStudio** bzw. das **ASI Camera SDK** installieren/herunterladen.
   Eine ASI-Studio-Standardinstallation (`C:\Program Files\ASIStudio`) findet
   FreeCol automatisch — dann entfällt Schritt 2.
2. Sonst: aus dem SDK die 64-bit `ASICamera2.dll` neben `FreeCol.exe` legen.
3. App neu starten → „Live (ASI)" als Quelle wählen.

Ohne diese DLL startet die App normal; nur die ASI-Direktverbindung ist dann inaktiv
(UVC und Alpaca funktionieren weiterhin).

## Bekannte Beta-Einschränkungen
- Unsigniertes Build (SmartScreen-Hinweis, s. o.).
- Native ASI nur mit manuell beigelegter `ASICamera2.dll`.

## Feedback
Bitte Schritt, erwartetes vs. tatsächliches Verhalten und (falls möglich) ein
Screenshot melden.
