using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using RigControl.ViewModels;

namespace RigControl;

public partial class MainWindow : Window
{
    /// <summary>Cabecera de cada pantalla (en el orden del menú): ilustración de Assets/Banners, rótulo, título y subtítulo.</summary>
    private static readonly (string Image, string Caption, string Title, string Subtitle)[] Banners =
    [
        ("monitor", "◢ 01 · ENTRADAS", "MONITOR", "Todos los mandos en directo: ejes, botones y hats."),
        ("ejecucion", "◢ 02 · MISIÓN", "EJECUCIÓN", "Simulador, avión y perfil activo: los mandos en vuelo."),
        ("perfiles", "◢ 03 · CONFIGURACIÓN", "PERFILES", "Bindings por avión, luces, pantalla del ICP y generación con IA."),
        ("hangar", "◢ 04 · ARRANQUE", "HANGAR", "Secuencias de arranque: programas, prioridad, ventanas, mods y gráficos."),
        ("mods", "◢ 05 · EQUIPAMIENTO", "MODS", "Bibliotecas y conjuntos de mods con uniones de directorio."),
        ("instantaneas", "◢ 06 · RESPALDO", "INSTANTÁNEAS", "Copias de la configuración de DCS, MSFS y RigControl."),
        ("graficos", "◢ 07 · RENDIMIENTO", "GRÁFICOS", "Perfiles gráficos, medición de FPS y optimizador para VR."),
    ];

    private readonly MonitorViewModel _monitor = new();
    private readonly RunViewModel _run = new();
    private readonly ProfilesViewModel _profiles = new();
    private readonly HangarViewModel _hangar = new();
    private readonly ModsViewModel _mods = new();
    private readonly SnapshotsViewModel _snapshots = new();
    private readonly GraphicsViewModel _graphics = new();

    public MainWindow()
    {
        InitializeComponent();
        MonitorPage.DataContext = _monitor;
        RunPage.DataContext = _run;
        ProfilesPage.DataContext = _profiles;
        HangarPage.DataContext = _hangar;
        ModsPage.DataContext = _mods;
        SnapshotsPage.DataContext = _snapshots;
        GraphicsPage.DataContext = _graphics;
        _hangar.StartRunRequested += _run.StartAuto;
        ShowBanner(0);

        // DirectInput necesita la ventana ya creada para el nivel cooperativo (compartido, en segundo plano).
        SourceInitialized += (_, _) =>
        {
            WindowPlacement.Restore(this);   // posición y tamaño de la última vez
            var hwnd = new WindowInteropHelper(this).Handle;
            _monitor.Start(hwnd);
            _run.Initialize(hwnd);
            _profiles.Initialize(hwnd);
        };

        Closing += (_, _) => WindowPlacement.Save(this);

        // Al cerrar: parar la ejecución (suelta teclas y dispositivos) y cerrar el monitor.
        Closed += (_, _) =>
        {
            _hangar.Dispose();
            _graphics.Dispose();
            _run.Dispose();
            _profiles.Dispose();
            _monitor.Dispose();
        };
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MonitorPage is null || RunPage is null || ProfilesPage is null || HangarPage is null || ModsPage is null || SnapshotsPage is null || GraphicsPage is null) return; // durante InitializeComponent
        var page = Nav.SelectedIndex;
        MonitorPage.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        RunPage.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        HangarPage.Visibility = page == 3 ? Visibility.Visible : Visibility.Collapsed;
        ModsPage.Visibility = page == 4 ? Visibility.Visible : Visibility.Collapsed;
        SnapshotsPage.Visibility = page == 5 ? Visibility.Visible : Visibility.Collapsed;
        GraphicsPage.Visibility = page == 6 ? Visibility.Visible : Visibility.Collapsed;
        _graphics.SetVisible(page == 6);
        _snapshots.SetVisible(page == 5);
        _monitor.SetVisible(page == 0);
        _profiles.SetVisible(page == 2);
        _hangar.SetVisible(page == 3);
        _mods.SetVisible(page == 4);
        ShowBanner(page);
    }

    private void ShowBanner(int page)
    {
        if (BannerImage is null || page < 0 || page >= Banners.Length) return;
        var (image, caption, title, subtitle) = Banners[page];
        BannerImage.Source = new BitmapImage(new Uri($"pack://application:,,,/RigControl;component/Assets/Banners/{image}.jpg"));
        BannerCaption.Text = caption;
        BannerTitle.Text = title;
        BannerSubtitle.Text = subtitle;
    }
}
