using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace Kpos;

internal static class WindowsAppRegistration
{
    private const string InstalledAppsKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\kPOS";
    private const string ShortcutName = "kPOS.lnk";

    internal static string? TryRegister(RegisterFolders folders, string settings)
    {
        // Explicit data-directory overrides are used by portable and acceptance-test runs.
        if (!string.Equals(Path.GetFullPath(settings), RegisterInstallation.SettingsPath, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KPOS_DATA_DIRECTORY")))
            return null;

        string executable = Path.Combine(folders.Root, "kPOS.exe");
        if (!File.Exists(executable)) return null;

        try
        {
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs, Environment.SpecialFolderOption.Create);
            if (programs.Length == 0) throw new IOException("The Windows Start menu folder could not be found.");
            Register(executable, programs, Registry.CurrentUser, InstalledAppsKey);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or COMException)
        {
            return error.Message;
        }
    }

    internal static void Register(string executable, string programs, RegistryKey userRegistry, string registrationKey)
    {
        string directory = Path.GetDirectoryName(executable)!;
        Directory.CreateDirectory(programs);
        CreateShortcut(Path.Combine(programs, ShortcutName), executable, directory);

        string version = FileVersionInfo.GetVersionInfo(executable).ProductVersion?.Split('+')[0] ?? ReleaseVersion.RunningVersion;
        using var application = userRegistry.CreateSubKey(registrationKey);
        Set("InstallLocation", directory);
        Set("DisplayIcon", $"\"{executable}\",0");
        Set("DisplayVersion", version);
        Set("NoModify", 1);
        Set("NoRepair", 1);
        Set("NoRemove", 1);
        Set("DisplayName", "kPOS");

        void Set(string name, object value)
        {
            if (!Equals(application.GetValue(name), value)) application.SetValue(name, value);
        }
    }

    private static void CreateShortcut(string path, string target, string directory)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows shortcut support is unavailable.");
        object shell = Activator.CreateInstance(shellType) ?? throw new IOException("Windows shortcut support could not start.");
        object? link = null;
        try
        {
            link = ((dynamic)shell).CreateShortcut(path);
            dynamic shortcut = link;
            if (File.Exists(path) && string.Equals((string)shortcut.TargetPath, target, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string)shortcut.WorkingDirectory, directory, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty((string)shortcut.Arguments)) return;

            shortcut.TargetPath = target;
            shortcut.WorkingDirectory = directory;
            shortcut.Description = "Open the kPOS register";
            shortcut.Arguments = "";
            shortcut.Save();
            if (!File.Exists(path)) throw new IOException("The kPOS Start menu shortcut could not be saved.");
        }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
