namespace FreeCol.Camera;

/// <summary>
/// Capability-Interface für Quellen, deren Fokus gesteuert werden kann. Einheit
/// und Range sind backend-spezifisch (V4L2 nutzt für UVC üblicherweise 0..255).
/// </summary>
public interface IFocusControl
{
    bool AutoFocus { get; set; }
    double Focus { get; set; }
    double MinFocus { get; }
    double MaxFocus { get; }
}
