using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NetUse;

public partial class App : Application
{
    // Global\ so a second copy in another session can't start (it would also stop our ETW session).
    // Any program can pre-create a Global\ mutex, so an existing one only counts when Administrators own
    // it (see InstanceLock); a squatted name can't keep NetUse from starting.
    public const string MutexName = @"Global\NetUseSingleInstanceMutex";
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool created;
        try { _mutex = new Mutex(true, MutexName, out created); }
        catch (UnauthorizedAccessException) { _mutex = null; created = false; } // squatter denied us access
        if (!created)
        {
            bool real = (_mutex != null && InstanceLock.OwnedByAdmins(_mutex)) || InstanceLock.OtherElevatedInstance();
            if (real)
            {
                MessageBox.Show("NetUse is already running. Look for it in the system tray.", "NetUse");
                Shutdown();
                return;
            }
            _mutex?.Dispose();
            _mutex = null;
            Log(new InvalidOperationException("The NetUse start-up lock was created by a non-administrator program. It was ignored."));
        }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, args) => { Log(args.Exception); args.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, args) => { Log(args.Exception); args.SetObserved(); };

        try
        {
            GeoIp.Preload();            // country table loads in the background while the window builds
            var w = new MainWindow();   // MainWindow loads the saved theme (Light / Dark / Dark Blue) itself
            MainWindow = w;
            if (!e.Args.Contains("--minimized")) w.Show();
        }
        catch (Exception ex)
        {
            // Without this the process would stay alive with no window (explicit shutdown mode).
            Log(ex);
            MessageBox.Show("NetUse could not start:\n\n" + ex.Message, "NetUse", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Appends to error.log, but only inside the secured data folder, and caps its size.</summary>
    public static void Log(Exception ex)
    {
        try
        {
            if (!SecureStore.Ready) return;
            string file = Path.Combine(Database.Dir, "error.log");
            var fi = new FileInfo(file);
            if (fi.Exists && fi.Length > 1024 * 1024) File.Move(file, file + ".old", overwrite: true);
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}: {ex}\n\n");
        }
        catch { }
    }
}
