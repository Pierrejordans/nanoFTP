using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NanoFTP.Models;
using NanoFTP.ViewModels;

namespace NanoFTP.Views;

public partial class ConnectionEditorWindow : Window
{
    private readonly ConnectionEditorViewModel _viewModel;
    private bool _deleteArmed;
    private bool _passwordVisible;

    public ConnectionEditorWindow()
        : this(null)
    {
    }

    public ConnectionEditorWindow(ConnectionProfile? profile)
    {
        InitializeComponent();

        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://NanoFTP/Assets/nanoftp.png")));
        _viewModel = new ConnectionEditorViewModel(profile);
        DataContext = _viewModel;

        Opened += (_, _) =>
        {
            FitToWorkingArea();

            Dispatcher.UIThread.Post(() =>
            {
                NameBox.Focus();

                if (!_viewModel.IsNew && !string.IsNullOrWhiteSpace(_viewModel.Host))
                {
                    NameBox.SelectAll();
                }
            }, DispatcherPriority.Input);
        };

        Closing += (_, _) => _viewModel.CancelTest();
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.Validate())
        {
            FocusFirstError();
            return;
        }

        var profile = _viewModel.BuildProfile();
        Close(new ConnectionEditorResult(profile, false, _viewModel.IsNew));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private async void Test_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.TestAsync();
        }
        catch (Exception)
        {
            // Le view model transforme déjà les erreurs réseau en message lisible.
        }
    }

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanDelete)
        {
            return;
        }

        if (!_deleteArmed)
        {
            _deleteArmed = true;
            DeleteButton.Content = "Confirmer";
            ToolTip.SetTip(DeleteButton, "Cliquez à nouveau pour supprimer définitivement cette connexion.");
            return;
        }

        Close(new ConnectionEditorResult(_viewModel.BuildProfile(), true, false));
    }

    private void RevealPassword_Click(object? sender, RoutedEventArgs e)
    {
        _passwordVisible = !_passwordVisible;
        PasswordBox.PasswordChar = _passwordVisible ? '\0' : '●';
        ToolTip.SetTip(RevealButton, _passwordVisible ? "Masquer le mot de passe" : "Afficher le mot de passe");
        AutomationProperties.SetName(RevealButton, _passwordVisible ? "Masquer le mot de passe" : "Afficher le mot de passe");
        PasswordBox.Focus();
        PasswordBox.CaretIndex = PasswordBox.Text?.Length ?? 0;
    }

    private void FitToWorkingArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var scale = Math.Max(screen.Scaling, RenderScaling);
        if (scale <= 0)
        {
            scale = 1;
        }

        var maxHeight = screen.WorkingArea.Height / scale - 40;
        if (Height > maxHeight)
        {
            Height = Math.Max(MinHeight, maxHeight);
        }
    }

    private void FocusFirstError()
    {
        if (_viewModel.HasNameError)
        {
            NameBox.Focus();
        }
        else if (_viewModel.HasHostError)
        {
            HostBox.Focus();
        }
        else if (_viewModel.HasPortError)
        {
            PortBox.Focus();
        }
    }
}
