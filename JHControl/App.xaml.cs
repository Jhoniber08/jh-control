namespace JHControl;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UserAppTheme = AppTheme.Dark;
    }

    protected override Window CreateWindow(IActivationState activationState)
    {
        var main = new MainPage();
        var nav = new NavigationPage(main)
        {
            BarBackgroundColor = Color.FromArgb("#0B0D12"),
            BarTextColor = Colors.White
        };
        var window = new Window(nav) { Title = "JH Control" };
        // Al volver a la app, reconectar si hace falta
        window.Resumed += (s, e) => main.AppResumed();
        return window;
    }
}
