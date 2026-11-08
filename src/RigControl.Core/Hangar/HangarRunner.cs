using System.ComponentModel;
using System.Diagnostics;

namespace RigControl.Core.Hangar;

public enum HangarStepState { Skipped, AlreadyOpen, Starting, Waiting, Ready, TimedOut, Failed, Cancelled }

public enum HangarOutcome { Ok, WithProblems, Cancelled }

/// <summary>Cómo está un programa ahora mismo, sin lanzar nada.</summary>
public enum HangarPresence { Unknown, Closed, Open, Missing }

/// <summary>Lo que el lanzador necesita de Windows; las pruebas lo sustituyen por uno falso.</summary>
public interface IProcessHost
{
    bool IsRunning(string processName);
    void Start(string target, string? args, string? workingDirectory);
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
}

/// <summary>
/// Abre en orden los programas de un hangar. Lo que ya está abierto no se vuelve a lanzar, y un
/// paso que falla no detiene los siguientes. Nunca cierra ningún programa: cancelar solo deja de
/// esperar y de lanzar.
/// </summary>
public sealed class HangarRunner(IProcessHost? host = null, int pollMs = 250)
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: el usuario rechazó el aviso de UAC

    private readonly IProcessHost _host = host ?? new ProcessHost();

    /// <summary>Índice del paso, estado y detalle. Se lanza en el hilo de la secuencia.</summary>
    public event Action<int, HangarStepState, string>? StepChanged;

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
                return HangarOutcome.Cancelled;
            }
        }
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
            Report(index, HangarStepState.AlreadyOpen, "ya estaba abierto");
            return true;
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
        if (process is not null)
        {
            var deadline = Stopwatch.StartNew();
            while (!_host.IsRunning(process))
            {
                if (deadline.Elapsed.TotalSeconds >= step.TimeoutSeconds)
                {
                    Report(index, HangarStepState.TimedOut, $"el proceso {process} no apareció en {step.TimeoutSeconds:0.#} s");
                    ok = false;
                    break;
                }
                await Task.Delay(pollMs, token);
            }
        }

        if (step.WaitSeconds > 0)
        {
            if (ok) Report(index, HangarStepState.Waiting, $"abierto, esperando {step.WaitSeconds:0.#} s…");
            await Task.Delay(TimeSpan.FromSeconds(step.WaitSeconds), token);
        }

        if (ok) Report(index, HangarStepState.Ready, process is null ? "lanzado" : "abierto");
        return ok;
    }

    private bool Fail(int index, string detail)
    {
        Report(index, HangarStepState.Failed, detail);
        return false;
    }

    private void Report(int index, HangarStepState state, string detail) => StepChanged?.Invoke(index, state, detail);
}
