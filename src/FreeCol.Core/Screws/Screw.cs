using System;

namespace FreeCol.Core.Screws;

/// <summary>
/// Beschreibt eine Justierschraube am Teleskop und ihren Einfluss auf die
/// Position einer Markierung im OCAL-Bild. Eine volle Umdrehung der Schraube
/// verschiebt die bewegliche Markierung der zugeordneten Phase um
/// (<see cref="EffectDx"/>, <see cref="EffectDy"/>) Frame-Pixel.
/// </summary>
/// <param name="CalibratedAt">
/// Zeitpunkt der letzten Kalibrierung, oder <c>null</c> bei unkalibrierten
/// Schrauben und bei aus älteren Profil-Dateien geladenen Schrauben (Feld
/// existierte dort noch nicht). Ans Ende gestellt und mit Default <c>null</c>,
/// damit alte <c>screws-*.json</c>-Dateien ohne dieses Feld weiter laden.
/// </param>
public sealed record Screw(
    string Name,
    int Phase,
    double EffectDx,
    double EffectDy,
    bool IsCalibrated,
    DateTimeOffset? CalibratedAt = null)
{
    public static Screw Untrained(string name, int phase) => new(name, phase, 0, 0, false);
}
