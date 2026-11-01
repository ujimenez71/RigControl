using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using RigControl.Core;
using Vortice.DirectInput;

namespace RigControl.ViewModels;

/// <summary>
/// Pantalla Monitor: abre todos los mandos en modo compartido y los lee a ~60 Hz en el hilo de la
/// interfaz (DirectInput es rápido y así no hay que sincronizar nada). Equivale a "RigProbe monitor".
/// </summary>
public sealed class MonitorViewModel : ObservableObject, IDisposable
{
    private const int MaxLog = 500;

    private readonly IDirectInput8 _di = DInput.DirectInput8Create();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = new();
    private DeviceMonitor? _monitor;
    private IntPtr _window;
    private DeviceViewModel? _selected;
    private string _status = "";
    private bool _paused;

    public MonitorViewModel() => _timer.Tick += (_, _) => Tick();

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    public DeviceViewModel? Selected { get => _selected; set => Set(ref _selected, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>Con el registro en pausa se siguen viendo los mandos, pero no se anotan cambios.</summary>
    public bool Paused { get => _paused; set => Set(ref _paused, value); }

    /// <param name="window">Ventana principal, para el nivel cooperativo de DirectInput.</param>
    public void Start(IntPtr window)
    {
        _window = window;
        Reopen();
    }

    /// <summary>Cierra y vuelve a abrir todos los mandos (p. ej. tras conectar uno nuevo).</summary>
    public void Reopen()
    {
        _timer.Stop();
        _monitor?.Dispose();

        var previous = Selected?.Key;
        Devices.Clear();

        _monitor = DeviceMonitor.Open(_di, _window);
        foreach (var d in _monitor.Devices) Devices.Add(new DeviceViewModel(d));
        Selected = Devices.FirstOrDefault(d => d.Key == previous) ?? Devices.FirstOrDefault();

        Status = $"{Devices.Count} dispositivo(s) abiertos en modo compartido a ~60 Hz." +
                 (_monitor.OpenErrors.Count == 0 ? "" : $"  No se pudieron abrir: {string.Join("; ", _monitor.OpenErrors)}");
        AddLog($"---- {Devices.Count} dispositivo(s) abiertos ----");

        _clock.Restart();
        _timer.Start();
    }

    public void ClearLog() => Log.Clear();

    /// <summary>Pantalla oculta: deja de leer (los dispositivos siguen abiertos). Visible otra vez: sigue.</summary>
    public void SetVisible(bool visible)
    {
        if (_monitor is null) return;
        if (visible) _timer.Start();
        else _timer.Stop();
    }

    public void Dispose()
    {
        _timer.Stop();
        _monitor?.Dispose();
        _di.Dispose();
    }

    private void Tick()
    {
        if (_monitor is null) return;
        _monitor.Poll();

        var now = _clock.ElapsedMilliseconds;
        // Primer segundo: DirectInput da ceros hasta que llega el estado real; solo se fija la base.
        var log = now >= 1000 && !Paused;
        foreach (var d in Devices) d.Update(now, log, AddLog);
    }

    private void AddLog(string text)
    {
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {text}");
        if (Log.Count > MaxLog) Log.RemoveAt(Log.Count - 1);
    }
}
