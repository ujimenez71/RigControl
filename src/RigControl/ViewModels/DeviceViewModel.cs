using System.Collections.ObjectModel;
using RigControl.Core;

namespace RigControl.ViewModels;

public sealed class AxisViewModel(string name) : ObservableObject
{
    private int _raw;
    private double _percent;

    public string Name { get; } = name;
    public int Raw { get => _raw; set => Set(ref _raw, value); }
    public double Percent { get => _percent; set { if (Set(ref _percent, value)) Raise(nameof(PercentText)); } }
    public string PercentText => $"{_percent:0.0} %";

    /// <summary>Último valor anotado en el registro (para no anotar el ruido).</summary>
    internal int LoggedRaw;
}

public sealed class ButtonViewModel(int number) : ObservableObject
{
    private bool _isPressed;
    public int Number { get; } = number;
    public bool IsPressed { get => _isPressed; set => Set(ref _isPressed, value); }
}

public sealed class HatViewModel(int number) : ObservableObject
{
    private int _value = -1;
    public int Number { get; } = number;
    public int Value { get => _value; set { if (Set(ref _value, value)) Raise(nameof(Text)); } }

    /// <summary>Flecha según el ángulo (centésimas de grado, 0 = arriba, sentido horario).</summary>
    public string Text => _value < 0 ? "· centro" : $"{Arrows[(_value + 2250) / 4500 % 8]} {_value / 100}°";

    private static readonly string[] Arrows = ["↑", "↗", "→", "↘", "↓", "↙", "←", "↖"];
}

/// <summary>Un mando en la pantalla Monitor: sus ejes, botones y hats, y si ha habido actividad reciente.</summary>
public sealed class DeviceViewModel : ObservableObject
{
    /// <summary>Cambio mínimo de un eje para anotarlo en el registro (~2 %, filtra el ruido).</summary>
    private const int AxisLogThreshold = 1300;
    private const int ActivityMs = 400;

    private readonly MonitoredDevice _device;
    private long _lastActivity = long.MinValue / 2;
    private bool _isActive;
    private bool _ok = true;

    public DeviceViewModel(MonitoredDevice device)
    {
        _device = device;
        Axes = [.. device.AxisIndices.Select(i => new AxisViewModel(MonitoredDevice.AxisNames[i]))];
        Buttons = [.. Enumerable.Range(1, device.ButtonCount).Select(n => new ButtonViewModel(n))];
        Hats = [.. Enumerable.Range(1, device.PovCount).Select(n => new HatViewModel(n))];
    }

    public string Name => _device.Name;
    public string ProductName => _device.ProductName;
    public string Key => _device.Key;
    public string Summary => $"{Axes.Count} ejes · {Buttons.Count} botones · {Hats.Count} hats";

    public ObservableCollection<AxisViewModel> Axes { get; }
    public ObservableCollection<ButtonViewModel> Buttons { get; }
    public ObservableCollection<HatViewModel> Hats { get; }

    /// <summary>Ha cambiado algo hace menos de ActivityMs (el punto que se enciende en la lista).</summary>
    public bool IsActive { get => _isActive; private set => Set(ref _isActive, value); }

    /// <summary>False si no se puede leer (desconectado).</summary>
    public bool Ok { get => _ok; private set => Set(ref _ok, value); }

    /// <summary>
    /// Copia el estado del dispositivo. Con log = false (calentamiento) solo fija la base, sin anotar
    /// cambios: DirectInput da ceros en las primeras lecturas.
    /// </summary>
    public void Update(long nowMs, bool log, Action<string> addLog)
    {
        Ok = _device.Ok;
        var changed = false;

        for (var i = 0; i < Axes.Count; i++)
        {
            var a = Axes[i];
            var raw = _device.Axes[_device.AxisIndices[i]];
            a.Raw = raw;
            a.Percent = raw * 100.0 / 65535;
            if (!log) { a.LoggedRaw = raw; continue; }
            if (Math.Abs(raw - a.LoggedRaw) < AxisLogThreshold) continue;
            a.LoggedRaw = raw;
            changed = true;
            addLog($"{Name}  Eje {a.Name,-2} {raw,6}  ({a.Percent,5:0.0} %)");
        }

        for (var i = 0; i < Buttons.Count; i++)
        {
            var b = Buttons[i];
            var pressed = _device.Buttons[i];
            if (pressed == b.IsPressed) continue;
            b.IsPressed = pressed;
            if (!log) continue;
            changed = true;
            addLog($"{Name}  Botón {b.Number,3} {(pressed ? "PULSADO" : "suelto")}");
        }

        for (var i = 0; i < Hats.Count; i++)
        {
            var h = Hats[i];
            if (_device.Povs[i] == h.Value) continue;
            h.Value = _device.Povs[i];
            if (!log) continue;
            changed = true;
            addLog($"{Name}  Hat {h.Number} {h.Text}");
        }

        if (changed) _lastActivity = nowMs;
        IsActive = nowMs - _lastActivity < ActivityMs;
    }
}
