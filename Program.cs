using Microsoft.Data.Sqlite;

namespace Kpos;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length == 4 && int.TryParse(args[1], out int processId) && processId > 0 &&
            long.TryParse(args[2], out long started) && started > 0)
        {
            if (args[0] == "--install-update")
            {
                UpdateInstaller.InstallAfterExit(new RegisterFolders(args[3]), processId, started);
                return;
            }
            if (args[0] == "--after-update")
                UpdateInstaller.CleanAfterExit(args[3], processId, started);
        }

        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kPOS", "installation-folder.txt");
        RegisterData? data = null;
        RegisterFolders? folders = null;
        string? previousExecutable = null;
        try
        {
            string? overridePath = Environment.GetEnvironmentVariable("KPOS_DATA_DIRECTORY");
            folders = RegisterFolders.Choose(settings, overridePath, out bool install);
            if (folders is null)
                return;

            folders.Create();
            if (install)
                folders.Install(settings);

            folders.BackUpBeforeSchemaUpgrade();
#if KPOS_SINGLE_FILE
            if (Environment.ProcessPath is string executable)
            {
                try
                {
                    previousExecutable = folders.InstallExecutable(executable);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    int windowsCode = error.HResult & 0xffff;
                    string help = windowsCode is 32 or 33
                        ? "Close any other kPOS windows and try again."
                        : "Extract the ZIP to a normal folder and make sure you can write to the installation folder.";
                    MessageBox.Show(
                        $"The kPOS application file could not be installed or updated. {help}\n\n{folders.Root}\n{error.Message}",
                        "kPOS installation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }
#endif

            data = new RegisterData(folders.Database);
            if (install)
                folders.Remember(settings);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException)
        {
            string details = error.Message;
            if (data is null && previousExecutable is not null)
            {
                try
                {
                    folders!.RestoreExecutable(previousExecutable);
                }
                catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                {
                    details += $"\nThe previous executable is at {previousExecutable}. {restoreError.Message}";
                }
            }
            data?.Dispose();
            MessageBox.Show(
                $"The register could not open its data folder.\n\n{folders?.Root}\n{details}",
                "kPOS could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using (data)
            Application.Run(new RegisterForm(data, folders));
    }
}
