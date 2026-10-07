using VibeRe.Client;
using VibeRe.Client.Configuration;

namespace VibeRe.Mobile;

public partial class MainPage : ContentPage
{
    private readonly ConnectionSettingsViewModel _model;

    public MainPage()
    {
        InitializeComponent();
        var connections = new ServerConnectionManager(
            new ClientConnectionDatabase(Path.Combine(FileSystem.AppDataDirectory, "client.db")),
            new MobileServerTokenStore());
        _model = new(connections, async uri => { await Launcher.Default.OpenAsync(uri); });
        BindingContext = _model;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _model.InitializeAsync();
        _model.StartMonitoring();
    }

    protected override void OnDisappearing()
    {
        _model.StopMonitoring();
        base.OnDisappearing();
    }
}
