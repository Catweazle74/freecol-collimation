using System.IO;
using Avalonia.Media.Imaging;
using OpenCvSharp;

namespace FreeCol.Ui;

/// <summary>
/// Konvertiert OpenCv-Mats nach Avalonia-Bitmaps. Wir nutzen BMP-Encoding (kein
/// Komprimierungs-Overhead) und lassen Avalonia daraus die Bitmap bauen — robust
/// gegenüber Pixelformat-Details, Performance reicht für den Kameratest.
/// </summary>
internal static class MatBitmapConverter
{
    public static Bitmap ToBitmap(Mat mat)
    {
        // Einkanalige Graubilder (Sterntest) zuerst nach BGR wandeln: das
        // 8-bit-Palette-BMP, das ImEncode daraus macht, brachte Avalonias
        // Bitmap-Decoder zum nativen Absturz. Die Kamera liefert ohnehin BGR.
        Mat? converted = null;
        try
        {
            var src = mat;
            if (mat.Channels() == 1)
            {
                converted = new Mat();
                Cv2.CvtColor(mat, converted, ColorConversionCodes.GRAY2BGR);
                src = converted;
            }
            Cv2.ImEncode(".bmp", src, out var bytes);
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        finally
        {
            converted?.Dispose();
        }
    }
}
