namespace VibeRe.Mobile;

public partial class AppShell : Shell
{
    public AppShell(MainPage page)
    {
        InitializeComponent();
        MainContent.Content = page;
    }
}
