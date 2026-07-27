namespace FreeCol.Core.Imaging;

/// <summary>
/// Bündelt die Detektoren der Kalibrier-/Justage-Pipeline an einer Stelle, damit ein
/// Aufrufer (z. B. das ViewModel) eine einzige Abhängigkeit hält statt sieben einzelner
/// Instanzen. Die Detektoren bilden eine Hint-Kette
/// (OAZ-Rand → Hauptspiegel-Reflex → Sekundär/Fangspiegel) und behalten deshalb bewusst
/// ihre individuellen <c>Detect(...)</c>-Signaturen — ein erzwungenes Einheits-Interface
/// wäre für die heterogenen Eingaben eine undichte Abstraktion. Diese Klasse kapselt nur
/// Aufbau und Bereitstellung, nicht die Detektions-Logik.
/// </summary>
/// <remarks>
/// Die Properties sind <c>init</c>-bar, damit Tests oder zukünftige Aufrufer einzelne
/// Detektoren mit abweichender Parametrierung einsetzen können; ohne Angabe entsteht
/// exakt das bisherige Verhalten (Default-Konstruktion je Detektor).
/// </remarks>
public sealed class DetectorSet
{
    public OazRandDetector OazRand { get; init; } = new();
    public HauptspiegelReflexDetector HauptspiegelReflex { get; init; } = new();
    public SekundaerSilhouetteDetector Sekundaer { get; init; } = new();
    public MarkerRingDetector MarkerRing { get; init; } = new();
    public MarkerDetector Linse { get; init; } = new();
    public FangspiegelReflexDetector FangspiegelReflex { get; init; } = new();
    public DonutDetector Donut { get; init; } = new();
}
