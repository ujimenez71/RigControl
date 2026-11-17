using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using RigControl.Core;

namespace RigControl;

/// <summary>
/// Recuerda la posición y el tamaño de la ventana principal (y si estaba maximizada) en
/// %APPDATA%\RigControl\ventana.json: se guarda al cerrar y se aplica al arrancar. Con Get/SetWindowPlacement de
/// Windows, que trabajan en píxeles reales y con la posición "normal" aunque esté maximizada (vale con varios
/// monitores y DPI distintos). Si lo guardado ya no cae en ninguna pantalla, no se aplica (sale centrada).
/// Minimizada al cerrar = se abre normal.
/// </summary>
public static class WindowPlacement
{
    public static string FilePath { get; } = Path.Combine(Path.GetDirectoryName(DeviceCatalog.DefaultPath)!, "ventana.json");

    private sealed record Saved(int Left, int Top, int Right, int Bottom, bool Maximized);

    /// <summary>Llamar en SourceInitialized (la ventana ya tiene identificador pero aún no se ve).</summary>
    public static void Restore(Window window)
    {
        Saved? s;
        try { s = File.Exists(FilePath) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return; }
        if (s is null || s.Right - s.Left < 200 || s.Bottom - s.Top < 150) return;

        var rect = new RECT { Left = s.Left, Top = s.Top, Right = s.Right, Bottom = s.Bottom };
        if (MonitorFromRect(ref rect, MONITOR_DEFAULTTONULL) == IntPtr.Zero) return;   // pantalla que ya no está

        var hwnd = new WindowInteropHelper(window).Handle;
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp)) return;
        wp.rcNormalPosition = rect;
        wp.showCmd = s.Maximized ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL;
        wp.flags = 0;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        SetWindowPlacement(hwnd, ref wp);
        if (s.Maximized) window.WindowState = WindowState.Maximized;
    }

    /// <summary>Llamar al cerrar (Closing), con la ventana aún abierta.</summary>
    public static void Save(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp)) return;
        var r = wp.rcNormalPosition;
        var maximized = window.WindowState == WindowState.Maximized
                        || (window.WindowState == WindowState.Minimized && (wp.flags & WPF_RESTORETOMAXIMIZED) != 0);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Saved(r.Left, r.Top, r.Right, r.Bottom, maximized),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ Win32

    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMAXIMIZED = 3;
    private const int WPF_RESTORETOMAXIMIZED = 2;
    private const uint MONITOR_DEFAULTTONULL = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);
}
