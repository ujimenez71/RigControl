using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RigControl.Core;
using RigControl.Core.Bindings;
using RigControl.Core.Sheets;
using Vortice.DirectInput;

namespace RigControl.ViewModels;

/// <summary>Una entrada del mando en la lista de la ventana de fotos.</summary>
public sealed class LayoutInputItem(string part, string label, string usedIn) : ObservableObject
{
    private bool _placed;

    public string Part { get; } = part;
    public string Label { get; } = label;

    /// <summary>Perfiles que la usan ("" = ninguno).</summary>
    public string UsedIn { get; } = usedIn;

    public bool Placed { get => _placed; set { if (Set(ref _placed, value)) Raise(nameof(Mark)); } }
    public string Mark => _placed ? "●" : "○";
}

/// <summary>Un marcador dibujado sobre la foto (en píxeles de la imagen).</summary>
public sealed record LayoutMarker(string Part, double X, double Y, double Size, bool Selected)
{
    public double Left => X - Size / 2;
    public double Top => Y - Size / 2;
    public double FontSize => Size * 0.42;
}

/// <summary>
/// Ventana "Fotos de los mandos": por cada mando del catálogo, su foto y dónde está cada entrada (para la
/// hoja de mandos). Se elige la entrada (de la lista o pulsándola en el mando) y se hace clic en la foto.
/// Todo se guarda al momento en fotos\VVVV_PPPP.json.
/// </summary>
public sealed class DeviceLayoutViewModel : ObservableObject, IDisposable
{
    private const int MaxImageSide = 1600;

    private readonly DeviceCatalog _catalog = DeviceCatalog.Load();
    private readonly IntPtr _window;
    private CatalogEntry? _device;
    private DeviceLayout _layout = new();
    private LayoutInputItem? _selectedInput;
    private BitmapSource? _image;
    private string _status = "";

    // Captura ("Pulsar en el mando"), como en el editor de perfiles.
    private readonly DispatcherTimer _captureTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IDirectInput8? _di;
    private DeviceMonitor? _monitor;
    private InputCapture? _capture;
    private long _captureStart;
    private bool _capturing;

    public DeviceLayoutViewModel(IntPtr window, string? deviceKey = null)
    {
        _window = window;
        foreach (var d in _catalog.Devices.OrderBy(d => d.Alias, StringComparer.CurrentCultureIgnoreCase)) Devices.Add(d);
        _captureTimer.Tick += (_, _) => CaptureTick();
        Device = (deviceKey is null ? null : Devices.FirstOrDefault(d => d.Key.StartsWith(deviceKey, StringComparison.OrdinalIgnoreCase)))
                 ?? Devices.FirstOrDefault();
    }

    public ObservableCollection<CatalogEntry> Devices { get; } = [];
    public ObservableCollection<LayoutInputItem> Inputs { get; } = [];
    public ObservableCollection<LayoutMarker> Markers { get; } = [];

    public CatalogEntry? Device
    {
        get => _device;
        set
        {
            if (!Set(ref _device, value)) return;
            Load();
        }
    }

    public LayoutInputItem? SelectedInput
    {
        get => _selectedInput;
        set { if (Set(ref _selectedInput, value)) RefreshMarkers(); }
    }

    public BitmapSource? Image { get => _image; private set { if (Set(ref _image, value)) { Raise(nameof(HasImage)); Raise(nameof(NoImage)); } } }
    public bool HasImage => _image is not null;
    public bool NoImage => _image is null;
    public double ImageWidth => _image?.PixelWidth ?? 0;
    public double ImageHeight => _image?.PixelHeight ?? 0;

    public string Status { get => _status; private set => Set(ref _status, value); }

    public bool Capturing { get => _capturing; private set { if (Set(ref _capturing, value)) Raise(nameof(NotCapturing)); } }
    public bool NotCapturing => !_capturing;

    public string FolderPath => DeviceLayout.Dir;

    // ------------------------------------------------------------ carga

    private void Load()
    {
        StopCapture(null);
        Inputs.Clear();
        Markers.Clear();
        Image = null;
        if (_device is null) return;
        _layout = DeviceLayout.Load(_device.Key);
        Image = LoadImage(_layout.ImagePath);
        Raise(nameof(ImageWidth));
        Raise(nameof(ImageHeight));

        // Entradas: con nombre en el catálogo, usadas en algún perfil y ya colocadas. Hats: también el hat entero.
        var used = UsedInProfiles(_device);
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _device.Inputs?.Keys ?? Enumerable.Empty<string>()) parts.Add(p.ToLowerInvariant());
        foreach (var p in used.Keys) parts.Add(p);
        foreach (var p in _layout.Markers.Keys) parts.Add(p.ToLowerInvariant());
        foreach (var p in parts.ToList())
        {
            var g = DeviceLayout.GroupOf(p);
            if (g.StartsWith("hat", StringComparison.Ordinal) || p != g) parts.Add(g);
        }
        // Las zonas de eje ("y>95") se colocan como el eje entero ("y").
        foreach (var p in parts.Where(p => p.IndexOfAny(['<', '>', '=']) > 0).ToList()) parts.Remove(p);

