using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RigControl.Core.Hangar;

/// <summary>Posición y tamaño "normales" (sin minimizar ni maximizar), en píxeles reales del escritorio.</summary>
public sealed record WindowInfo(int X, int Y, int Width, int Height, HangarWindowState State, bool Topmost);

public enum WindowApplyResult { Unchanged, Changed, OffScreen }

/// <summary>Lo que el Hangar necesita de las ventanas de Windows; las pruebas lo sustituyen por uno falso.</summary>
public interface IWindowHost
{
    /// <summary>Ventanas principales visibles del proceso (las de la barra de tareas), la de más arriba primero.</summary>
    IReadOnlyList<nint> MainWindows(int processId);

    /// <summary>null si la ventana ya no existe.</summary>
    WindowInfo? Describe(nint window);

    /// <summary>
    /// Deja la ventana como dicen los ajustes, cambiando solo lo que no coincide (se puede llamar
    /// muchas veces). OffScreen = la posición no cae en ninguna pantalla y no se ha movido (lo
    /// demás sí se aplica). Win32Exception si no se deja (ventana de un programa elevado);
    /// InvalidOperationException si la ventana ya no existe.
    /// </summary>
    WindowApplyResult Apply(nint window, HangarWindow settings);

    /// <summary>Restaura la ventana si está minimizada y la pone en primer plano. Devuelve si lo consiguió.</summary>
    bool BringToFront(nint window);
}

