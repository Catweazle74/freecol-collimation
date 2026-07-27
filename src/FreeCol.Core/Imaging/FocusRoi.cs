namespace FreeCol.Core.Imaging;

/// <summary>
/// Quadratisches Fenster um den Fokus-Punkt einer Markierung, in dem die Schärfe
/// gemessen wird. Der Fokus-Punkt ist der Mausklick (bei manueller Platzierung)
/// bzw. ein Punkt auf der Markierungs-Kante — so misst der Autofokus die
/// relevante Struktur (Kante/Marker) und nicht das andersfokussierte Zentrum.
/// </summary>
public readonly record struct FocusRoi(double CenterX, double CenterY, double HalfSize);
