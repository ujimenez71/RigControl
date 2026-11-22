using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using RigControl.Core;
using RigControl.Core.Sheets;

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

/// <summary>
/// Marcador de una entrada sobre la foto del mando (de "Fotos de los mandos"), en píxeles de la imagen.
/// Se enciende mientras su botón está pulsado o su hat apunta en esa dirección (o a cualquiera, el hat entero).
/// </summary>
public sealed class PhotoMarkerViewModel(string text, string tip, double x, double y, double size) : ObservableObject
{
    private bool _isActive;
    public string Text { get; } = text;
    public string Tip { get; } = tip;
    public double Size { get; } = size;
    public double Left { get; } = x - size / 2;
    public double Top { get; } = y - size / 2;
    public double FontSize => Size * 0.45;
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    // Qué lo enciende: botón (índice base 0), hat (índice) con dirección (-1 = cualquiera) o nada (ejes).
    internal int Button = -1;
    internal int Hat = -1;
    internal int HatDir = -1;
}

/// <summary>Un mando en la pantalla Dispositivos: sus ejes, botones y hats, y si ha habido actividad reciente.</summary>
public sealed class DeviceViewModel : ObservableObject
{
    /// <summary>Cambio mínimo de un eje para anotarlo en el registro (~2 %, filtra el ruido).</summary>
    private const int AxisLogThreshold = 1300;
    private const int ActivityMs = 400;

    private readonly MonitoredDevice _device;
    private long _lastActivity = long.MinValue / 2;
    private bool _isActive;
    private bool _ok = true;

    private readonly CatalogEntry? _entry;
    private bool _photoLoaded;
    private BitmapSource? _photo;

    public DeviceViewModel(MonitoredDevice device, CatalogEntry? entry = null)
    {
        _device = device;
        _entry = entry;
        Manufacturer = DeviceManufacturer.ForKey(device.Key);
        Axes = [.. device.AxisIndices.Select(i => new AxisViewModel(MonitoredDevice.AxisNames[i]))];
        Buttons = [.. Enumerable.Range(1, device.ButtonCount).Select(n => new ButtonViewModel(n))];
        Hats = [.. Enumerable.Range(1, device.PovCount).Select(n => new HatViewModel(n))];
    }

    public string Name => _device.Name;

    /// <summary>Fabricante (por el VID), o null si no se sabe.</summary>
    public string? Manufacturer { get; }

    /// <summary>Nombre con el fabricante delante, salvo que ya lo lleve ("WinWing Throttle F-15EX").</summary>
    public string DisplayName => DeviceManufacturer.WithName(Manufacturer, Name);

    // ------------------------------------------------------------ foto

    public ObservableCollection<PhotoMarkerViewModel> PhotoMarkers { get; } = [];
    public BitmapSource? Photo { get => _photo; private set { if (Set(ref _photo, value)) { Raise(nameof(HasPhoto)); Raise(nameof(NoPhoto)); Raise(nameof(NoMarkers)); Raise(nameof(PhotoWidth)); Raise(nameof(PhotoHeight)); } } }
    public bool HasPhoto => _photo is not null;
    public bool NoPhoto => _photo is null;
    /// <summary>Hay foto pero ninguna entrada colocada en ella.</summary>
    public bool NoMarkers => _photo is not null && PhotoMarkers.Count == 0;
    public double PhotoWidth => _photo?.PixelWidth ?? 0;
    public double PhotoHeight => _photo?.PixelHeight ?? 0;

    /// <summary>Carga la foto y sus marcadores la primera vez que hace falta (al elegir el mando), o de nuevo si force.</summary>
    public void EnsurePhoto(bool force = false)
    {
        if (_photoLoaded && !force) return;
        _photoLoaded = true;
        PhotoMarkers.Clear();
        var layout = DeviceLayout.Load(Key);
        var photo = LoadImage(layout.ImagePath);
        Photo = photo;
        if (photo is null) return;

        var w = photo.PixelWidth;
        var h = photo.PixelHeight;
        var size = Math.Max(22, Math.Min(w, h) / 22.0);
        foreach (var (part, pos) in layout.Markers)
        {
            var p = part.ToLowerInvariant();
            var label = _entry?.InputLabel(p);
            var m = new PhotoMarkerViewModel(ShortText(p), label is null ? p : $"{p} · {label}", pos.X * w, pos.Y * h, size);
            if (p.StartsWith('b') && int.TryParse(p[1..], out var b)) m.Button = b - 1;
            else if (p.StartsWith("hat", StringComparison.Ordinal))
            {
                var dot = p.IndexOf('.');
                if (int.TryParse(dot < 0 ? p[3..] : p[3..dot], out var n)) m.Hat = n - 1;
                m.HatDir = dot < 0 ? -1 : p[(dot + 1)..] switch { "up" => 0, "right" => 1, "down" => 2, "left" => 3, _ => -2 };
            }
            PhotoMarkers.Add(m);
        }
        Raise(nameof(NoMarkers));
        UpdateMarkers();
    }

    /// <summary>"b12" -> "12", "hat1.up" -> "↑", "hat2" -> "H2", "y" -> "Y".</summary>
    private static string ShortText(string part)
    {
        if (part.StartsWith('b') && part.Length > 1 && char.IsDigit(part[1])) return part[1..];
        if (part.StartsWith("hat", StringComparison.Ordinal))
        {
            var dot = part.IndexOf('.');
            return dot < 0 ? "H" + part[3..] : part[(dot + 1)..] switch { "up" => "↑", "right" => "→", "down" => "↓", "left" => "←", _ => "?" };
        }
        return part.ToUpperInvariant();
    }

    private void UpdateMarkers()
    {
        foreach (var m in PhotoMarkers)
        {
            if (m.Button >= 0) m.IsActive = m.Button < Buttons.Count && Buttons[m.Button].IsPressed;
            else if (m.Hat >= 0 && m.Hat < Hats.Count)
            {
                var v = Hats[m.Hat].Value;
                m.IsActive = v >= 0 && (m.HatDir == -1 || HatPoints(v, m.HatDir));
            }
        }
    }

    /// <summary>¿El hat (centésimas de grado) apunta hacia esa dirección (0 arriba, 1 derecha...)? Las diagonales cuentan para las dos.</summary>
    private static bool HatPoints(int value, int dir)
    {
        var diff = Math.Abs(value - dir * 9000) % 36000;
        return Math.Min(diff, 36000 - diff) <= 4500;
    }

    private static BitmapSource? LoadImage(string? path)
    {
        if (path is null) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // sin bloquear el archivo
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException)
        {
            return null;
        }
    }
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
            addLog($"{DisplayName}  Eje {a.Name,-2} {raw,6}  ({a.Percent,5:0.0} %)");
        }

        for (var i = 0; i < Buttons.Count; i++)
        {
            var b = Buttons[i];
            var pressed = _device.Buttons[i];
            if (pressed == b.IsPressed) continue;
            b.IsPressed = pressed;
            if (!log) continue;
            changed = true;
            addLog($"{DisplayName}  Botón {b.Number,3} {(pressed ? "PULSADO" : "suelto")}");
        }

        for (var i = 0; i < Hats.Count; i++)
        {
            var h = Hats[i];
            if (_device.Povs[i] == h.Value) continue;
            h.Value = _device.Povs[i];
            if (!log) continue;
            changed = true;
            addLog($"{DisplayName}  Hat {h.Number} {h.Text}");
        }

        if (changed) _lastActivity = nowMs;
        if (changed || !log) UpdateMarkers();
        IsActive = nowMs - _lastActivity < ActivityMs;
    }
}
