namespace RigProbe;

public static class ConsoleInput
{
    /// <summary>
    /// True si se ha pulsado ESC. Sin consola interactiva (entrada redirigida, p. ej. lanzado desde
    /// otra herramienta) no hay teclado que leer: devuelve false y el bucle sigue hasta que se detenga el proceso.
    /// </summary>
    public static bool EscapePressed() =>
        !Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape;
}
