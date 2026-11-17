using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace PartCombiner;

public partial class SplitView : UserControl
{
    public SplitView() => InitializeComponent();

    private SplitViewModel Vm => (SplitViewModel)DataContext;

    private void OnChooseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose a file to split", Filter = "All files|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            Vm.SetSource(dialog.FileName);
    }

    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to split (packed as .7z or .zip)" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            Vm.SetSource(dialog.FolderName);
    }

    private void OnFewer(object sender, RoutedEventArgs e) => Vm.StepParts(-1);

    private void OnMore(object sender, RoutedEventArgs e) => Vm.StepParts(+1);

    private void OnPartsKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down)
        {
            Vm.StepParts(e.Key == Key.Up ? 1 : -1);
            PartsBox.CaretIndex = PartsBox.Text.Length;
            e.Handled = true;
        }
    }

    // ----- Password (PasswordBox can't be data-bound) -----

    private bool _syncingPassword;

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword) return;
        SetPassword(PasswordInput.Password);
    }

    private void OnPasswordShownChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingPassword) return;
        SetPassword(PasswordShown.Text);
    }

    private void SetPassword(string value)
    {
        Vm.Password = value;
        PasswordHint.Visibility = value.Length == 0 && RevealPassword.IsChecked != true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRevealChanged(object sender, RoutedEventArgs e)
    {
        bool show = RevealPassword.IsChecked == true;
        _syncingPassword = true;
        if (show) PasswordShown.Text = PasswordInput.Password;
        else PasswordInput.Password = PasswordShown.Text;
        _syncingPassword = false;

        PasswordShown.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PasswordInput.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        RevealPassword.Content = show ? "" : "";
        RevealPassword.ToolTip = show ? "Hide the password" : "Show the password";
        System.Windows.Automation.AutomationProperties.SetName(RevealPassword, show ? "Hide password" : "Show password");
        PasswordHint.Visibility = Visibility.Collapsed;
        if (!show) PasswordHint.Visibility = PasswordInput.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        (show ? (Control)PasswordShown : PasswordInput).Focus();
    }

    private void OnChangeOutput(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Save the parts to" };
        if (Vm.HasCustomOutput) dialog.InitialDirectory = Vm.OutputFolder;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            Vm.OutputFolder = dialog.FolderName;
    }

    private void OnResetOutput(object sender, RoutedEventArgs e) => Vm.OutputFolder = "";

    private async void OnSplit(object sender, RoutedEventArgs e) => await Vm.SplitAsync();

    private void OnCancel(object sender, RoutedEventArgs e) => Vm.Cancel();

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if (Vm.LastFirstPart is { } path) MainViewModel.Reveal(path);
    }
}
