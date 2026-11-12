using Microsoft.Data.Sqlite;

namespace Kpos;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        string directory = Environment.GetEnvironmentVariable("KPOS_DATA_DIRECTORY") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kPOS");
        RegisterData data;
        try
        {
            Directory.CreateDirectory(directory);
            data = new RegisterData(Path.Combine(directory, "register.db"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        {
            MessageBox.Show(
                $"The register could not open its product database.\n\nFolder: {directory}\n{error.Message}",
                "kPOS could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using (data)
            Application.Run(new RegisterForm(data));
    }
}
