namespace FreeCol.Core.Markings;

public enum MarkingKind
{
    // OazRand bleibt der erste Wert → numerische Serialisierung 0 unverändert,
    // damit alte JSON-Dateien (Kind: 0) ohne Migration lesbar bleiben.
    OazRand,
    HauptspiegelReflex,
    Sekundaer,
    Marker,
    // Linse = dunkle Scheibe der OCAL-Eigenlinse im Marker-Ring. Bewegt sich
    // beim Hauptspiegel-Kippen (Phase 3) und ist dort das IST; Ziel ist, sie
    // in den Marker-Punkt zu bringen. Als letzter Enum-Wert (4) angehängt →
    // alte JSON-Dateien ohne diesen Slot bleiben lesbar.
    Linse,
}

/// <summary>
/// Eine vom Benutzer (oder per Auto-Detection) platzierte Markierung im Live-Bild.
/// Kreise haben <see cref="RadiusX"/> == <see cref="RadiusY"/> und <see cref="AngleDeg"/> == 0.
/// Ellipsen werden nur für die Sekundärspiegel-Silhouette verwendet.
/// </summary>
public sealed record Marking(
    MarkingKind Kind,
    bool IsPlaced,
    double CenterX,
    double CenterY,
    double RadiusX,
    double RadiusY,
    double AngleDeg,
    bool IsAutoEnabled,
    bool IsVisible,
    // null = aktuell eingestellten Fokus für Auto-Detection nutzen.
    // Sonst Soll-Fokus, den der Auto-Run vor dem Frame-Grab anfährt.
    double? AutoFocusTarget = null,
    // Fokus-Anker (Frame-Koords): bei Mausklick gesetzt → der Autofokus-ROI
    // zentriert sich genau dort. null → Fallback auf Kanten-/Mittelpunkt.
    double? FocusPointX = null,
    double? FocusPointY = null)
{
    public static Marking Default(MarkingKind kind) => new(
        Kind: kind,
        IsPlaced: false,
        CenterX: 0,
        CenterY: 0,
        RadiusX: 0,
        RadiusY: 0,
        AngleDeg: 0,
        IsAutoEnabled: true,
        IsVisible: true,
        AutoFocusTarget: null);

    /// <summary>
    /// Numerische Exzentrizität e = √(1 − (b/a)²). 0 = Kreis, ~1 = stark elliptisch.
    /// Nur für die Sekundärspiegel-Silhouette als Justage-Kriterium relevant.
    /// </summary>
    public double Eccentricity
    {
        get
        {
            if (RadiusX <= 0 || RadiusY <= 0) return 0;
            var a = System.Math.Max(RadiusX, RadiusY);
            var b = System.Math.Min(RadiusX, RadiusY);
            var ratio = b / a;
            return System.Math.Sqrt(1.0 - ratio * ratio);
        }
    }
}
