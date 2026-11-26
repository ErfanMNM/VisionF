using Avalonia.Controls;
using Avalonia.Input;

namespace TanTienQR.Views;

public partial class ManualActivationView : UserControl
{
    public ManualActivationView() => InitializeComponent();

    // Keyboard input is committed only by the explicit Add button. A scanner
    // commits through the service's complete-frame event, never this key handler.
    private void OnCodeInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) e.Handled = true;
    }
}
