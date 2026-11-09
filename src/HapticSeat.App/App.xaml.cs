using System.Windows;
using System.Windows.Threading;
using HapticSeat.App.ViewModels;
using HapticSeat.Effects;
using HapticSeat.Profiles;
using HapticSeat.Telemetry;
using HapticSeat.Telemetry.Dcs;
using HapticSeat.Telemetry.Il2;
using HapticSeat.Telemetry.Msfs;

namespace HapticSeat.App;

public partial class App : Application
{
    private Mutex? _single;
    private HapticHost? _host;
    private MainViewModel? _vm;
    private TrayIcon? _tray;
    private int _shutdownDone;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Una sola instancia: dos a la vez se pelearían por el puerto del cojín
        _single = new Mutex(true, @"Local\HapticSeat.App", out var first);
        if (!first)
        {
            MessageBox.Show("HapticSeat ya está abierto (mira en la bandeja del sistema).", "HapticSeat");
            _single = null;
            Shutdown();
            return;
        }

        // Pase lo que pase, los motores se apagan al salir
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownHost();

        var store = new ProfileStore();
        var settings = store.LoadSettings();
        var warnings = new List<string>();
        var profiles = store.LoadAll(warnings.Add);

        var source = new MultiSource(new MsfsSource(), new DcsSource(), new Il2Source(settings.Il2Port));
        _host = new HapticHost(source, EffectEngine.CreateDefault(), settings.MinLevelBytes());
        _host.Start();

        var dialogs = new Dialogs();
        _vm = new MainViewModel(_host, store, settings, profiles, dialogs);
        var window = new MainWindow { DataContext = _vm };
        dialogs.Owner = window;
        MainWindow = window;
        window.Closed += (_, _) => Shutdown();

        _tray = new TrayIcon(window, Shutdown);
        window.Icon = _tray.WindowIcon;

        if (settings.StartMinimized)
            _tray.ShowBalloon("HapticSeat sigue en marcha en la bandeja del sistema.");
        else
            window.Show();

        foreach (var w in warnings) dialogs.Info(w);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ShutdownHost();
        _tray?.Dispose();
        _single?.ReleaseMutex();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShutdownHost();
        MessageBox.Show($"Error inesperado; HapticSeat se cerrará.\n\n{e.Exception}", "HapticSeat", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown();
    }

    /// <summary>Guarda los cambios pendientes y apaga los motores (una sola vez).</summary>
    private void ShutdownHost()
    {
        if (Interlocked.Exchange(ref _shutdownDone, 1) == 1) return;
        try
        {
            _vm?.Dispose();
        }
        catch (InvalidOperationException)
        {
            // Llamado desde ProcessExit fuera del hilo de la interfaz: lo importante es apagar el cojín
        }
        finally
        {
            _host?.Dispose();
        }
    }
}
