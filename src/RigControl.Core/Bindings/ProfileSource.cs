namespace RigControl.Core.Bindings;

/// <summary>
/// De dónde sale el perfil activo. El runner pregunta en cada ciclo (Poll) si hay que cambiarlo;
/// todo ocurre en el hilo del bucle, así que no hace falta sincronizar nada más.
/// </summary>
public abstract class ProfileSource : IDisposable
{
    public abstract CompiledProfile Initial { get; }

    /// <summary>Devuelve el perfil al que cambiar, o null si sigue el actual. No debe bloquear.</summary>
    public abstract CompiledProfile? Poll(long timeMs);

    public virtual void Dispose() { }
}

/// <summary>Un archivo concreto ("run perfil"): se recarga al guardarlo; si tiene errores, sigue el anterior.</summary>
public sealed class FileProfileSource(string path, CompiledProfile initial) : ProfileSource
{
    private readonly FileWatch _watch = new(path, Path.GetFileName(path));

    public override CompiledProfile Initial => initial;

    public override CompiledProfile? Poll(long timeMs)
    {
        if (!_watch.Due(timeMs)) return null;

        Log.Info($"\n---- El perfil ha cambiado, recargando ----");
        try
        {
            var profile = ProfileLoader.LoadAndCheck(path);
            if (profile is null) Log.Warning("Se mantiene el perfil anterior.\n");
            return profile;
        }
        catch (IOException ex)
        {
            Log.Warning($"No se pudo leer ({ex.Message}); reintentando...");
            _watch.Retry(timeMs);
            return null;
        }
    }

    public override void Dispose() => _watch.Dispose();
}

/// <summary>
/// "run auto": todos los perfiles de una carpeta. Al cargar un avión en MSFS se activa el primero
/// (por nombre de archivo) que tenga un trozo de su ruta en "aircraft"; si no, el que tenga "*";
/// si no, ninguno. Al guardar cualquier perfil se recarga la carpeta y se vuelve a elegir.
/// </summary>
public sealed class AutoProfileSource : ProfileSource
{
    private static readonly CompiledProfile None = CompiledProfile.Empty("(ningún perfil)");

    private readonly string _dir;
    private readonly FileWatch _watch;
    private readonly SortedDictionary<string, CompiledProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private string? _aircraft;
    private bool _aircraftChanged;
    private CompiledProfile _current;

    public AutoProfileSource(string dir, SimConnectOutput sim)
    {
        _dir = dir;
        _watch = new FileWatch(Path.Combine(dir, "x.json"), "*.json");
        sim.AircraftChanged += path => { _aircraft = path; _aircraftChanged = true; };

        LoadAll(keepPrevious: false);
        _current = Select(announce: true);
    }

    public override CompiledProfile Initial => _current;

    public override CompiledProfile? Poll(long timeMs)
    {
        var reloaded = false;
        if (_watch.Due(timeMs))
        {
            Log.Info($"\n---- Cambios en los perfiles, recargando la carpeta ----");
            try
            {
                LoadAll(keepPrevious: true);
                reloaded = true;
            }
            catch (IOException ex)
            {
                Log.Warning($"No se pudo leer ({ex.Message}); reintentando...");
                _watch.Retry(timeMs);
            }
        }

        if (!reloaded && !_aircraftChanged) return null;
        _aircraftChanged = false;

        var next = Select(announce: false);
        // Tras recargar la carpeta, el perfil elegido es un objeto nuevo aunque sea el mismo archivo:
        // se cambia siempre. Por cambio de avión, solo si cambia el archivo.
        if (!reloaded && next.SourcePath == _current.SourcePath) return null;

        _current = next;
        Announce(next);
        return next;
    }

    public override void Dispose() => _watch.Dispose();

