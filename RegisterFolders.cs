using Microsoft.Data.Sqlite;

namespace Kpos;

internal sealed class RegisterFolders(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string Database => Path.Combine(Root, "Database", "register.db");
    public string DailyArchives => Path.Combine(Root, "Records", "End of Day");
    public string Backups => Path.Combine(Root, "Records", "Backups");

    public void Create()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Database)!);
        Directory.CreateDirectory(DailyArchives);
        Directory.CreateDirectory(Backups);
    }

    public static RegisterFolders? Choose(string settings, string? overridePath, out bool install)
    {
        install = false;
        if (!string.IsNullOrWhiteSpace(overridePath))
            return new RegisterFolders(overridePath);

        if (File.Exists(settings))
        {
            var saved = new RegisterFolders(File.ReadAllText(settings).Trim());
            if (File.Exists(saved.Database))
                return saved;

            MessageBox.Show($"The register database could not be found at:\n{saved.Database}\n\nChoose the folder that contains your existing kPOS installation.",
                "Locate kPOS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        using var picker = new FolderBrowserDialog
        {
            Description = "Choose a folder for kPOS and its records",
            UseDescriptionForTitle = true,
            InitialDirectory = Path.GetDirectoryName(settings)!
        };
        Directory.CreateDirectory(picker.InitialDirectory);
        if (picker.ShowDialog() != DialogResult.OK)
            return null;

        var folders = new RegisterFolders(picker.SelectedPath);
        if (File.Exists(settings) && !File.Exists(folders.Database))
            throw new FileNotFoundException("The chosen folder does not contain an existing kPOS database.", folders.Database);

        install = true;
        return folders;
    }

    public void Install(string settings)
    {
        string legacy = Path.Combine(Path.GetDirectoryName(settings)!, "register.db");
        if (!File.Exists(Database) && File.Exists(legacy))
            RegisterData.CopyDatabase(legacy, Database);
    }

    public void BackUpBeforeSchemaUpgrade()
    {
        if (!File.Exists(Database) || RegisterData.ReadSchemaVersion(Database) >= RegisterData.SchemaVersion)
            return;

        string path = Path.Combine(Backups, $"before-upgrade-{DateTime.Now:yyyy-MM-dd_HHmmssfff}.zip");
        RegisterData.ExportBackup(Database, path);
    }

    public string? InstallExecutable(string runningApp)
    {
        string installedApp = Path.Combine(Root, "kPOS.exe");
        if (string.Equals(runningApp, installedApp, StringComparison.OrdinalIgnoreCase))
            return null;

        bool replacing = File.Exists(installedApp);
        if (replacing && !ReleaseVersion.IsNewerExecutable(runningApp, installedApp))
            return null;

        string temporary = installedApp + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool created = false;
        try
        {
            File.Copy(runningApp, temporary);
            created = true;
            // ZIP viewers can mark the source read-only. Clear that flag on our copy only.
            MakeWritableIfPresent(temporary);
            if (!replacing)
            {
                File.Move(temporary, installedApp);
                return null;
            }

            string previous = installedApp + ".previous";
            MakeWritableIfPresent(installedApp);
            MakeWritableIfPresent(previous);
            File.Replace(temporary, installedApp, previous);
            return previous;
        }
        catch
        {
            if (created)
            {
                try
                {
                    MakeWritableIfPresent(temporary);
                    File.Delete(temporary);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Keep the original installation error if cleanup also fails.
                }
            }
            throw;
        }

        static void MakeWritableIfPresent(string path)
        {
            var file = new FileInfo(path);
            if (file.Exists && file.IsReadOnly)
                file.IsReadOnly = false;
        }
    }

    public void RestoreExecutable(string previous) =>
        File.Replace(previous, Path.Combine(Root, "kPOS.exe"), null);

    public void Remember(string settings)
    {
        string temporary = settings + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, Root);
            File.Move(temporary, settings, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
