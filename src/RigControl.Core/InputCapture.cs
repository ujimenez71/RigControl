namespace RigControl.Core;

/// <summary>Entrada capturada: dispositivo y parte en el formato de los perfiles ("b8", "hat1.up", "y>90").</summary>
public sealed record CapturedInput(string DeviceKey, string DeviceName, string Part);

/// <summary>
/// "Pulsa el botón que quieras asignar": compara el estado de todos los mandos con el del momento
/// en que empieza y devuelve el primer cambio. Ignora el primer segundo (DirectInput da ceros al
/// principio). Botones mantenidos al empezar no cuentan hasta que se suelten y se vuelvan a pulsar.
/// Un eje cuenta si se mueve más de un 25 % y propone una zona en el extremo al que se ha llevado.
/// </summary>
public sealed class InputCapture(DeviceMonitor monitor)
{
    private const int WarmUpMs = 1000;
    private const int AxisDelta = 65535 / 4;

    private long _start = -1;
    private bool[][]? _buttons;
    private int[][]? _povs;
    private int[][]? _axes;

    /// <summary>Ya ha pasado el arranque y está esperando una entrada.</summary>
    public bool Ready => _buttons is not null;

    public CapturedInput? Poll(long timeMs)
    {
        monitor.Poll();
        var devices = monitor.Devices;
        if (_start < 0) _start = timeMs;

        if (_buttons is null)
        {
            if (timeMs - _start < WarmUpMs) return null;
            _buttons = [.. devices.Select(d => (bool[])d.Buttons.Clone())];
            _povs = [.. devices.Select(d => (int[])d.Povs.Clone())];
            _axes = [.. devices.Select(d => (int[])d.Axes.Clone())];
            return null;
        }

        for (var i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            if (!d.Ok) continue;

            for (var b = 0; b < d.Buttons.Length; b++)
            {
                if (d.Buttons[b] && !_buttons[i][b]) return new(d.Key, d.Name, $"b{b + 1}");
                if (!d.Buttons[b]) _buttons[i][b] = false; // soltado: la próxima pulsación ya cuenta
            }

            for (var p = 0; p < d.Povs.Length; p++)
            {
                var v = d.Povs[p];
                if (v >= 0 && v != _povs![i][p])
                {
                    var dir = ((v + 4500) / 9000 % 4) switch { 0 => "up", 1 => "right", 2 => "down", _ => "left" };
                    return new(d.Key, d.Name, $"hat{p + 1}.{dir}");
                }
                if (v < 0) _povs![i][p] = -1;
            }

            foreach (var a in d.AxisIndices)
            {
                if (Math.Abs(d.Axes[a] - _axes![i][a]) <= AxisDelta) continue;
                var zone = d.Axes[a] > 32767 ? ">90" : "<10";
                return new(d.Key, d.Name, MonitoredDevice.AxisNames[a].ToLowerInvariant() + zone);
            }
        }
        return null;
    }
}
