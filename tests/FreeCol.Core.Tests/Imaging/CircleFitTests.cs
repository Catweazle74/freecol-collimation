using System;
using System.Collections.Generic;
using FreeCol.Core.Imaging;
using OpenCvSharp;

namespace FreeCol.Core.Tests.Imaging;

public class CircleFitTests
{
    private static List<Point2f> Sample(Point2f center, double radius, int n)
    {
        var pts = new List<Point2f>(n);
        for (var i = 0; i < n; i++)
        {
            var a = i * Math.PI * 2 / n;
            pts.Add(new Point2f(
                (float)(center.X + radius * Math.Cos(a)),
                (float)(center.Y + radius * Math.Sin(a))));
        }
        return pts;
    }

    [Fact]
    public void Fit_PointsOnKnownCircle_RecoversCenterAndRadius()
    {
        var truthCenter = new Point2f(120, 240);
        const double truthRadius = 75.0;
        var pts = Sample(truthCenter, truthRadius, 8);

        var result = CircleFit.Fit(pts);

        Assert.InRange(result.Center.X, truthCenter.X - 0.1f, truthCenter.X + 0.1f);
        Assert.InRange(result.Center.Y, truthCenter.Y - 0.1f, truthCenter.Y + 0.1f);
        Assert.InRange(result.Radius, truthRadius - 0.1, truthRadius + 0.1);
        Assert.True(result.RmsResidual < 0.05, $"RMS war {result.RmsResidual}");
    }

    [Fact]
    public void Fit_NoisyPoints_StillNearTruth()
    {
        var truth = new Point2f(300, 200);
        const double truthRadius = 40.0;
        var rng = new Random(42);
        var pts = new List<Point2f>();
        for (var i = 0; i < 12; i++)
        {
            var a = i * Math.PI * 2 / 12;
            var noiseX = (rng.NextDouble() - 0.5) * 2.0;
            var noiseY = (rng.NextDouble() - 0.5) * 2.0;
            pts.Add(new Point2f(
                (float)(truth.X + truthRadius * Math.Cos(a) + noiseX),
                (float)(truth.Y + truthRadius * Math.Sin(a) + noiseY)));
        }

        var result = CircleFit.Fit(pts);

        Assert.InRange(result.Center.X, truth.X - 1.0f, truth.X + 1.0f);
        Assert.InRange(result.Center.Y, truth.Y - 1.0f, truth.Y + 1.0f);
        Assert.InRange(result.Radius, truthRadius - 1.0, truthRadius + 1.0);
    }

    [Fact]
    public void Fit_TooFewPoints_Throws()
    {
        var pts = new List<Point2f> { new(0, 0), new(1, 0) };
        Assert.Throws<ArgumentException>(() => CircleFit.Fit(pts));
    }
}