/// <summary>
/// Ventanas de verdad (user32). Todo en modo "consciente del DPI por monitor" para trabajar en
/// píxeles reales aunque haya escalado: así coinciden las coordenadas guardadas y las aplicadas,
/// sea cual sea el proceso que llama (la consola no declara DPI; la app WPF, sí).
/// Las llamadas son síncronas: un programa colgado puede retener al hilo que llama (el runner las
/// hace en sus propias tareas).
/// </summary>
public sealed class WindowHost : IWindowHost
{
    public IReadOnlyList<nint> MainWindows(int processId)
    {
        using var _ = new DpiScope();
        var found = new List<nint>();
        EnumWindowsProc callback = (hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == processId && IsMainWindow(hwnd)) found.Add(hwnd);
            return true;
        };
        EnumWindows(callback, 0);
        GC.KeepAlive(callback);
        return found;
    }

    public WindowInfo? Describe(nint window)
    {
        using var _ = new DpiScope();
        if (!IsWindow(window)) return null;
        var state = IsIconic(window) ? HangarWindowState.Minimized
                  : IsZoomed(window) ? HangarWindowState.Maximized
                  : HangarWindowState.Normal;
        var rect = NormalRect(window, state);
        if (rect is not { } r) return null;
        return new WindowInfo(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, state, IsTopmost(window));
    }

    public WindowApplyResult Apply(nint window, HangarWindow settings)
    {
        using var _ = new DpiScope();
        if (!IsWindow(window)) throw new InvalidOperationException("la ventana ya no existe");
        var changed = false;
        var offScreen = false;
        var initial = IsIconic(window) ? HangarWindowState.Minimized : IsZoomed(window) ? HangarWindowState.Maximized : HangarWindowState.Normal;

        if (settings.HasPosition || settings.HasSize)
        {
            var current = NormalRect(window, initial) ?? throw new InvalidOperationException("la ventana ya no existe");
            var x = settings.X ?? current.Left;
            var y = settings.Y ?? current.Top;
            var w = settings.Width ?? current.Right - current.Left;
            var h = settings.Height ?? current.Bottom - current.Top;
            var target = new Rect { Left = x, Top = y, Right = x + w, Bottom = y + h };
            if (MonitorFromRect(ref target, MonitorDefaultToNull) == 0) offScreen = true;
            else if (current.Left != x || current.Top != y || current.Right - current.Left != w || current.Bottom - current.Top != h)
            {
                // Una ventana minimizada o maximizada se restaura para moverla; luego se le
                // devuelve su estado (o el que pidan los ajustes).
                if (initial != HangarWindowState.Normal) ShowWindow(window, SwShowNoActivate);
                var flags = SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder;
                if (!settings.HasPosition) flags |= SwpNoMove;
                if (!settings.HasSize) flags |= SwpNoSize;
                if (!SetWindowPos(window, 0, x, y, w, h, flags)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (settings.State is null && initial != HangarWindowState.Normal) Show(window, initial);
                changed = true;
            }
        }

        if (settings.State is { } wanted)
        {
            var now = IsIconic(window) ? HangarWindowState.Minimized : IsZoomed(window) ? HangarWindowState.Maximized : HangarWindowState.Normal;
            if (now != wanted)
            {
                Show(window, wanted);
                var after = IsIconic(window) ? HangarWindowState.Minimized : IsZoomed(window) ? HangarWindowState.Maximized : HangarWindowState.Normal;
                // ShowWindow no da error con UIPI (programa elevado): simplemente no cambia nada.
                if (after != wanted && !CanTouch(window)) throw new Win32Exception(ErrorAccessDenied);
                changed = true;
            }
        }

        if (settings.Topmost is { } topmost && IsTopmost(window) != topmost)
        {
            if (!SetWindowPos(window, topmost ? HwndTopmost : HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            changed = true;
        }

        return offScreen ? WindowApplyResult.OffScreen : changed ? WindowApplyResult.Changed : WindowApplyResult.Unchanged;
    }

    public bool BringToFront(nint window)
    {
        using var dpi = new DpiScope();
        if (!IsWindow(window)) return false;
        if (IsIconic(window)) ShowWindow(window, SwRestore);
        var foreground = GetForegroundWindow();
        if (foreground == window) return true;

        // Windows solo deja cambiar el primer plano al proceso que lo tiene: unir la cola de
        // entrada a la de la ventana activa lo permite (no se envía ninguna tecla).
        var me = GetCurrentThreadId();
        var other = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        var attached = other != 0 && other != me && AttachThreadInput(me, other, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally
        {
            if (attached) AttachThreadInput(me, other, false);
        }
        return GetForegroundWindow() == window;
    }

    /// <summary>Como la barra de tareas: visible, sin dueño, no de herramientas (salvo que pida botón), sin ocultar por DWM.</summary>
    private static bool IsMainWindow(nint hwnd)
    {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != 0) return false;
        var ex = (long)GetWindowLongPtr(hwnd, GwlExStyle);
        if ((ex & WsExToolWindow) != 0 && (ex & WsExAppWindow) == 0) return false;
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        return GetWindowRect(hwnd, out var r) && r.Right > r.Left && r.Bottom > r.Top;
    }

    private static bool IsTopmost(nint hwnd) => ((long)GetWindowLongPtr(hwnd, GwlExStyle) & WsExTopmost) != 0;

    /// <summary>
    /// Rectángulo normal en coordenadas de pantalla. Si está minimizada o maximizada se usa el de
    /// GetWindowPlacement, que va en coordenadas del área de trabajo del monitor principal.
    /// </summary>
    private static Rect? NormalRect(nint hwnd, HangarWindowState state)
    {
        if (state == HangarWindowState.Normal) return GetWindowRect(hwnd, out var r) ? r : null;
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(hwnd, ref placement)) return null;
        var n = placement.NormalPosition;
        if (((long)GetWindowLongPtr(hwnd, GwlExStyle) & WsExToolWindow) == 0)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(MonitorFromPoint(default, MonitorDefaultToPrimary), ref info))
            {
                var dx = info.Work.Left - info.Monitor.Left;
                var dy = info.Work.Top - info.Monitor.Top;
                n = new Rect { Left = n.Left + dx, Top = n.Top + dy, Right = n.Right + dx, Bottom = n.Bottom + dy };
            }
        }
        return n;
    }

    private static void Show(nint hwnd, HangarWindowState state) => ShowWindow(hwnd, state switch
    {
        HangarWindowState.Minimized => SwShowMinNoActive,
        HangarWindowState.Maximized => SwMaximize, // no hay "maximizar sin activar"
        _ => SwShowNoActivate,
    });

    /// <summary>¿Se le pueden mandar órdenes? Un SetWindowPos que no hace nada falla con UIPI.</summary>
    private static bool CanTouch(nint hwnd) =>
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);

    /// <summary>Píxeles reales mientras dura (en el hilo que llama).</summary>
    private readonly struct DpiScope : IDisposable
    {
        private readonly nint _previous;
        public DpiScope() => _previous = SetThreadDpiAwarenessContext(DpiAwarenessPerMonitorV2);
        public void Dispose() { if (_previous != 0) SetThreadDpiAwarenessContext(_previous); }
    }

    private const int ErrorAccessDenied = 5;
    private const int GwOwner = 4;
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x8, WsExToolWindow = 0x80, WsExAppWindow = 0x40000;
    private const int DwmwaCloaked = 14;
    private const int SwShowNoActivate = 4, SwMaximize = 3, SwShowMinNoActive = 7, SwRestore = 9;
    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoZOrder = 0x4, SwpNoActivate = 0x10, SwpNoOwnerZOrder = 0x200;
    private const uint MonitorDefaultToNull = 0, MonitorDefaultToPrimary = 1;
    private static readonly nint HwndTopmost = -1, HwndNoTopmost = -2;
    private static readonly nint DpiAwarenessPerMonitorV2 = -4;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length, Flags, ShowCmd;
        public Point MinPosition, MaxPosition;
        public Rect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, int cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(nint hwnd, ref WindowPlacement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(ref Rect rect, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
}
