using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using OpenCvSharp;

namespace FreeCol.Core.Imaging;

/// <summary>
/// Minimaler FITS-Reader für die ASI-Sterntest-Aufnahmen: ein einzelnes 2D-Bild
/// (NAXIS=2), wie es die ZWO-Software speichert. Liest den ASCII-Header (2880-
/// Byte-Blöcke à 36 Cards mit 80 Zeichen) bis <c>END</c>, dann die Bilddaten.
///
/// Unterstützt BITPIX 16 (big-endian signed int16, via BZERO=32768 als unsigned
/// 16 bit gespeichert) und 8. Liefert eine CV_16UC1-<see cref="Mat"/> mit den
/// physikalischen Werten (BZERO + BSCALE·raw). Bei den ASI-Farbkameras ist das
/// die rohe Bayer-Matrix (RGGB) — für die Donut-Analyse irrelevant (das Scheibchen
/// ist groß), für eine Farbdarstellung müsste man debayern.
/// </summary>
public static class FitsReader
{
    private const int BlockSize = 2880;
    private const int CardSize = 80;

    public static Mat ReadGray16(string path)
    {
        using var fs = File.OpenRead(path);
        var header = ParseHeader(fs, out var dataStart);

        int bitpix = (int)GetRequired(header, "BITPIX");
        int naxis = (int)GetRequired(header, "NAXIS");
        if (naxis != 2)
            throw new NotSupportedException($"FITS: nur NAXIS=2 unterstützt, war {naxis}.");
        int width = (int)GetRequired(header, "NAXIS1");
        int height = (int)GetRequired(header, "NAXIS2");
        double bzero = header.TryGetValue("BZERO", out var bz) ? bz : 0.0;
        double bscale = header.TryGetValue("BSCALE", out var bs) ? bs : 1.0;

        long count = (long)width * height;
        fs.Seek(dataStart, SeekOrigin.Begin);

        var mat = new Mat(height, width, MatType.CV_16UC1);
        var values = new short[count]; // Bit-Muster = ushort-Wert (unchecked)

        if (bitpix == 16)
        {
            var raw = ReadExactly(fs, count * 2);
            for (long i = 0; i < count; i++)
            {
                // FITS ist big-endian.
                short signed = (short)((raw[i * 2] << 8) | raw[i * 2 + 1]);
                double phys = bzero + bscale * signed;
                values[i] = unchecked((short)ClampToUShort(phys));
            }
        }
        else if (bitpix == 8)
        {
            var raw = ReadExactly(fs, count);
            for (long i = 0; i < count; i++)
            {
                double phys = bzero + bscale * raw[i];
                values[i] = unchecked((short)ClampToUShort(phys));
            }
        }
        else
        {
            mat.Dispose();
            throw new NotSupportedException($"FITS: BITPIX {bitpix} nicht unterstützt (nur 8/16).");
        }

        Marshal.Copy(values, 0, mat.Data, (int)count);
        return mat;
    }

    private static int ClampToUShort(double v)
    {
        if (v < 0) return 0;
        if (v > 65535) return 65535;
        return (int)Math.Round(v);
    }

    private static Dictionary<string, double> ParseHeader(Stream fs, out long dataStart)
    {
        var header = new Dictionary<string, double>(StringComparer.Ordinal);
        var block = new byte[BlockSize];
        bool sawEnd = false;
        long pos = 0;

        while (!sawEnd)
        {
            int read = fs.Read(block, 0, BlockSize);
            if (read < BlockSize) throw new EndOfStreamException("FITS: Header unvollständig.");
            pos += BlockSize;

            for (int c = 0; c < BlockSize / CardSize; c++)
            {
                var card = Encoding.ASCII.GetString(block, c * CardSize, CardSize);
                var key = card.Length >= 8 ? card[..8].Trim() : card.Trim();
                if (key == "END") { sawEnd = true; break; }
                if (card.Length > 10 && card[8] == '=')
                {
                    var valuePart = card[10..];
                    var slash = valuePart.IndexOf('/');
                    if (slash >= 0) valuePart = valuePart[..slash];
                    valuePart = valuePart.Trim();
                    if (valuePart.StartsWith('\'')) continue; // String-Card, ignorieren
                    if (double.TryParse(valuePart, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                        header[key] = num;
                }
            }
        }

        dataStart = pos; // Header endet auf 2880-Block-Grenze; Daten beginnen hier.
        return header;
    }

    private static double GetRequired(Dictionary<string, double> header, string key)
        => header.TryGetValue(key, out var v)
            ? v
            : throw new InvalidDataException($"FITS: Pflicht-Keyword {key} fehlt.");

    private static byte[] ReadExactly(Stream s, long length)
    {
        var buf = new byte[length];
        long off = 0;
        while (off < length)
        {
            int r = s.Read(buf, (int)off, (int)Math.Min(int.MaxValue, length - off));
            if (r <= 0) throw new EndOfStreamException("FITS: Bilddaten unvollständig.");
            off += r;
        }
        return buf;
    }
}
