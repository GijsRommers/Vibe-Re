using VibeRe.Client;

namespace VibeRe.Mobile;

public partial class MainPage : ContentPage
{
    private readonly ConnectionSettingsViewModel _model;

    public MainPage(ConnectionSettingsViewModel model)
    {
        InitializeComponent();
        _model = model;
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
