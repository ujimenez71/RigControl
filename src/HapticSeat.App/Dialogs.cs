using System.Windows;
using System.Windows.Controls;
using HapticSeat.App.ViewModels;

namespace HapticSeat.App;

/// <summary>Diálogos sencillos. WPF no trae un cuadro para pedir texto: se monta en código.</summary>
public sealed class Dialogs : IDialogs
{
    public Window? Owner { get; set; }

    public string? AskText(string title, string prompt, string initial)
    {
        var box = new TextBox { Text = initial, MinWidth = 320, Margin = new Thickness(0, 6, 0, 16) };
        var ok = new Button { Content = "Aceptar", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = prompt });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = Owner?.IsVisible == true ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = Owner?.IsVisible == true ? Owner : null,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        return dialog.ShowDialog() == true ? box.Text : null;
    }

    public bool Confirm(string text) =>
        MessageBox.Show(text, "HapticSeat", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void Info(string text) => MessageBox.Show(text, "HapticSeat");
}
