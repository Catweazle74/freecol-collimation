using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeCol.Core.Imaging;

/// <summary>
/// Fasst Ellipsen-Kandidaten zusammen, die wahrscheinlich denselben physischen
/// Ring beschreiben. Canny findet an dickeren Linien meist die Innen- und die
/// Außenkante als getrennte Konturen, was zu fast identischen Ellipsen-Fits
/// führt. Pro Cluster bleibt der Fit mit der größten Konturfläche.
/// </summary>
public sealed class EllipseClusterer
{
    /// <summary>Maximaler Euklid-Abstand der Mittelpunkte (Pixel).</summary>
    public double CenterTolerancePixels { get; init; } = 5.0;

    /// <summary>Maximale relative Differenz der größeren Achse (0..1).</summary>
    public double SizeTolerancePercent { get; init; } = 0.10;

    public IReadOnlyList<EllipseFit> Merge(IReadOnlyList<EllipseFit> ellipses)
    {
        if (ellipses.Count <= 1)
        {
            return ellipses;
        }

        // Union-Find: paarweise prüfen, transitiv mergen.
        var parent = new int[ellipses.Count];
        for (var i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
        }

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb)
            {
                parent[ra] = rb;
            }
        }

        for (var i = 0; i < ellipses.Count; i++)
        {
            for (var j = i + 1; j < ellipses.Count; j++)
            {
                if (AreSimilar(ellipses[i], ellipses[j]))
                {
                    Union(i, j);
                }
            }
        }

        // Pro Cluster den Fit mit der größten Konturfläche behalten.
        var representatives = new Dictionary<int, EllipseFit>();
        for (var i = 0; i < ellipses.Count; i++)
        {
            var root = Find(i);
            if (!representatives.TryGetValue(root, out var rep) || ellipses[i].ContourArea > rep.ContourArea)
            {
                representatives[root] = ellipses[i];
            }
        }

        return representatives.Values
            .OrderByDescending(e => e.ContourArea)
            .ToList();
    }

    private bool AreSimilar(EllipseFit a, EllipseFit b)
    {
        var dx = a.Center.X - b.Center.X;
        var dy = a.Center.Y - b.Center.Y;
        if (Math.Sqrt(dx * dx + dy * dy) > CenterTolerancePixels)
        {
            return false;
        }

        var maxAxisA = Math.Max(a.Size.Width, a.Size.Height);
        var maxAxisB = Math.Max(b.Size.Width, b.Size.Height);
        var larger = Math.Max(maxAxisA, maxAxisB);
        if (larger <= 0)
        {
            return false;
        }

        var sizeDiff = Math.Abs(maxAxisA - maxAxisB) / larger;
        return sizeDiff <= SizeTolerancePercent;
    }
}
