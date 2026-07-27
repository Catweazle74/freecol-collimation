using System;
using OpenCvSharp;

namespace FreeCol.Core.Calibration;

/// <summary>
/// Persistierte Kalibrier-Daten der OCAL. <see cref="OpticalCenter"/> ist der
/// Drehmittelpunkt der OCAL-Innenoptik in Sensor-Pixelkoordinaten — der
/// Referenzpunkt für jede spätere Kollimations-Versatzmessung.
/// <see cref="OrientationConfirmed"/> bedeutet: der Benutzer hat seine
/// mechanische Montage-Markierung mit der Bild-12-Uhr-Achse abgeglichen,
/// also gilt image-up ≡ telescope-up bei späterer Montage.
/// </summary>
public sealed record CalibrationResult(
    Point2f OpticalCenter,
    double FitRadius,
    double RmsResidual,
    int SampleCount,
    bool OrientationConfirmed,
    DateTimeOffset Timestamp,
    // Aufnahme-Auflösung des analysierten Frames. Default 0 = Legacy/
    // unbekannt (ältere JSON-Dateien ohne dieses Feld laden weiterhin
    // klaglos). Grundlage für die Auflösungs-Mismatch-Erkennung — siehe
    // MainWindowViewModel.CalibrationResolutionMismatch.
    int FrameWidth = 0,
    int FrameHeight = 0);
