using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace RigControl.Core.Hangar;

/// <summary>Warning = el programa está abierto, pero algo del paso no salió (p. ej. la prioridad).</summary>
public enum HangarStepState { Skipped, AlreadyOpen, Starting, Waiting, Ready, Warning, TimedOut, Failed, Cancelled }

public enum HangarOutcome { Ok, WithProblems, Cancelled }

/// <summary>Cómo está un programa ahora mismo, sin lanzar nada.</summary>
public enum HangarPresence { Unknown, Closed, Open, Missing }

/// <summary>Lo que el lanzador necesita de Windows; las pruebas lo sustituyen por uno falso.</summary>
public interface IProcessHost
{
    bool IsRunning(string processName);
    void Start(string target, string? args, string? workingDirectory);

    /// <summary>Identificadores de los procesos abiertos con ese nombre.</summary>
    IReadOnlyList<int> FindProcesses(string processName);

    /// <summary>
    /// Pone la prioridad a un proceso si no la tiene ya; devuelve si ha tenido que cambiarla.
    /// Win32Exception si no se deja (p. ej. elevado); InvalidOperationException si ya no existe.
    /// </summary>
    bool SetPriority(int processId, ProcessPriorityClass priority);
}

/// <summary>Procesos de verdad. ShellExecute: vale para URI y pide UAC a los programas que lo necesitan.</summary>
public sealed class ProcessHost(ProcessWindowStyle windowStyle = ProcessWindowStyle.Normal) : IProcessHost
{
    public bool IsRunning(string processName)
    {
        var found = Process.GetProcessesByName(processName);
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    }

    public void Start(string target, string? args, string? workingDirectory)
    {
        var info = new ProcessStartInfo(target) { UseShellExecute = true, WindowStyle = windowStyle };
        if (!string.IsNullOrWhiteSpace(args)) info.Arguments = args;
        if (workingDirectory is not null) info.WorkingDirectory = workingDirectory;
        using var _ = Process.Start(info);
    }

    public IReadOnlyList<int> FindProcesses(string processName)
    {
        var found = Process.GetProcessesByName(processName);
        var ids = new int[found.Length];
        for (var i = 0; i < found.Length; i++)
        {
            ids[i] = found[i].Id;
            found[i].Dispose();
        }
        return ids;
    }

    public bool SetPriority(int processId, ProcessPriorityClass priority)
    {
        Process p;
        try { p = Process.GetProcessById(processId); }
        catch (ArgumentException) { throw new InvalidOperationException("el proceso ya no está"); }
        using (p)
        {
            if (p.PriorityClass == priority) return false;
            p.PriorityClass = priority;
            return true;
        }
    }
}

