using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal static class RegisterInstallation
{
    internal static bool StartInstalledIfDifferent(RegisterFolders folders, string settings)
    {
        string? running = Program.InstallationExecutable;
        string installed = Path.Combine(folders.Root, "kPOS.exe");
        if (running is null || string.Equals(running, installed, StringComparison.OrdinalIgnoreCase) || !File.Exists(installed))
            return false;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var start = new System.Diagnostics.ProcessStartInfo(installed)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folders.Root
        };
        foreach (string value in new[] { "--open-installed", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), folders.Root, settings })
            start.ArgumentList.Add(value);
        using var child = System.Diagnostics.Process.Start(start) ?? throw new IOException("The installed kPOS application could not start.");
        return true;
    }

    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kPOS", "installation-folder.txt");
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "kPOS");

    public static string? Install(RegisterFolders folders, string settings, string? executable)
    {
        folders.Create();
        using var session = RegisterSession.Open(folders);
        folders.Install(settings);
        folders.BackUpBeforeSchemaUpgrade();
        string? previous = null;
        try
        {
            if (executable is not null) previous = folders.InstallExecutable(executable);
            using var data = new RegisterData(folders.Database);
            folders.Remember(settings);
        }
        catch
        {
            if (previous is not null) folders.RestoreExecutable(previous);
            throw;
        }
        return executable is null ? null : WindowsAppRegistration.TryRegister(folders, settings);
    }

    public static void ValidateDestination(RegisterFolders source, string destination)
    {
        string from = Path.TrimEndingDirectorySeparator(source.Root);
        string to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase) ||
            to.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            from.StartsWith(to + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a separate folder outside the current installation.");
        if (File.Exists(to) || Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any())
            throw new IOException("Choose a new or empty folder. Existing files will not be overwritten.");
        RejectLink(source.Root);
        for (DirectoryInfo? parent = new(to); parent is not null; parent = parent.Parent)
            if (parent.Exists) RejectLink(parent.FullName);
    }

    public static RegisterFolders CopyTo(RegisterFolders source, string destination, string settings, IProgress<string>? progress = null)
    {
        ValidateDestination(source, destination);
        var target = new RegisterFolders(Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)));
        string parent = Path.GetDirectoryName(target.Root) ?? throw new IOException("Choose a folder inside a drive.");
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, ".kpos-move-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            // A read-only Windows share permits SQLite's snapshot reader, but excludes
            // another register/writer. The caller closes its own database before copying.
            using var guard = new FileStream(source.Database, FileMode.Open, FileAccess.Read, FileShare.Read);
            foreach (string entry in Enumerate(source.Root))
            {
                string relative = Path.GetRelativePath(source.Root, entry);
                if (relative.Equals(RegisterSession.LockName, StringComparison.OrdinalIgnoreCase)) continue;
                string copy = Path.Combine(stage, relative);
                if (Directory.Exists(entry)) { Directory.CreateDirectory(copy); continue; }
                if (entry.Equals(source.Database, StringComparison.OrdinalIgnoreCase) ||
                    new[] { "-journal", "-wal", "-shm" }.Any(suffix => entry.Equals(source.Database + suffix, StringComparison.OrdinalIgnoreCase)))
                    continue;
                progress?.Report("Copying " + relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(entry, copy);
                using var original = File.OpenRead(entry);
                using var copied = File.OpenRead(copy);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(original), SHA256.HashData(copied)))
                    throw new IOException("Copy verification failed for " + relative);
            }
            var staged = new RegisterFolders(stage);
            Directory.CreateDirectory(Path.GetDirectoryName(staged.Database)!);
            progress?.Report("Copying and checking the register database…");
            RegisterData.CopyDatabase(source.Database, staged.Database);
            using (var check = new RegisterData(staged.Database))
            {
                using var database = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = staged.Database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                database.Open();
                database.CreateCollation("ITEM_CODE", StringComparer.OrdinalIgnoreCase.Compare);
                using var verify = database.CreateCommand();
                verify.CommandText = "PRAGMA quick_check";
                if (verify.ExecuteScalar() as string != "ok") throw new InvalidDataException("The copied database did not pass verification.");
            }
            ValidateDestination(source, target.Root);
            if (Directory.Exists(target.Root)) Directory.Delete(target.Root); // Empty directory only.
            Directory.Move(stage, target.Root);
            target.Remember(settings); // Commit the path only after the entire copy is usable.
            return target;
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    private static IEnumerable<string> Enumerate(string root)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            RejectLink(entry);
            yield return entry;
            if (Directory.Exists(entry))
                foreach (string child in Enumerate(entry)) yield return child;
        }
    }

    private static void RejectLink(string path)
    {
        FileSystemInfo entry = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && entry.LinkTarget is not null)
            throw new IOException("Choose a normal local folder without links or junctions: " + path);
    }
}

internal sealed class RegisterSession : IDisposable
{
    internal const string LockName = ".kpos-session.lock";
    private readonly FileStream lease;
    private RegisterSession(FileStream lease) => this.lease = lease;
    public static RegisterSession Open(RegisterFolders folders) => new(new FileStream(Path.Combine(folders.Root, LockName),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose));
    public void Dispose() => lease.Dispose();
}
