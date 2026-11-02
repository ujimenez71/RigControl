namespace RigControl.Core.Bindings;

public enum Gesture { Press, Release, Short, Long, Hold, Double, Sequence }

/// <summary>Trigger = una vez; Start/End = inicio y fin de una pulsación mantenida (hold).</summary>
public enum ActionPhase { Trigger, Start, End }

public enum InputKind : byte { Button, HatUp, HatRight, HatDown, HatLeft, AxisZone }

/// <summary>
/// Entrada física: dispositivo (índice en CompiledProfile.DeviceKeys), tipo e índice base 0.
/// En una zona de eje, Index es el eje (0..7: X, Y, Z, RX, RY, RZ, S0, S1) y Lo..Hi el rango en
/// unidades brutas (0..65535, ambos incluidos).
/// </summary>
public readonly record struct InputRef(int Device, InputKind Kind, int Index, int Lo = 0, int Hi = 0);

/// <summary>
/// Zona de un eje como entrada virtual: "pulsada" mientras el eje está dentro.
/// Histéresis: para entrar hay que meterse <c>hysteresis</c> unidades dentro del borde; para salir
/// basta con cruzarlo. Así el ruido junto al borde no la hace parpadear y dos zonas contiguas
/// (0..33 y 33..66) nunca están activas a la vez. Los extremos del recorrido (0 y 65535) no llevan
/// margen, porque el eje no puede pasar de ahí.
/// </summary>
public static class AxisZone
{
    public const int Max = 65535;

    public static bool Next(bool active, int value, int lo, int hi, int hysteresis)
    {
        if (active) return value >= lo && value <= hi;
        return (lo == 0 || value >= lo + hysteresis) && (hi == Max || value <= hi - hysteresis);
    }

    /// <summary>False si la zona es tan estrecha que, con la histéresis, nunca se podría entrar.</summary>
    public static bool IsReachable(int lo, int hi, int hysteresis) =>
        (lo == 0 ? 0 : lo + hysteresis) <= (hi == Max ? Max : hi - hysteresis);
}

public sealed class Binding
{
    /// <summary>Posición en el JSON, base 1, para los mensajes.</summary>
    public required int Number { get; init; }

    public required Gesture Gesture { get; init; }

    /// <summary>Huecos de entrada del motor: uno, o varios en orden si es una secuencia.</summary>
    public required int[] Inputs { get; init; }

    public Condition? When { get; init; }
    /// <summary>Texto de la acción tal como está en el JSON (para mostrarla).</summary>
    public required string Action { get; init; }

    /// <summary>"desc" del perfil (null si no tiene).</summary>
    public string? Description { get; init; }

    /// <summary>La acción ya interpretada: qué se envía (SimConnect, teclado) o si solo se imprime.</summary>
    public required ActionSpec Output { get; init; }

    /// <summary>En un hold con acción de simulador: reenvío cada tantos ms mientras se mantiene (0 = no).</summary>
    public int RepeatMs { get; init; }

    /// <summary>Descripción legible del disparador, p. ej. "bravo:b3 short".</summary>
    public required string Trigger { get; init; }

    // Estado del motor (cada binding pertenece a un solo disparador, así que vive aquí sin asignar nada).
    internal bool Matched;
    internal bool HoldActive;

    public override string ToString() =>
        When is null ? $"#{Number} {Trigger}" : $"#{Number} {Trigger} si {When.Describe()}";
}

public sealed class CompiledProfile
{
    public required string Name { get; init; }
    public required TimingFile Timing { get; init; }

    /// <summary>Trozos de ruta de avión en minúsculas ("*" = comodín). Vacío = solo para uso manual.</summary>
    public string[] Aircraft { get; init; } = [];

    /// <summary>Archivo del que se cargó (lo rellena ProfileLoader).</summary>
    public string? SourcePath { get; set; }

    /// <summary>Luces del Bravo que dependen del simulador.</summary>
    public LightRule[] Lights { get; init; } = [];

    /// <summary>Variables del simulador que hay que vigilar para las luces (el índice es el de LightCondition.Var).</summary>
    public SimVarSpec[] WatchedVars { get; init; } = [];

    /// <summary>Perfil sin bindings, para cuando ningún perfil encaja con el avión.</summary>
    public static CompiledProfile Empty(string name) => new()
    {
        Name = name, Timing = new TimingFile(), AxisHysteresis = 0,
        DeviceAliases = [], DeviceKeys = [], Inputs = [], InputNames = [], Bindings = [],
    };

    /// <summary>Histéresis de las zonas de eje, en unidades brutas (0..65535).</summary>
    public required int AxisHysteresis { get; init; }

    /// <summary>Alias locales y claves de dispositivo, en paralelo.</summary>
    public required string[] DeviceAliases { get; init; }
    public required string[] DeviceKeys { get; init; }

    /// <summary>Todas las entradas que el motor vigila (disparadores y condiciones), por hueco.</summary>
    public required InputRef[] Inputs { get; init; }
    public required string[] InputNames { get; init; }

    public required Binding[] Bindings { get; init; }
}
