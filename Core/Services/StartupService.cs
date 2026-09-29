using Microsoft.Win32;

namespace TimeTracker.Core.Services;

/// <summary>
/// Gère l'entrée de démarrage automatique dans
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.
/// </summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TimeTracker";

    /// <summary>
    /// Registre intouchable : posé par <c>--db=</c>. Une base de test a son propre réglage
    /// (faux sur une base neuve) et l'alignement du démarrage effaçait l'entrée de l'installation
    /// réelle — ou la repointait vers un exe de développement.
    /// </summary>
    public static bool Frozen { get; set; }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) != null;
    }

    public static void SetEnabled(bool enabled)
    {
        if (Frozen) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return;

        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