/// <summary>
/// Abre en orden los programas de un hangar. Lo que ya está abierto no se vuelve a lanzar, y un
/// paso que falla no detiene los siguientes. Nunca cierra ningún programa: cancelar solo deja de
/// esperar y de lanzar.
/// La prioridad de un paso se MANTIENE mientras el programa siga abierto (también después de
/// acabar la secuencia): cada segundo se comprueban sus procesos y se corrige la que haya cambiado.
/// Hace falta porque DCS abre primero un launcher, se relanza ("--restarted") al elegir pantalla o
/// VR, y al arrancar fija él mismo su prioridad (edCore.dll llama a SetPriorityClass). La vigilancia
/// acaba cuando no hay ningún proceso con ese nombre durante priorityWatchSeconds seguidos.
/// Las ventanas ("window") se colocan en cada ventana principal NUEVA del programa: al verla y
/// durante unos segundos más (hay programas que se recolocan solos al abrirse), y no después, para
/// no pelear con lo que se haga a mano. Se buscan ventanas nuevas hasta windowWatchSeconds después
/// de acabar la secuencia (DCS se relanza al elegir VR). "focus" trae al frente, una vez, la ventana
/// del último paso que lo lleva al acabar la secuencia (o cuando aparezca, dentro de ese plazo).
/// </summary>
public sealed class HangarRunner(IProcessHost? host = null, int pollMs = 250, double priorityWatchSeconds = 60,
                                 IWindowHost? windows = null, double windowWatchSeconds = 120)
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: el usuario rechazó el aviso de UAC
    private const int ErrorAccessDenied = 5;

    private readonly IProcessHost _host = host ?? new ProcessHost();
    private readonly IWindowHost _windows = windows ?? new WindowHost();
    private readonly List<Task> _watches = [];
    private readonly ConcurrentDictionary<int, string> _warnings = []; // último aviso de cada paso
    private CancellationTokenSource? _watchCts;
    private TaskCompletionSource _sequenceEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _focusIndex = -1;

    /// <summary>Segundos sin ningún proceso del programa tras los que se deja de vigilar su prioridad.</summary>
    public double PriorityWatchSeconds => priorityWatchSeconds;

    /// <summary>Segundos tras la secuencia durante los que se siguen colocando ventanas nuevas.</summary>
    public double WindowWatchSeconds => windowWatchSeconds;

    /// <summary>Termina cuando acaban todas las vigilancias de prioridad y ventanas (o se paran). Nunca falla.</summary>
    public Task Watching
    {
        get { lock (_watches) return Task.WhenAll([.. _watches]); }
    }

    /// <summary>Deja de vigilar prioridades y ventanas (no cambia nada de lo ya puesto).</summary>
    public void StopWatching() => _watchCts?.Cancel();

    /// <summary>Índice del paso, estado y detalle. Se lanza en el hilo de la secuencia o en el de una vigilancia de prioridad.</summary>
    public event Action<int, HangarStepState, string>? StepChanged;

    public static ProcessPriorityClass ToPriorityClass(HangarPriority priority) => priority switch
    {
        HangarPriority.Idle => ProcessPriorityClass.Idle,
        HangarPriority.BelowNormal => ProcessPriorityClass.BelowNormal,
        HangarPriority.AboveNormal => ProcessPriorityClass.AboveNormal,
        HangarPriority.High => ProcessPriorityClass.High,
        _ => ProcessPriorityClass.Normal,
    };

    /// <summary>Como en el Administrador de tareas.</summary>
    public static string PriorityText(HangarPriority priority) => priority switch
    {
        HangarPriority.Idle => "Baja",
        HangarPriority.BelowNormal => "Por debajo de lo normal",
        HangarPriority.AboveNormal => "Por encima de lo normal",
        HangarPriority.High => "Alta",
        _ => "Normal",
    };

    /// <summary>Resumen legible de los ajustes de ventana, p. ej. "minimizada, en 100,200 de 1280×720, siempre encima".</summary>
    public static string WindowText(HangarWindow window)
    {
        var parts = new List<string>();
        if (window.State is { } state)
            parts.Add(state switch { HangarWindowState.Minimized => "minimizada", HangarWindowState.Maximized => "maximizada", _ => "normal" });
        if (window.HasPosition && window.HasSize) parts.Add($"en {window.X},{window.Y} de {window.Width}×{window.Height}");
        else if (window.HasPosition) parts.Add($"en {window.X},{window.Y}");
        else if (window.HasSize) parts.Add($"de {window.Width}×{window.Height}");
        if (window.Topmost is { } top) parts.Add(top ? "siempre encima" : "sin «siempre encima»");
        return string.Join(", ", parts);
    }

    public static string ExpandTarget(HangarStep step) => Environment.ExpandEnvironmentVariables(step.Target.Trim());

    /// <summary>Proceso con el que se detecta el programa; null si no se puede saber (URI sin "process").</summary>
    public static string? ProcessNameOf(HangarStep step)
    {
        var name = step.Process?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            var target = ExpandTarget(step);
            if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            name = Path.GetFileName(target);
        }
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>Estado actual del programa, para mostrarlo o para "--dry".</summary>
    public HangarPresence Inspect(HangarStep step)
    {
        var process = ProcessNameOf(step);
        if (process is not null && _host.IsRunning(process)) return HangarPresence.Open;
        var target = ExpandTarget(step);
        if (target.Length == 0 || Path.IsPathFullyQualified(target) && !File.Exists(target)) return HangarPresence.Missing;
        return process is null ? HangarPresence.Unknown : HangarPresence.Closed;
    }

    public async Task<HangarOutcome> RunAsync(HangarProfile hangar, CancellationToken token = default)
    {
        StopWatching();
        _watchCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _sequenceEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _warnings.Clear();
        lock (_watches) _watches.Clear();
        _focusIndex = hangar.Steps.FindLastIndex(s => s.Enabled && s.Focus);
        var problems = false;
        for (var i = 0; i < hangar.Steps.Count; i++)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                problems |= !await RunStepAsync(i, hangar.Steps[i], token);
            }
            catch (OperationCanceledException)
            {
                for (var j = i; j < hangar.Steps.Count; j++) Report(j, HangarStepState.Cancelled, "cancelado");
                _watchCts.Cancel(); // ni ventanas ni foco tras cancelar
                return HangarOutcome.Cancelled;
            }
        }
        _sequenceEnded.TrySetResult();
        return problems ? HangarOutcome.WithProblems : HangarOutcome.Ok;
    }

    /// <summary>Devuelve false si el paso tuvo algún problema.</summary>
    private async Task<bool> RunStepAsync(int index, HangarStep step, CancellationToken token)
    {
        if (!step.Enabled)
        {
            Report(index, HangarStepState.Skipped, "desactivado");
            return true;
        }

        var process = ProcessNameOf(step);
        if (process is not null && _host.IsRunning(process))
        {
            var (priorityOk, openText) = ApplyPriority(index, step, process);
            Report(index, priorityOk ? HangarStepState.AlreadyOpen : HangarStepState.Warning, "ya estaba abierto" + openText);
            StartWindowWatch(index, step, process);
            return priorityOk;
        }

        var target = ExpandTarget(step);
        if (target.Length == 0) return Fail(index, "sin ruta");
        var isFile = Path.IsPathFullyQualified(target);
        if (isFile && !File.Exists(target)) return Fail(index, $"no existe {target}");

        Report(index, HangarStepState.Starting, "abriendo…");
        try
        {
            _host.Start(target, step.Args, isFile ? Path.GetDirectoryName(target) : null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return Fail(index, "cancelado en el aviso de Windows (UAC)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(index, ex.Message);
        }

        var ok = true;
        var priorityText = "";
        if (process is not null)
        {
            var deadline = Stopwatch.StartNew();
            while (!_host.IsRunning(process))
            {
                if (deadline.Elapsed.TotalSeconds >= step.TimeoutSeconds)
                {
                    var late = step.Priority is null ? "" : $"; si aparece en {priorityWatchSeconds:0.#} s se le pondrá la prioridad";
                    Report(index, HangarStepState.TimedOut, $"el proceso {process} no apareció en {step.TimeoutSeconds:0.#} s{late}");
                    if (step.Priority is { } wanted) StartWatch(index, process, wanted, []);
                    StartWindowWatch(index, step, process);
                    ok = false;
                    break;
                }
                await Task.Delay(pollMs, token);
            }
            if (ok)
            {
                (ok, priorityText) = ApplyPriority(index, step, process);
                if (!ok) Report(index, HangarStepState.Warning, "abierto" + priorityText);
                StartWindowWatch(index, step, process);
            }
        }
        else if (step.Priority is not null || NeedsWindows(index, step))
        {
            ok = false;
            var what = new List<string>();
            if (step.Priority is { } wanted) what.Add($"poner la prioridad {PriorityText(wanted)}");
            if (step.Window is { IsEmpty: false }) what.Add("colocar la ventana");
            if (index == _focusIndex) what.Add("traerlo al frente");
            Report(index, HangarStepState.Warning, $"lanzado, pero sin \"process\" no se puede {string.Join(" ni ", what)}");
        }

        if (step.WaitSeconds > 0)
        {
            if (ok) Report(index, HangarStepState.Waiting, $"abierto{priorityText}, esperando {step.WaitSeconds:0.#} s…");
            await Task.Delay(TimeSpan.FromSeconds(step.WaitSeconds), token);
        }

        if (ok) Report(index, HangarStepState.Ready, process is null ? "lanzado" : "abierto" + priorityText);
        return ok;
    }

    /// <summary>
    /// Pone la prioridad del paso, si tiene, a todos sus procesos y empieza a mantenerla.
    /// Devuelve si salió bien y el texto para añadir al detalle.
    /// </summary>
    private (bool Ok, string Text) ApplyPriority(int index, HangarStep step, string process)
    {
        if (step.Priority is not { } priority) return (true, "");
        var name = PriorityText(priority);
        var seen = new HashSet<int>();
        var denied = new HashSet<int>();
        var set = 0;
        string? error = null;
        foreach (var id in _host.FindProcesses(process))
        {
            seen.Add(id);
            var (_, failure) = TrySetPriority(id, priority);
            if (failure is null) set++;
            else if (failure.Length > 0) { error ??= failure; denied.Add(id); }
        }
        StartWatch(index, process, priority, seen, denied);
        if (error is not null) return (false, $", pero no se pudo poner la prioridad {name}: {error}");
        return set > 0 ? (true, $", prioridad {name}") : (false, $", pero no se pudo poner la prioridad {name}: el proceso ya no está");
    }

    /// <summary>Failure: null = puesta (Changed = hubo que cambiarla); "" = el proceso ya no está; si no, el motivo.</summary>
    private (bool Changed, string? Failure) TrySetPriority(int processId, HangarPriority priority)
    {
        try
        {
            return (_host.SetPriority(processId, ToPriorityClass(priority)), null);
        }
        catch (InvalidOperationException) { return (false, ""); }
        catch (Win32Exception ex)
        {
            return (false, ex.NativeErrorCode == ErrorAccessDenied ? "acceso denegado (¿se ejecuta como administrador?)" : ex.Message);
        }
    }

    private void StartWatch(int index, string process, HangarPriority priority, HashSet<int> seen, HashSet<int>? denied = null)
    {
        if (priorityWatchSeconds <= 0 || _watchCts is not { } cts) return;
        var token = cts.Token;
        var task = Task.Run(() => WatchAsync(index, process, priority, seen, denied ?? [], token));
        lock (_watches) _watches.Add(task);
    }

    /// <summary>
    /// Mantiene la prioridad en todos los procesos con ese nombre (nuevos o que la hayan cambiado)
    /// hasta que pasan priorityWatchSeconds seguidos sin ninguno. Avisa una vez por proceso.
    /// </summary>
    private async Task WatchAsync(int index, string process, HangarPriority priority,
                                  HashSet<int> seen, HashSet<int> denied, CancellationToken token)
    {
        var name = PriorityText(priority);
        var corrected = new HashSet<int>();
        var absent = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                await Task.Delay(Math.Max(1, pollMs * 4), token);
                IReadOnlyList<int> ids;
                try { ids = _host.FindProcesses(process); }
                catch (Exception ex) when (ex is not OperationCanceledException) { continue; }

                if (ids.Count == 0)
                {
                    if (absent.Elapsed.TotalSeconds >= priorityWatchSeconds) return;
                    continue;
                }
                absent.Restart();

                foreach (var id in ids)
                {
                    if (denied.Contains(id)) continue;
                    var isNew = seen.Add(id);
                    var (changed, failure) = TrySetPriority(id, priority);
                    if (failure is { Length: > 0 })
                    {
                        denied.Add(id);
                        Report(index, HangarStepState.Warning, $"abierto, pero no se pudo poner la prioridad {name}: {failure}");
                    }
                    else if (failure is not null) continue; // se cerró entre medias
                    else if (isNew)
                        Report(index, HangarStepState.Ready, seen.Count > 1
                            ? $"abierto, prioridad {name} (proceso nuevo: el programa se relanzó)"
                            : $"abierto tarde, prioridad {name}");
                    else if (changed && corrected.Add(id))
                        Report(index, HangarStepState.Ready, $"abierto, prioridad {name} (el programa la cambió; se mantiene)");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private bool NeedsWindows(int index, HangarStep step) => step.Window is { IsEmpty: false } || index == _focusIndex;

    private void StartWindowWatch(int index, HangarStep step, string process)
    {
        if (!NeedsWindows(index, step) || _watchCts is not { } cts) return;
        var token = cts.Token;
        var ended = _sequenceEnded.Task;
        var focus = index == _focusIndex;
        var task = Task.Run(() => WindowWatchAsync(index, step, process, focus, ended, token));
        lock (_watches) _watches.Add(task);
    }

    /// <summary>
    /// Coloca cada ventana principal nueva del programa (al verla y durante unos segundos) y, si
    /// es el paso del foco, trae al frente la más reciente al acabar la secuencia. Termina
    /// windowWatchSeconds después de acabar la secuencia.
    /// </summary>
    private async Task WindowWatchAsync(int index, HangarStep step, string process, bool focus, Task ended, CancellationToken token)
    {
        var settings = step.Window is { IsEmpty: false } w ? w : null;
        var settleMs = pollMs * 12L; // 3 s con el sondeo normal
        var clock = Stopwatch.StartNew();
        var firstSeen = new Dictionary<nint, long>();
        var placed = new HashSet<nint>();
        var refused = new HashSet<nint>();
        long? endedAt = null;
        var focused = false;
        try
        {
            while (true)
            {
                var now = clock.ElapsedMilliseconds;
                if (ended.IsCompleted)
                {
                    endedAt ??= now;
                    if (now - endedAt > windowWatchSeconds * 1000) return;
                }

                var found = new List<nint>();
                try
                {
                    foreach (var id in _host.FindProcesses(process)) found.AddRange(_windows.MainWindows(id));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { found.Clear(); }

                foreach (var hwnd in found)
                {
                    if (!firstSeen.TryGetValue(hwnd, out var seenAt)) firstSeen[hwnd] = seenAt = now;
                    if (settings is null || refused.Contains(hwnd) || now - seenAt > settleMs) continue;
                    try
                    {
                        var result = _windows.Apply(hwnd, settings);
                        if (result == WindowApplyResult.OffScreen)
                        {
                            refused.Add(hwnd);
                            ReportWindow(index, step, true, $"la posición {settings.X},{settings.Y} no cae en ninguna pantalla: la ventana no se ha movido");
                        }
                        else if (placed.Add(hwnd))
                            ReportWindow(index, step, false, $"ventana {WindowText(settings)}");
                    }
                    catch (InvalidOperationException) { } // se cerró entre medias
                    catch (Win32Exception ex)
                    {
                        refused.Add(hwnd);
                        ReportWindow(index, step, true, ex.NativeErrorCode == ErrorAccessDenied
                            ? "no se pudo colocar la ventana: acceso denegado (¿se ejecuta como administrador?)"
                            : $"no se pudo colocar la ventana: {ex.Message}");
                    }
                }

                if (focus && !focused && ended.IsCompleted && found.Count > 0)
                {
                    // La más reciente: con DCS, la del programa relanzado y no la del launcher.
                    var target = found.MaxBy(h => firstSeen[h]);
                    focused = true;
                    var ok = false;
                    try { ok = _windows.BringToFront(target); } catch (Exception ex) when (ex is not OperationCanceledException) { }
                    ReportWindow(index, step, !ok, ok ? "traído al frente" : "no se pudo traer al frente (Windows no lo ha permitido)");
                }

                await Task.Delay(Math.Max(1, pollMs * 2), token);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Añade lo de la ventana al estado del paso sin tapar un aviso anterior (p. ej. de prioridad).</summary>
    private void ReportWindow(int index, HangarStep step, bool problem, string text)
    {
        if (_warnings.TryGetValue(index, out var warning))
            StepChanged?.Invoke(index, HangarStepState.Warning, $"{warning}; {text}"); // sin guardarlo: no se acumula
        else if (problem)
            Report(index, HangarStepState.Warning, $"abierto; {text}");
        else
            Report(index, HangarStepState.Ready,
                   step.Priority is { } p ? $"abierto, prioridad {PriorityText(p)}; {text}" : $"abierto; {text}");
    }

    private bool Fail(int index, string detail)
    {
        Report(index, HangarStepState.Failed, detail);
        return false;
    }

    private void Report(int index, HangarStepState state, string detail)
    {
        if (state is HangarStepState.Warning or HangarStepState.TimedOut or HangarStepState.Failed) _warnings[index] = detail;
        else if (state is HangarStepState.Ready or HangarStepState.AlreadyOpen) _warnings.TryRemove(index, out _);
        StepChanged?.Invoke(index, state, detail);
    }
}
