using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

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
        single = new Mutex(true, Name, out bool first);
        if (!first)
        {
            AllowSetForegroundWindow(-1); // ASFW_ANY: the running instance may take the foreground
            if (EventWaitHandle.TryOpenExisting(Name + (connect ? ".connect" : ".show"), out EventWaitHandle? wake))
                using (wake) wake.Set();
            Shutdown();
            return;
        }
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, Name + ".show");
        var connectAgain = new EventWaitHandle(false, EventResetMode.AutoReset, Name + ".connect");
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
