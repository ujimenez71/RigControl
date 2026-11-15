using RigControl.Core.Graphics;

namespace RigControl.Tests;

/// <summary>
/// Perfiles gráficos: options.lua, autoexec.cfg y UserCfg.opt editados en su sitio (el resto del archivo igual),
/// diferencias, aplicar con copia, simulador abierto = no se toca, y las reglas de VR. En una carpeta temporal.
/// </summary>
internal static class GraphicsTests
{
    private const string Options = "options = {\n\t[\"VR\"] = {\n\t\t[\"enable\"] = false,\n\t\t[\"openxr_quadView\"] = true,\n\t\t[\"pixel_density\"] = 1,\n\t},\n" +
                                   "\t[\"graphics\"] = {\n\t\t[\"AA\"] = \"DLAA\",\n\t\t[\"Scaling\"] = 0.66,\n\t\t[\"maxFPS\"] = 90,\n\t\t[\"shadows\"] = 4,\n\t\t[\"sync\"] = false,\n\t},\n" +
                                   "\t[\"plugins\"] = {\n\t\t[\"A-10C\"] = {\n\t\t\t[\"CPLocalList\"] = \"default\",\n\t\t},\n\t},\n}\n";

    private const string UserCfg = "Version 66\n{Video\n\tAdapter \"NVIDIA GeForce RTX 4090\"\n\tAntiAliasing DLSS\n\tDLSSModeVR QUALITY\n\tSharpenAmountVR 0.000000\n}\n" +
                                   "{GraphicsVR\n\tVersion 2.1.0\n\t{Shadows\n\t\tSize 1536\n\t}\n\t{DOF\n\t\tEnabled 1\n\t}\n}\n" +
                                   "{Graphics\n\t{Shadows\n\t\tSize 4096\n\t}\n}\n";

