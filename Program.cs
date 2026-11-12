using Microsoft.Data.Sqlite;

namespace Kpos;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

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

            if (string.IsNullOrWhiteSpace(overridePath))
            {
                folders.BackUpBeforeSchemaUpgrade();
#if KPOS_SINGLE_FILE
                if (Environment.ProcessPath is string executable)
                    previousExecutable = folders.InstallExecutable(executable);
#endif
            }

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
