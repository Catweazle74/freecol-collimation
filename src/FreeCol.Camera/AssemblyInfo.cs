using System.Runtime.CompilerServices;

// Erlaubt dem Testprojekt den Zugriff auf interne Typen/Member — hier konkret
// CameraEnumerator.SerialFromDevicePath, das als plattformneutraler Parser
// bewusst nicht public ist (reines Implementierungsdetail der Windows-Enumeration).
[assembly: InternalsVisibleTo("FreeCol.Core.Tests")]
