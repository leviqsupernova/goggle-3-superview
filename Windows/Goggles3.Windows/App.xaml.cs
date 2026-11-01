using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace Goggles3.Windows;
public partial class App : Application
{
    // Per Windows session. Second launch (e.g. superflash again): show the running window, then exit.
    private const string Name = @"Local\Goggles3SuperView";
    private Mutex? single;

    public App()
    {
        // An explicit fallback for virtual display drivers that cannot initialize WPF.
        if (Environment.GetCommandLineArgs().Contains("--software-render"))
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool connect = e.Args.Contains("--connect");
        // Events first, mutex second: whoever finds the mutex taken can always signal, and the signal
        // stays set until the running instance's listener (started below) takes it.
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, Name + ".show");
        var connectAgain = new EventWaitHandle(false, EventResetMode.AutoReset, Name + ".connect");
        single = new Mutex(true, Name, out bool first);
        if (!first)
        {
            AllowSetForegroundWindow(-1); // ASFW_ANY: the running instance may take the foreground
            (connect ? connectAgain : show).Set();
            Shutdown();
            return;
        }
        // Mint accent (Connect, Got it) on the dark theme from App.xaml.
        ApplicationAccentColorManager.Apply(Color.FromRgb(0x30, 0xDB, 0xCF), ApplicationTheme.Dark, false, false);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        new Thread(() =>
        {
            while (true)
            {
                int signal = WaitHandle.WaitAny([show, connectAgain]);
                Dispatcher.BeginInvoke(() => window.BringToFront(connect: signal == 1));
            }
        }) { IsBackground = true, Name = "Single instance" }.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        single?.Dispose();
        base.OnExit(e);
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
