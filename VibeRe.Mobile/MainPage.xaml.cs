using VibeRe.Client.Application;

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
    }

    protected override void OnDisappearing()
    {
        _model.CancelPending();
        base.OnDisappearing();
    }
}
