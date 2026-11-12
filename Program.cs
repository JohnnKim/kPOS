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
        try
        {
            folders = RegisterFolders.Choose(settings, Environment.GetEnvironmentVariable("KPOS_DATA_DIRECTORY"), out bool install);
            if (folders is null)
                return;

            folders.Create();
            if (install)
                folders.Install(settings);

            data = new RegisterData(folders.Database);
            if (install)
                folders.Remember(settings);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        {
            data?.Dispose();
            MessageBox.Show(
                $"The register could not open its data folder.\n\n{folders?.Root}\n{error.Message}",
                "kPOS could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using (data)
            Application.Run(new RegisterForm(data, folders));
    }
}
