using HapticSeat.Hf8;
using HapticSeat.Telemetry.Il2;

namespace HapticSeat.Profiles;

/// <summary>Ajustes globales (del cojín y de la app, no de un avión): %APPDATA%\HapticSeat\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Nivel 0..255 desde el que se nota cada motor (calibración con "umbral").</summary>
    public int[] MinLevels { get; set; } = [.. Hf8Motors.DefaultMinLevels];

    public int Il2Port { get; set; } = Il2Source.DefaultPort;

    /// <summary>Cambiar de perfil solo según el avión.</summary>
    public bool AutoProfile { get; set; } = true;

    /// <summary>Perfil elegido a mano la última vez.</summary>
    public string LastProfile { get; set; } = Profile.DefaultName;

    public bool StartMinimized { get; set; }

    public byte[] MinLevelBytes()
    {
        var levels = new byte[Hf8Motors.Count];
        for (var m = 0; m < levels.Length; m++)
            levels[m] = (byte)Math.Clamp(m < MinLevels.Length ? MinLevels[m] : Hf8Motors.DefaultMinLevels[m], 0, 254);
        return levels;
    }
}
