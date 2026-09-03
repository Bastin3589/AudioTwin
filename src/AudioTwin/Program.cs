using System.Text;

namespace AudioTwin;

internal static class Program
{
    private const string MutexName = @"Local\AudioTwin.SingleInstance";
    private const string ShowWindowEventName = @"Local\AudioTwin.ShowWindow";

    /// <summary>Command-line switches that mean "start in the tray, no window".</summary>
    private static readonly string[] HiddenStartSwitches = { "tray", "hidden", "minimized", "background", "silent" };

    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _showWindowSignal;
    private static RegisteredWaitHandle? _showWindowRegistration;

    [STAThread]
    private static void Main(string[] args)
    {
        // Only one copy may run: two instances would open the same devices twice.
        _instanceMutex = new Mutex(true, MutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            SignalRunningInstance();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) => ReportFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception);

        // The Run entry may point at an old location after the .exe is moved.
        StartupManager.RefreshIfEnabled();

        var form = new MainForm(WantsHiddenStart(args));

        try
        {
            _showWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
            _showWindowRegistration = ThreadPool.RegisterWaitForSingleObject(
                _showWindowSignal, (_, _) => RequestShow(form), null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch
        {
            // Without the signal a second launch just exits quietly - not fatal.
        }

        try
        {
            Application.Run(form);
        }
        finally
        {
            _showWindowRegistration?.Unregister(null);
            _showWindowSignal?.Dispose();
            GC.KeepAlive(_instanceMutex);
        }
    }

    /// <summary>
    /// A tray application has no taskbar button to click, so launching it a
    /// second time is how people ask for the window back. Poke the live instance
    /// and leave without a dialog.
    /// </summary>
    private static void SignalRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var running))
            {
                using (running) running.Set();
                return;
            }
        }
        catch
        {
            // Fall through to the message box.
        }

        MessageBox.Show(
            "AudioTwin is already running.\r\n\r\n" +
            "Look for its icon in the notification area, next to the clock.",
            "AudioTwin",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static void RequestShow(MainForm form)
    {
        try
        {
            if (form.IsDisposed || !form.IsHandleCreated) return;
            form.BeginInvoke(new Action(form.ShowFromTray));
        }
        catch
        {
            // The form is closing; nothing to bring up.
        }
    }

    private static bool WantsHiddenStart(IEnumerable<string> args) => args.Any(arg =>
        HiddenStartSwitches.Contains(arg.TrimStart('-', '/'), StringComparer.OrdinalIgnoreCase));

    private static void ReportFatal(Exception? ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AudioTwin hit an unexpected error.");
        sb.AppendLine();
        sb.AppendLine(ex?.Message ?? "Unknown error.");
        MessageBox.Show(sb.ToString(), "AudioTwin", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