    public static int Run()
    {
        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FALLO")} {what}"); if (!ok) fails++; }

        // --- options.lua
        var lua = new LuaOptionsFile(Options);
        Check(lua.Get("graphics.AA") == "\"DLAA\"" && lua.Get("VR.pixel_density") == "1" && lua.Get("plugins.A-10C.CPLocalList") == "\"default\"",
              "options.lua: valores por clave con punto (también anidados)");
        lua.Set("graphics.shadows", "2");
        Check(lua.Text == Options.Replace("[\"shadows\"] = 4", "[\"shadows\"] = 2"), "options.lua: cambiar un valor no toca nada más");
        lua.Set("graphics.newKey", "true");
        Check(lua.Text.Contains("\t\t[\"sync\"] = false,\n\t\t[\"newKey\"] = true,\n\t},") && lua.Get("graphics.newKey") == "true",
              "options.lua: clave nueva al final de su tabla, con la misma sangría");
        var threw = false;
        try { lua.Set("graphics.AA", "os.execute('x')"); } catch (InvalidDataException) { threw = true; }
        Check(threw, "options.lua: solo literales simples (nada de código Lua)");
        threw = false;
        try { lua.Set("noexiste.x", "1"); } catch (InvalidDataException) { threw = true; }
        Check(threw, "options.lua: tabla inexistente = error");
        Check(new LuaOptionsFile("-- comentario\noptions = {\n\t[\"a\"] = -1.5e-3, -- otro\n\t[\"b\"] = \"x\\\"y\",\n}\n").Get("a") == "-1.5e-3",
              "options.lua: comentarios, negativos y comillas escapadas");

        // --- autoexec.cfg
        var auto = new AutoexecFile("-- mío\nDLSS_Preset = 'K'\nHUD_MFD_after_DLSS = true -- nota");
        auto.Set("HUD_MFD_after_DLSS", "false");
        auto.Set("nuevo", "3");
        Check(auto.Text == "-- mío\nDLSS_Preset = 'K'\nHUD_MFD_after_DLSS = false -- nota\nnuevo = 3", "autoexec.cfg: cambia el valor, conserva comentarios y el final sin salto");

        // --- UserCfg.opt
        var cfg = new UserCfgFile(UserCfg);
        Check(cfg.Get("GraphicsVR.Shadows.Size") == "1536" && cfg.Get("Graphics.Shadows.Size") == "4096" && cfg.Get("Video.Adapter") == "\"NVIDIA GeForce RTX 4090\"",
              "UserCfg.opt: bloques anidados con clave con punto");
        cfg.Set("GraphicsVR.Shadows.Size", "1024");
        Check(cfg.Text == UserCfg.Replace("\t\tSize 1536", "\t\tSize 1024"), "UserCfg.opt: cambiar un valor no toca nada más");

        // --- GraphicsManager en carpeta temporal
        var dir = Path.Combine(Path.GetTempPath(), $"rigcontrol-graficos-{Guid.NewGuid():N}");
        var dcs = Path.Combine(dir, "Config");
        Directory.CreateDirectory(dcs);
        File.WriteAllText(Path.Combine(dcs, "options.lua"), Options);
        File.WriteAllText(Path.Combine(dir, "UserCfg.opt"), UserCfg);
        var running = false;
        try
        {
            var m = new GraphicsManager(dcs, Path.Combine(dir, "UserCfg.opt"), _ => running);
            var current = m.Current("dcs");
            Check(current.ContainsKey("graphics.AA") && current.ContainsKey("VR.pixel_density") && !current.Keys.Any(k => k.StartsWith("plugins.")),
                  "DCS: un perfil lleva graphics.* y VR.* (no plugins ni lo demás)");
            var vrMsfs = m.Current("msfs");
            Check(vrMsfs.ContainsKey("Video.DLSSModeVR") && vrMsfs.ContainsKey("GraphicsVR.Shadows.Size") && !vrMsfs.ContainsKey("Video.AntiAliasing")
                  && !vrMsfs.ContainsKey("Graphics.Shadows.Size") && !vrMsfs.ContainsKey("Video.Adapter"),
                  "MSFS VR: solo los ajustes de VR (ni monitor ni tarjeta gráfica)");
            var mon = m.Current("msfs", vr: false);
            Check(mon.ContainsKey("Video.AntiAliasing") && mon.ContainsKey("Graphics.Shadows.Size") && !mon.ContainsKey("Video.DLSSModeVR") && !mon.ContainsKey("Video.Adapter"),
                  "MSFS monitor: lo contrario, y sin la tarjeta gráfica");

            var profile = new GraphicsProfile
            {
                Name = "Prueba", Sim = "dcs",
                Settings = new() { ["graphics.shadows"] = "2", ["graphics.Scaling"] = "0.660", ["autoexec.DLSS_Preset"] = "'K'" },
            };
            var diff = m.Diff(profile);
            Check(diff.Count == 2 && diff.Any(c => c.Key == "graphics.shadows") && diff.Any(c => c.Key == "autoexec.DLSS_Preset" && c.Current is null),
                  "diferencias: 0.660 = 0.66 (mismo número); autoexec sin archivo = no está");

            running = true;
            threw = false;
            try { m.Apply(profile); } catch (InvalidOperationException) { threw = true; }
            Check(threw && File.ReadAllText(Path.Combine(dcs, "options.lua")) == Options, "DCS abierto: no se aplica y el archivo queda igual");
            running = false;
            Check(m.Apply(profile, dryRun: true).Count == 2 && File.ReadAllText(Path.Combine(dcs, "options.lua")) == Options, "simulación: no escribe");

            var applied = m.Apply(profile);
            Check(applied.Count == 2 && m.Diff(profile).Count == 0, "aplicar: después ya no hay diferencias");
            Check(File.ReadAllText(Path.Combine(dcs, "autoexec.cfg")) == "DLSS_Preset = 'K'\n", "autoexec.cfg creado si no existía");
            var backups = GraphicsManager.Backups(Path.Combine(dcs, "options.lua")).ToList();
            Check(backups.Count == 1 && File.ReadAllText(backups[0]) == Options, "copia .bak con el original");

            var captured = m.Capture("dcs", "Actual");
            Check(captured.Settings["graphics.shadows"] == "2" && captured.Settings["autoexec.DLSS_Preset"] == "'K'", "guardar lo actual como perfil");

            var file = new GraphicsFile { Profiles = [captured] };
            var json = Path.Combine(dir, "graficos.json");
            file.Save(json);
            var back = GraphicsFile.Load(json);
            Check(back.Find("actual")?.Settings["graphics.AA"] == "\"DLAA\"" && back.Headset.Name == "Pimax Crystal Light" && back.Headset.RefreshHz == 90,
                  "graficos.json: se guarda y se lee (con el visor por defecto)");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        // --- reglas de VR
        var headset = new HeadsetInfo();
        var bad = new Dictionary<string, string>
        {
            ["graphics.maxFPS"] = "60", ["VR.openxr_quadView"] = "false", ["VR.openxr_eyeGaze"] = "true",
            ["VR.pixel_density"] = "1.3", ["graphics.DOF"] = "1", ["graphics.sync"] = "true",
        };
        var advice = VrAdvisor.Check("dcs", bad, headset);
        Check(advice.Count == 6 && advice.Count(a => a.Level == AdviceLevel.Aviso) == 2, $"VR DCS: 6 consejos, 2 avisos ({advice.Count})");
        Check(advice.Any(a => a.Key == "graphics.maxFPS" && a.Text.Contains("90 Hz")), "VR: límite de FPS por debajo del refresco del visor");
        var good = new Dictionary<string, string> { ["graphics.maxFPS"] = "90", ["VR.openxr_quadView"] = "true", ["VR.pixel_density"] = "1", ["graphics.sync"] = "false" };
        Check(VrAdvisor.Check("dcs", good, headset).Count == 0, "VR DCS: configuración buena = nada que decir");
        Check(VrAdvisor.Check("dcs", new Dictionary<string, string> { ["VR.openxr_eyeGaze"] = "true" }, new HeadsetInfo { Name = "Crystal", EyeTracking = true }).Count == 0,
              "VR: eye gaze vale con un visor con seguimiento ocular");
        Check(VrAdvisor.Check("msfs", new Dictionary<string, string> { ["GraphicsVR.DOF.Enabled"] = "1", ["GraphicsVR.MotionBlur.Enabled"] = "0" }, headset).Count == 1,
              "VR MSFS: profundidad de campo activada");
        return fails;
    }
}
