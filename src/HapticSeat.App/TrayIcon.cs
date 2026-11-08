using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace HapticSeat.App;

/// <summary>
/// Icono de la bandeja: doble clic abre la ventana, menú con Abrir/Salir. Al minimizar, la ventana se
/// esconde en la bandeja (la app sigue generando efectos). El icono se dibuja en código: el cojín con
/// sus 8 motores.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _notify;
    private readonly Icon _icon;
    private readonly Window _window;

    public ImageSource WindowIcon { get; }

    public TrayIcon(Window window, Action exit)
    {
        _window = window;
        _icon = DrawIcon();
        WindowIcon = Imaging.CreateBitmapSourceFromHIcon(_icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir HapticSeat", null, (_, _) => ShowWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Salir (apaga el cojín)", null, (_, _) => exit());

        _notify = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "HapticSeat",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notify.DoubleClick += (_, _) => ShowWindow();

        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Minimized) window.Hide();
        };
    }

    public void ShowBalloon(string text) => _notify.ShowBalloonTip(3000, "HapticSeat", text, Forms.ToolTipIcon.Info);

    private void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private static Icon DrawIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var pad = new SolidBrush(System.Drawing.Color.FromArgb(0x2E, 0x32, 0x3A));
            using var motor = new SolidBrush(System.Drawing.Color.FromArgb(0xF0, 0x88, 0x3E));
            g.FillRectangle(pad, 6, 1, 20, 30);
            for (var row = 0; row < 4; row++)
                for (var col = 0; col < 2; col++)
                    g.FillEllipse(motor, 9 + col * 9, 3 + row * 7, 6, 6);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
        _icon.Dispose();
    }
}
