namespace SpinFourKay.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName =
        @"Local\SpinFOURKAYYY_91B84F58-9897-4A22-ABAE-B012A6EE89B3";

    private static Mutex? _singleInstanceMutex;
    private static bool _ownsSingleInstanceMutex;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            SingleInstanceMutexName,
            out bool createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            _ = System.Windows.MessageBox.Show(
                "SpinFOURKAYYY is already open. Use the existing window so two "
                    + "sessions cannot prepare the same Legends client at once.",
                "SpinFOURKAYYY is already running",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
            _ownsSingleInstanceMutex = false;
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }
}
