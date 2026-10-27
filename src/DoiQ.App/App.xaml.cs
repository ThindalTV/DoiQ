using DoiQ.App.Services;

namespace DoiQ.App;

public partial class App : Application
{
    private readonly AppLifecycle _lifecycle;

    public App(AppLifecycle lifecycle)
    {
        InitializeComponent();
        _lifecycle = lifecycle;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage()) { Title = "DoiQ" };
        _lifecycle.Attach(window);
        return window;
    }
}
