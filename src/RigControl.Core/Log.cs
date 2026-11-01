namespace RigControl.Core;

public enum LogLevel { Info, Warning, Error, Action }

public readonly record struct LogEntry(DateTime Time, LogLevel Level, string Text);

/// <summary>
/// Registro del núcleo. Core no escribe en la consola: avisa aquí y cada programa decide qué hacer
/// (la consola RigProbe lo imprime tal cual; la app lo muestra con colores). Puede llamarse desde
/// cualquier hilo (el runner va en el suyo); quien se suscribe debe pasarlo a su hilo si lo necesita.
/// </summary>
public static class Log
{
    /// <summary>Destino de los mensajes. Por defecto, la consola.</summary>
    public static Action<LogEntry> Sink { get; set; } = e => Console.WriteLine(e.Text);

    public static void Info(string text) => Write(LogLevel.Info, text);
    public static void Warning(string text) => Write(LogLevel.Warning, text);
    public static void Error(string text) => Write(LogLevel.Error, text);

    /// <summary>Una acción disparada por un binding (lo que antes imprimía ConsoleActionSink).</summary>
    public static void Action(string text) => Write(LogLevel.Action, text);

    private static void Write(LogLevel level, string text)
    {
        try { Sink(new LogEntry(DateTime.Now, level, text)); }
        catch { /* el registro nunca debe tumbar el bucle */ }
    }
}
