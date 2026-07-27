using OpenCvSharp;

namespace FreeCol.Core.Imaging;

/// <summary>
/// Eine durch Konturen-Fit gefundene Ellipse. <see cref="Size"/> sind die vollen
/// Achsenlängen (Breite und Höhe der umschließenden Box), nicht die Halbachsen.
/// <see cref="ContourArea"/> ist die Fläche der zugrundeliegenden Kontur und
/// taugt als Rang-Maß für "wie groß war das Feature im Bild".
/// </summary>
public sealed record EllipseFit(
    Point2f Center,
    Size2f Size,
    float AngleDegrees,
    double ContourArea)
{
    public RotatedRect ToRotatedRect() => new(Center, Size, AngleDegrees);
}