        foreach (var p in parts.OrderBy(PartSortKey, StringComparer.Ordinal))
        {
            var label = _device.InputLabel(p);
            var profiles = used.Where(u => u.Key == p || DeviceLayout.GroupOf(u.Key) == p).SelectMany(u => u.Value).Distinct().Order().ToList();
            Inputs.Add(new LayoutInputItem(p, label is null ? p : $"{p} · {label}", string.Join(", ", profiles))
            {
                Placed = _layout.Markers.ContainsKey(p),
            });
        }
        SelectedInput = Inputs.FirstOrDefault(i => !i.Placed && i.UsedIn.Length > 0) ?? Inputs.FirstOrDefault(i => !i.Placed) ?? Inputs.FirstOrDefault();
        RefreshMarkers();
        Status = Image is null
            ? "Este mando no tiene foto: pulsa «Cargar foto…» (JPG o PNG; se reduce a 1600 px)."
            : $"{_layout.Markers.Count} de {Inputs.Count} entradas colocadas. Elige una entrada y haz clic en la foto; clic derecho en un marcador lo quita.";
    }

    /// <summary>Partes usadas por los perfiles con este mando -> nombres de los perfiles.</summary>
    private static Dictionary<string, List<string>> UsedInProfiles(CatalogEntry device)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(ProfileFile.ProfilesDir)) return result;
        foreach (var path in Directory.EnumerateFiles(ProfileFile.ProfilesDir, "*.json"))
        {
            ProfileFile file;
            try { file = ProfileFile.Load(path); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException) { continue; }
            var aliases = file.Devices.Where(d => device.Key.Equals(d.Value.Trim(), StringComparison.OrdinalIgnoreCase)
                                                  || device.Key.StartsWith(d.Value.Trim() + ":", StringComparison.OrdinalIgnoreCase))
                                      .Select(d => d.Key + ":").ToList();
            if (aliases.Count == 0) continue;
            var name = Path.GetFileNameWithoutExtension(path);
            foreach (var b in file.Bindings)
                foreach (var input in b.Sequence ?? (b.Input is null ? [] : [b.Input]))
                    if (aliases.FirstOrDefault(a => input.StartsWith(a, StringComparison.OrdinalIgnoreCase)) is { } a)
                    {
                        var part = input[a.Length..].Trim().ToLowerInvariant();
                        if (!result.TryGetValue(part, out var list)) result[part] = list = [];
                        if (!list.Contains(name)) list.Add(name);
                    }
        }
        return result;
    }

    private static string PartSortKey(string part)
    {
        var g = DeviceLayout.GroupOf(part);
        var kind = g.StartsWith('b') ? "0" : g.StartsWith("hat", StringComparison.Ordinal) ? "1" : "2";
        var n = int.TryParse(new string(g.Where(char.IsDigit).ToArray()), out var v) ? v : 0;
        var dir = part.Length > g.Length ? part[g.Length..] switch { ".up" => "1", ".right" => "2", ".down" => "3", ".left" => "4", var x => x } : "0";
        return $"{kind}{g.TrimEnd("0123456789".ToCharArray())}{n:D4}{dir}";
    }

    private static BitmapSource? LoadImage(string? path)
    {
        if (path is null) return null;
        try
        {
            // Sin bloquear el archivo (se puede sustituir con la ventana abierta).
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
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

    private void RefreshMarkers()
    {
        Markers.Clear();
        if (_image is null) return;
        var size = Math.Max(18, Math.Min(_image.PixelWidth, _image.PixelHeight) / 22.0);
        foreach (var (part, pos) in _layout.Markers.OrderBy(m => PartSortKey(m.Key), StringComparer.Ordinal))
            Markers.Add(new LayoutMarker(part, pos.X * _image.PixelWidth, pos.Y * _image.PixelHeight, size,
                                         string.Equals(part, _selectedInput?.Part, StringComparison.OrdinalIgnoreCase)));
    }

    // ------------------------------------------------------------ foto

    /// <summary>Copia la foto a fotos\ (girada según el EXIF del móvil y reducida a 1600 px) y la asigna al mando.</summary>
    public void ImportImage(string source)
    {
        if (_device is null) return;
        try
        {
            var frame = BitmapFrame.Create(new Uri(source), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource bmp = frame;
            var angle = ExifRotation(frame);
            if (angle != 0) bmp = new TransformedBitmap(bmp, new RotateTransform(angle));
            var scale = Math.Min(1.0, (double)MaxImageSide / Math.Max(bmp.PixelWidth, bmp.PixelHeight));
            if (scale < 1) bmp = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));

            Directory.CreateDirectory(DeviceLayout.Dir);
            var name = DeviceLayout.IdFor(_device.Key) + ".jpg";
            var target = Path.Combine(DeviceLayout.Dir, name);
            var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            var tmp = target + ".tmp";
            using (var f = File.Create(tmp)) encoder.Save(f);
            File.Move(tmp, target, overwrite: true);

            _layout.Image = name;
            _layout.Save(_device.Key);
            Load();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException or ArgumentException)
        {
            Status = $"No se pudo cargar la foto: {ex.Message}";
        }
    }

    /// <summary>Giro que pide la etiqueta Orientation del EXIF (las fotos de móvil vienen tumbadas sin ella).</summary>
    private static double ExifRotation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata m && m.GetQuery("/app1/ifd/{ushort=274}") is ushort o)
                return o switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException) { }
        return 0;
    }

    // ------------------------------------------------------------ marcadores

    /// <summary>Clic en la foto (en píxeles de la imagen): coloca la entrada elegida y pasa a la siguiente sin colocar.</summary>
    public void PlaceAt(double x, double y)
    {
        if (_device is null || _image is null || _selectedInput is null) return;
        var item = _selectedInput;
        _layout.Markers[item.Part] = new MarkerPosition(Math.Round(Math.Clamp(x / _image.PixelWidth, 0, 1), 4),
                                                        Math.Round(Math.Clamp(y / _image.PixelHeight, 0, 1), 4));
        Save();
        item.Placed = true;
        var index = Inputs.IndexOf(item);
        SelectedInput = Inputs.Skip(index + 1).FirstOrDefault(i => !i.Placed) ?? item;
        RefreshMarkers();
        Status = $"{item.Part} colocada. " + (SelectedInput == item ? "No quedan entradas sin colocar después de esta." : $"Siguiente: {SelectedInput!.Label}");
    }

    public void SelectMarker(string part)
    {
        SelectedInput = Inputs.FirstOrDefault(i => i.Part.Equals(part, StringComparison.OrdinalIgnoreCase)) ?? SelectedInput;
    }

    public void RemoveMarker(string part)
    {
        if (_device is null || !_layout.Markers.Remove(part)) return;
        Save();
        if (Inputs.FirstOrDefault(i => i.Part.Equals(part, StringComparison.OrdinalIgnoreCase)) is { } item)
        {
            item.Placed = false;
            SelectedInput = item;
        }
        RefreshMarkers();
        Status = $"{part} quitada.";
    }

    private void Save()
    {
        try
        {
            _layout.Save(_device!.Key);
        }
        catch (IOException ex)
        {
            Status = $"No se pudo guardar: {ex.Message}";
        }
    }

    // ------------------------------------------------------------ captura

    public void StartCapture()
    {
        try
        {
            _di ??= DInput.DirectInput8Create();
            _monitor ??= DeviceMonitor.Open(_di, _window);
        }
        catch (Exception ex)
        {
            Status = $"No se pueden abrir los mandos: {ex.Message}";
            return;
        }
        _capture = new InputCapture(_monitor);
        _captureStart = _clock.ElapsedMilliseconds;
        Capturing = true;
        Status = "Preparando los mandos…";
        _captureTimer.Start();
    }

    private void CaptureTick()
    {
        var now = _clock.ElapsedMilliseconds;
        var got = _capture!.Poll(now);
        if (_capture.Ready && Status.StartsWith("Preparando", StringComparison.Ordinal))
            Status = "Pulsa el botón, mueve el hat o lleva un eje a un extremo…";
        if (got is null)
        {
            if (now - _captureStart > 15_000) StopCapture("No se ha pulsado nada (15 s).");
            return;
        }

        StopCapture(null);
        var device = Devices.FirstOrDefault(d => d.Key.Equals(got.DeviceKey, StringComparison.OrdinalIgnoreCase)
                                                 || d.Key.StartsWith(got.DeviceKey + ":", StringComparison.OrdinalIgnoreCase)
                                                 || got.DeviceKey.StartsWith(d.Key + ":", StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            Status = $"{got.DeviceName} no está en el catálogo.";
            return;
        }
        if (device != _device) Device = device;
        var part = got.Part.IndexOfAny(['<', '>', '=']) > 0 ? DeviceLayout.GroupOf(got.Part) : got.Part.ToLowerInvariant();
        var item = Inputs.FirstOrDefault(i => i.Part == part);
        if (item is null)
        {
            var label = device.InputLabel(part);
            item = new LayoutInputItem(part, label is null ? part : $"{part} · {label}", "");
            var at = Inputs.TakeWhile(i => string.CompareOrdinal(PartSortKey(i.Part), PartSortKey(part)) < 0).Count();
            Inputs.Insert(at, item);
        }
        SelectedInput = item;
        Status = $"Pulsado: {item.Label}. Haz clic en la foto donde está.";
    }

    private void StopCapture(string? message)
    {
        _captureTimer.Stop();
        _capture = null;
        Capturing = false;
        if (message is not null) Status = message;
    }

    public void Dispose()
    {
        _captureTimer.Stop();
        _monitor?.Dispose();
        _di?.Dispose();
    }
}