    private void LoadAll(bool keepPrevious)
    {
        var files = Directory.GetFiles(_dir, "*.json");
        var previous = new Dictionary<string, CompiledProfile>(_profiles, StringComparer.OrdinalIgnoreCase);
        _profiles.Clear();

        foreach (var file in files)
        {
            var key = Path.GetFullPath(file);
            var profile = ProfileLoader.LoadAndCheck(file, verbose: false);
            if (profile is null && keepPrevious && previous.TryGetValue(key, out var old))
            {
                Log.Warning($"  {Path.GetFileName(file)}: se mantiene la versión anterior.");
                profile = old;
            }
            if (profile is not null) _profiles[key] = profile;
        }

        Log.Info($"Perfiles en {_dir}:");
        if (_profiles.Count == 0) Log.Info("  (ninguno válido)");
        foreach (var (path, p) in _profiles)
        {
            var aircraft = p.Aircraft.Length == 0 ? "sin \"aircraft\": solo uso manual" : string.Join(", ", p.Aircraft);
            Log.Info($"  {Path.GetFileName(path),-24} \"{p.Name}\"  aviones: {aircraft}");
        }
    }

    private CompiledProfile Select(bool announce)
    {
        var chosen =
            (_aircraft is null ? null : _profiles.Values.FirstOrDefault(p => p.Aircraft.Any(a => a != "*" && _aircraft.Contains(a))))
            ?? _profiles.Values.FirstOrDefault(p => p.Aircraft.Contains("*"))
            ?? None;
        if (announce) Announce(chosen);
        return chosen;
    }

    private void Announce(CompiledProfile p)
    {
        var aircraft = _aircraft ?? "(todavía sin avión: esperando a MSFS)";
        var file = p.SourcePath is null ? "" : $" ({Path.GetFileName(p.SourcePath)})";
        Log.Info($"\n==== Avión: {aircraft}");
        Log.Info(ReferenceEquals(p, None)
            ? "                ==== Ningún perfil encaja con este avión: mandos sin bindings."
            : $"                ==== Perfil activo: \"{p.Name}\"{file}");
    }
}

/// <summary>
/// Vigila un archivo o un patrón en una carpeta. El watcher avisa desde otro hilo; aquí solo se anota
/// cuándo, y Due dice si ya han pasado DebounceMs desde el último aviso (los editores guardan en varios pasos).
/// </summary>
public sealed class FileWatch : IDisposable
{
    private const int DebounceMs = 300;

    private readonly FileSystemWatcher _watcher;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private long _changedAt = -1;

    /// <param name="path">Archivo (o cualquier ruta dentro de la carpeta a vigilar).</param>
    /// <param name="filter">Nombre del archivo o patrón, p. ej. "*.json".</param>
    public FileWatch(string path, string filter)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        _watcher = new FileSystemWatcher(dir, filter)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        // Changed: guardado directo. Created/Renamed/Deleted: editores que guardan en un temporal y renombran,
        // o archivos nuevos/borrados en la carpeta.
        FileSystemEventHandler mark = (_, _) => Interlocked.Exchange(ref _changedAt, _clock.ElapsedMilliseconds);
        _watcher.Changed += mark;
        _watcher.Created += mark;
        _watcher.Deleted += mark;
        _watcher.Renamed += (_, _) => Interlocked.Exchange(ref _changedAt, _clock.ElapsedMilliseconds);
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>True una vez por tanda de cambios, cuando ya lleva DebounceMs en calma.</summary>
    public bool Due(long timeMs)
    {
        // El reloj del runner y el nuestro arrancan en momentos distintos: se usa siempre el nuestro.
        var now = _clock.ElapsedMilliseconds;
        var changed = Interlocked.Read(ref _changedAt);
        if (changed < 0 || now - changed < DebounceMs) return false;
        // Si llega otro aviso mientras se recarga, no se pierde: CompareExchange no lo pisa.
        Interlocked.CompareExchange(ref _changedAt, -1, changed);
        return true;
    }

    /// <summary>Volver a intentarlo dentro de DebounceMs (archivo bloqueado por el editor).</summary>
    public void Retry(long timeMs) => Interlocked.CompareExchange(ref _changedAt, _clock.ElapsedMilliseconds, -1);

    public void Dispose() => _watcher.Dispose();
}
