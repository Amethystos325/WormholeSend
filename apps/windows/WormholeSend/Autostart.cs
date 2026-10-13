using Microsoft.Win32;

namespace WormholeSend;

/// Start with Windows: the Run key of the current user (an MSIX package
/// would use a StartupTask instead).
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Wormhole Send";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                key.SetValue(Name, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}
