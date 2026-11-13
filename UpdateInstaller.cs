using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace Kpos;

internal static class UpdateInstaller
{
    private const string StagePrefix = "kpos-update-";
    private const string ExecutableName = "kPOS.exe";

    public static async Task PrepareAndStart(ReleasePackage release, RegisterFolders folders, IProgress<int> progress,
        CancellationToken cancellation)
    {
        if (!File.Exists(Path.Combine(folders.Root, ExecutableName)) || !File.Exists(folders.Database))
            throw new FileNotFoundException("The installed kPOS application or its database could not be found.");

        string stage = Directory.CreateTempSubdirectory(StagePrefix).FullName;
        string archive = Path.Combine(stage, release.FileName);
        bool handedOff = false;
        try
        {
            await ReleaseUpdates.Download(release, stage, progress, cancellation).ConfigureAwait(false);
            string executable = Path.Combine(stage, ExecutableName);
            using (var package = ZipFile.OpenRead(archive))
            {
                var entry = SingleExecutable(package);
                await using var source = entry.Open();
                await using var target = new FileStream(executable, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 128 * 1024, useAsync: true);
                await source.CopyToAsync(target, cancellation).ConfigureAwait(false);
            }

            if (!ReleaseVersion.TryParse(release.Tag, out ReleaseVersion expected) ||
                !ReleaseVersion.TryParse(FileVersionInfo.GetVersionInfo(executable).ProductVersion, out ReleaseVersion actual) ||
                actual != expected)
                throw new InvalidDataException("The executable version does not match the selected release.");

            File.Delete(archive);
            cancellation.ThrowIfCancellationRequested();
            Start(executable, folders.Root, "--install-update", folders.Root);
            handedOff = true;
        }
        finally
        {
            if (!handedOff)
            {
                TryDelete(archive);
                CleanStage(stage);
            }
        }
    }

    internal static ZipArchiveEntry SingleExecutable(ZipArchive package)
    {
        if (package.Entries.Count != 1 || package.Entries[0].FullName != ExecutableName ||
            package.Entries[0].Length is <= 0 or > 512 * 1024 * 1024)
            throw new InvalidDataException("The update must contain a single kPOS.exe.");
        return package.Entries[0];
    }

    // The installer runs from the extracted copy so Windows can replace the original after it exits.
    public static void InstallAfterExit(RegisterFolders folders, int parentId, long parentStart)
    {
        string staged = Environment.ProcessPath!;
        string stage = Path.GetDirectoryName(staged)!;
        string installed = Path.Combine(folders.Root, ExecutableName);
        try
        {
            if (!IsStage(stage) || !string.Equals(Path.GetFileName(staged), ExecutableName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update was not started from a kPOS staging folder.");

            if (!WaitForExit(parentId, parentStart, 30000))
                throw new IOException("Close all open kPOS windows and run the update again.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception)
        {
            ShowError("kPOS could not start the update.", error);
            return;
        }

        string? previous = null;
        bool canRestart = true;
        try
        {
            if (!File.Exists(installed) || !File.Exists(folders.Database))
                throw new FileNotFoundException("The installed kPOS application or its database could not be found.");

            folders.BackUpBeforeSchemaUpgrade();
            previous = folders.InstallExecutable(staged);
            if (previous is not null)
            {
                // Validate database startup while the previous executable is still available.
                using var data = new RegisterData(folders.Database);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException)
        {
            if (previous is not null)
            {
                try
                {
                    folders.RestoreExecutable(previous);
                }
                catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                {
                    canRestart = false;
                    MessageBox.Show($"The update failed and the previous executable is still at:\n{previous}\n\n{error.Message}\n{restoreError.Message}",
                        "kPOS Update", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            if (canRestart)
                ShowError("kPOS could not finish the update.", error);
        }

        if (!canRestart || !File.Exists(installed))
            return;

        try
        {
            Start(installed, folders.Root, "--after-update", stage);
        }
        catch (Exception error) when (error is Win32Exception or IOException)
        {
            ShowError($"Open kPOS from {installed} to continue.", error);
        }
    }

    public static void CleanAfterExit(string stage, int workerId, long workerStart)
    {
        try
        {
            if (IsStage(stage) && WaitForExit(workerId, workerStart, 5000))
                CleanStage(stage);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            // Staging cleanup must not stop the register from opening.
        }
    }

    private static void Start(string executable, string workingDirectory, string command, string path)
    {
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false };
        start.ArgumentList.Add(command);
        start.ArgumentList.Add(current.Id.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(path);
        using var child = Process.Start(start) ?? throw new Win32Exception("The kPOS process could not start.");
    }

    private static bool WaitForExit(int id, long started, int milliseconds)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return process.HasExited || process.StartTime.ToUniversalTime().Ticks != started || process.WaitForExit(milliseconds);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool IsStage(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return string.Equals(Path.GetDirectoryName(fullPath), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(fullPath).StartsWith(StagePrefix, StringComparison.Ordinal)
            && Directory.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) == 0;
    }

    private static void CleanStage(string stage)
    {
        try
        {
            if (!IsStage(stage))
                return;
            TryDelete(Path.Combine(stage, ExecutableName));
            Directory.Delete(stage);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The running register can continue even if Windows still holds a staged file open.
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Leave the original update error available to the caller.
        }
    }

    private static void ShowError(string message, Exception error) =>
        MessageBox.Show($"{message}\n\n{error.Message}", "kPOS Update", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
