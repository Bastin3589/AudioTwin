using Microsoft.Win32;

namespace AudioTwin;

/// <summary>
/// "Start with Windows" via the per-user Run key. No admin rights, no service,
/// no scheduled task.
///
/// The entry passes --tray so a machine start-up brings the app up in the
/// notification area rather than throwing a window at the user's face.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AudioTwin";
    private const string HiddenStartSwitch = "--tray";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns null on success, or an error message.</summary>
    public static string? SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null) return "The Windows startup registry key could not be opened.";

            if (enabled)
            {
                var command = BuildCommand();
                if (command == null) return "The application path could not be determined.";
                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else
            {
                if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return null;
        }
        catch (Exception ex)
        {
            return "Could not change the Windows startup setting: " + ex.Message;
        }
    }

    /// <summary>
    /// Rewrites the Run entry if it is stale - the .exe was moved, or it was
    /// written by an older build that did not pass --tray. Silent by design.
    /// </summary>
    public static void RefreshIfEnabled()
    {
        try
        {
            var command = BuildCommand();
            if (command == null) return;

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            var current = key?.GetValue(ValueName) as string;

            if (key == null || current == null) return;
            if (string.Equals(current, command, StringComparison.OrdinalIgnoreCase)) return;

            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        catch
        {
            // A locked or read-only registry is not worth interrupting start-up for.
        }
    }

    private static string? BuildCommand()
    {
        var path = Environment.ProcessPath;
        return string.IsNullOrEmpty(path) ? null : "\"" + path + "\" " + HiddenStartSwitch;
    }
}
