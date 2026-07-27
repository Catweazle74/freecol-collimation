using OpenCvSharp;

namespace FreeCol.Core.Imaging;

/// <summary>
/// Bildvorverarbeitung. Bringt eingehende Frames in eine Form, die nachfolgende
/// Konturen-/Ellipsen-Erkennung gut verarbeiten kann.
/// </summary>
public static class Preprocessor
{
    /// <summary>
    /// Konvertiert <paramref name="input"/> nach Grau und glättet mit einem
    /// Gauß-Kernel der angegebenen Kantenlänge. <paramref name="blurKernel"/>
    /// muss ungerade sein; Werte ≤ 1 deaktivieren den Blur. Der Rückgabe-Mat
    /// ist neu allokiert und gehört dem Aufrufer.
    /// </summary>
    public static Mat ToGrayscaleBlurred(Mat input, int blurKernel = 5)
    {
        var gray = new Mat();
        if (input.Channels() > 1)
        {
            Cv2.CvtColor(input, gray, ColorConversionCodes.BGR2GRAY);
        }
        else
        {
            input.CopyTo(gray);
        }

        if (blurKernel > 1 && (blurKernel % 2) == 1)
        {
            Cv2.GaussianBlur(gray, gray, new Size(blurKernel, blurKernel), sigmaX: 0);
        }

        return gray;
    }
}
