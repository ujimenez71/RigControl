using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RigControl.Core;
using RigControl.Core.Bindings;

namespace RigControl.ViewModels;

/// <summary>
/// Imagen de la aeronave de un perfil para la pantalla Perfiles. Primero una del usuario en
/// %APPDATA%\RigControl\aviones\ (con el nombre del archivo del perfil, p. ej. "ah64d.jpg", o de uno de sus
/// "aircraft", p. ej. "uh-1h.png"); si no, la ilustración de serie del tipo (Assets/Aircraft, arte propio de
/// tools/arte/rigcontrol_arte.py): AH-64, UH-1H, C172 o genéricas de caza, helicóptero y avioneta.
/// </summary>
public static class AircraftArt
{
    public static string UserDir { get; } = Path.Combine(Path.GetDirectoryName(DeviceCatalog.DefaultPath)!, "aviones");

    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>Palabras de helicópteros (DCS y MSFS) para elegir la ilustración genérica.</summary>
    private static readonly string[] HeloWords =
        ["mi-8", "mi8", "mi-24", "mi24", "ka-50", "ka50", "sa342", "gazelle", "ch-47", "ch47", "oh-58", "oh58", "uh-60", "uh60",
         "ah-6", "h135", "h125", "h145", "h160", "bell", "r44", "r66", "ec135", "cabri", "huey", "helicop", "hughes", "md500"];

    /// <summary>Tipo de ilustración de serie: ah64, uh1h, c172, helo, jet o ga.</summary>
    public static string Kind(ProfileReport p)
    {
        var all = string.Join(' ', p.Aircraft.Append(p.Name).Append(Path.GetFileNameWithoutExtension(p.Path))).ToLowerInvariant();
        if (all.Contains("ah-64") || all.Contains("ah64") || all.Contains("apache")) return "ah64";
        if (all.Contains("uh-1") || all.Contains("uh1")) return "uh1h";
        if (all.Contains("c172") || all.Contains("cessna 172") || all.Contains("172")) return "c172";
        if (HeloWords.Any(all.Contains)) return "helo";
        return p.Sim == SimKind.Dcs ? "jet" : "ga";
    }

    public static string KindText(string kind) => kind switch
    {
        "ah64" => "HELICÓPTERO DE ATAQUE",
        "uh1h" => "HELICÓPTERO UTILITARIO",
        "c172" => "AVIONETA",
        "helo" => "HELICÓPTERO",
        "jet" => "AVIÓN DE COMBATE",
        _ => "AVIACIÓN GENERAL",
    };

    /// <summary>Imagen del usuario para el perfil (o null).</summary>
    public static string? UserImage(ProfileReport p)
    {
        if (!Directory.Exists(UserDir)) return null;
        var names = new List<string> { Path.GetFileNameWithoutExtension(p.Path) };
        names.AddRange(p.Aircraft.Where(a => a != "*").Select(a => a.StartsWith("dcs:", StringComparison.OrdinalIgnoreCase) ? a[4..] : a));
        foreach (var n in names)
            foreach (var ext in Extensions)
            {
                var f = Path.Combine(UserDir, n + ext);
                if (File.Exists(f)) return f;
            }
        return null;
    }

    public static ImageSource? For(ProfileReport? p)
    {
        if (p is null) return null;
        if (UserImage(p) is { } file)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(file);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException) { }
        }
        var builtin = new BitmapImage(new Uri($"pack://application:,,,/RigControl;component/Assets/Aircraft/{Kind(p)}.jpg"));
        builtin.Freeze();
        return builtin;
    }
}
