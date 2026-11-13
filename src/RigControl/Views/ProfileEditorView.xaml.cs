using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RigControl.ViewModels;

namespace RigControl.Views;

public partial class ProfileEditorView : UserControl
{
    public ProfileEditorView() => InitializeComponent();

    private ProfileEditorViewModel Vm => (ProfileEditorViewModel)DataContext;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Vm.Validate();
        if (!Vm.Ok && MessageBox.Show(Window.GetWindow(this)!,
                "El perfil tiene errores. Se guardará, pero la ejecución seguirá usando la versión anterior hasta que los corrijas.\n\n¿Guardar de todos modos?",
                "RigControl", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        Vm.Save();
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Dirty && MessageBox.Show(Window.GetWindow(this)!, "¿Descartar los cambios sin guardar?", "RigControl",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        Vm.Discard();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        Vm.AddBinding();
        BindingsGrid.ScrollIntoView(Vm.Selected!);
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e) => Vm.DuplicateSelected();

    private void Delete_Click(object sender, RoutedEventArgs e) => Vm.DeleteSelected();

    private void Capture_Click(object sender, RoutedEventArgs e) => Vm.ToggleCapture();

    private void UseAircraft_Click(object sender, RoutedEventArgs e) => Vm.UseCurrentAircraft();

    // ---- condiciones (de bindings y de capas): el DataContext del botón dice sobre qué actúa

    private static T? Ctx<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

    private void AddTerm_Click(object sender, RoutedEventArgs e) => Ctx<ConditionEditViewModel>(sender)?.AddTerm();

    private void RemoveTerm_Click(object sender, RoutedEventArgs e)
    {
        if (Ctx<ConditionTermViewModel>(sender) is { } t) t.Owner.RemoveTerm(t);
    }

    private void CaptureTerm_Click(object sender, RoutedEventArgs e)
    {
        if (Ctx<ConditionTermViewModel>(sender) is { } t) Vm.CaptureInto(t);
    }

    private void ClearComplex_Click(object sender, RoutedEventArgs e) => Ctx<ConditionEditViewModel>(sender)?.ClearComplex();

    // ---- capas

    private void NewLayer_Click(object sender, RoutedEventArgs e) => Vm.NewLayer();

    private void DeleteLayer_Click(object sender, RoutedEventArgs e)
    {
        if (Ctx<LayerEditViewModel>(sender) is not { } layer) return;
        if (Vm.LayerInUse(layer) && MessageBox.Show(Window.GetWindow(this)!,
                $"La capa \"{layer.Name}\" se usa en alguna condición; si la borras, esas condiciones darán error.\n\n¿Borrarla?",
                "RigControl", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        Vm.DeleteLayer(layer);
    }

    // ---- luces

    private void NewLight_Click(object sender, RoutedEventArgs e) => Vm.NewLight();

    private void DeleteLight_Click(object sender, RoutedEventArgs e)
    {
        if (Ctx<LightEditViewModel>(sender) is { } light) Vm.DeleteLight(light);
    }

    private void AddLightTerm_Click(object sender, RoutedEventArgs e) => Ctx<LightEditViewModel>(sender)?.AddTerm();

    private void RemoveLightTerm_Click(object sender, RoutedEventArgs e)
    {
        if (Ctx<LightTermViewModel>(sender) is { } t) t.Owner.RemoveTerm(t);
    }

    // ---- pantalla del ICP

    private TextBox? _lastIcpBox;

    private void IcpLine_GotFocus(object sender, RoutedEventArgs e) => _lastIcpBox = sender as TextBox;

    /// <summary>Inserta el valor elegido donde estaba el cursor en la última línea editada (o al final de la primera libre).</summary>
    private void IcpInsert_Click(object sender, RoutedEventArgs e)
    {
        var text = IcpFieldBox.SelectedItem is IcpFieldOption o ? o.Text
            : IcpFieldBox.Text.Split(" · ")[0].Trim();
        if (text.Length == 0) return;
        if (_lastIcpBox is { } box)
        {
            var at = box.SelectionStart;
            box.SelectedText = text;
            box.Focus();
            box.CaretIndex = at + text.Length;
            return;
        }
        var line = Vm.IcpLines.FirstOrDefault(l => l.Text.Trim().Length == 0) ?? Vm.IcpLines[^1];
        line.Text += text;
    }

    private void Suggestion_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: string name }) Vm.PickSuggestion(name);
    }

    /// <summary>Captura una tecla o combinación en el formato de "key:" (CTRL+SHIFT+G, F5, NUM+...).</summary>
    private void KeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin)
            return; // esperar a la tecla principal

        var name = KeyName(key);
        if (name is null || Vm.Selected is null) return;
        var mods = Keyboard.Modifiers;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("CTRL");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("SHIFT");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("ALT");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("WIN");
        parts.Add(name);
        Vm.Selected.Name = string.Join("+", parts);
    }

    private static string? KeyName(Key k) => k switch
    {
        >= Key.A and <= Key.Z => k.ToString(),
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        >= Key.F1 and <= Key.F24 => k.ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => $"NUM{(int)(k - Key.NumPad0)}",
        Key.Space => "SPACE", Key.Enter => "ENTER", Key.Tab => "TAB", Key.Escape => "ESC", Key.Back => "BACKSPACE",
        Key.Up => "UP", Key.Down => "DOWN", Key.Left => "LEFT", Key.Right => "RIGHT",
        Key.Home => "HOME", Key.End => "END", Key.PageUp => "PGUP", Key.PageDown => "PGDN",
        Key.Insert => "INS", Key.Delete => "DEL", Key.Pause => "PAUSE", Key.CapsLock => "CAPSLOCK", Key.Scroll => "SCROLLLOCK",
        Key.Multiply => "NUM*", Key.Add => "NUM+", Key.Subtract => "NUM-", Key.Decimal => "NUM.", Key.Divide => "NUM/",
        _ => null,
    };
}
