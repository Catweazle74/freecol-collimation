using System;

namespace FreeCol.Ui;

/// <summary>
/// Mapping zwischen Frame-Pixel-Koordinaten (Originalbild der Kamera) und
/// Display-Pixel-Koordinaten (innerhalb des Image-Controls), unter Berücksichtigung
/// von Stretch=Uniform-Letterboxing und Zoom-Crop. Wird vom Overlay-Layer genutzt,
/// damit Markierungen unabhängig von der Capture-Rate gerendert werden können.
/// </summary>
public readonly record struct DisplayTransform(
    double Ratio,
    double MarginX,
    double MarginY,
    double CropOffsetX,
    double CropOffsetY)
{
    public bool IsValid => Ratio > 0;

    public (double X, double Y) MapToDisplay(double frameX, double frameY)
    {
        // Avalonia rendert Bitmap-Pixel (i, j) so, dass dessen visueller Mittel-
        // punkt auf dem Display bei (MarginX + (i + 0.5) × Ratio, …) liegt. Der
        // halbe-Ratio-Offset muss daher in der Detection-Pixel-zu-Display-
        // Abbildung mit dazu, sonst sitzen Markierungen 0.5 × Ratio Pixel
        // links-oben vom tatsächlichen Feature.
        var croppedX = frameX - CropOffsetX + 0.5;
        var croppedY = frameY - CropOffsetY + 0.5;
        return (croppedX * Ratio + MarginX, croppedY * Ratio + MarginY);
    }

    public double MapLengthToDisplay(double frameLength) => frameLength * Ratio;

    public static DisplayTransform Compute(
        double controlWidth, double controlHeight,
        int croppedWidth, int croppedHeight,
        int frameWidth, int frameHeight)
        => Compute(controlWidth, controlHeight, croppedWidth, croppedHeight,
                   (frameWidth - croppedWidth) / 2.0, (frameHeight - croppedHeight) / 2.0);

    /// <summary>
    /// Wie oben, aber mit explizitem Crop-Offset (linke obere Ecke des Crop-
    /// Fensters im Frame). Für den Sterntest-Auto-Zoom, der auf den außermittigen
    /// Donut zentriert — dort ist der Crop nicht frame-zentriert.
    /// </summary>
    public static DisplayTransform Compute(
        double controlWidth, double controlHeight,
        int croppedWidth, int croppedHeight,
        double cropOffsetX, double cropOffsetY)
    {
        if (croppedWidth <= 0 || croppedHeight <= 0 || controlWidth <= 0 || controlHeight <= 0)
            return default;

        var ratio = Math.Min(controlWidth / croppedWidth, controlHeight / croppedHeight);
        if (ratio <= 0) return default;

        var displayedW = croppedWidth * ratio;
        var displayedH = croppedHeight * ratio;
        var marginX = (controlWidth - displayedW) / 2;
        var marginY = (controlHeight - displayedH) / 2;
        return new DisplayTransform(ratio, marginX, marginY, cropOffsetX, cropOffsetY);
    }
}
