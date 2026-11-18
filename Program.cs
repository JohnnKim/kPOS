using Microsoft.Data.Sqlite;

namespace Kpos;

internal static class Program
{
    internal static string? InstallationExecutable
    {
        get
        {
#if KPOS_SINGLE_FILE
            return Environment.ProcessPath;
#else
            return null;
#endif
        }
    }
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length == 1 && args[0] == "--home")
        {
            Application.Run(new HomeTerminalForm());
            return;
        }

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

        string? overridePath = Environment.GetEnvironmentVariable("KPOS_DATA_DIRECTORY");
        string settings = args.Contains("--setup") && !string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(overridePath, "installation-folder.txt") : RegisterInstallation.SettingsPath;
        if (args.Length == 5 && args[0] == "--open-installed" && int.TryParse(args[1], out int parent) && parent > 0 &&
            long.TryParse(args[2], out long parentStarted) && parentStarted > 0)
        {
            try
            {
                if (!UpdateInstaller.WaitForExit(parent, parentStarted, 30000))
                    throw new IOException("The previous kPOS process is still closing. Open the installed copy again after it exits.");
                overridePath = args[3];
                settings = args[4];
            }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
            { MessageBox.Show(error.Message, "kPOS could not reopen"); return; }
        }
        if (args.Contains("--setup"))
        {
            using var setup = new InstallationForm(settings, InstallationExecutable, initialDirectory: overridePath);
            if (setup.ShowDialog() != DialogResult.OK) return;
            overridePath = setup.InstalledFolders!.Root;
            try { if (RegisterInstallation.StartInstalledIfDifferent(setup.InstalledFolders, settings)) return; }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
            { MessageBox.Show(error.Message, "Open kPOS from " + setup.InstalledFolders.Root); return; }
        }
        RunRegister(settings, overridePath);
    }

    private static void RunRegister(string settings, string? overridePath)
    {
        RegisterData? data = null;
        RegisterFolders? folders = null;
        RegisterSession? session = null;
        string? previousExecutable = null;
        while (true)
        {
            try
            {
                bool installed = false;
                if (folders is null) folders = RegisterFolders.Choose(settings, overridePath, out installed);
                if (folders is null)
                    return;
                if (installed && RegisterInstallation.StartInstalledIfDifferent(folders, settings)) return;

                folders.Create();
                session = RegisterSession.Open(folders);

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
                    session.Dispose();
                    return;
                }
            }
#endif

                data = new RegisterData(folders.Database);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException or System.ComponentModel.Win32Exception)
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
                session?.Dispose();
                MessageBox.Show(
                    $"The register could not open its data folder.\n\n{folders?.Root}\n{details}",
                    "kPOS could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            using (session)
            {
                string? destination;
                using (data)
                using (var register = new RegisterForm(data, folders))
                {
                    Application.Run(register);
                    destination = register.RequestedDirectory;
                }
                data = null;
                previousExecutable = null;
                if (destination is null) return;
                using var move = new DirectoryMoveForm(folders, destination, settings);
                if (move.ShowDialog() == DialogResult.OK && move.Destination is RegisterFolders moved)
                {
                    folders = moved;
                    try { if (RegisterInstallation.StartInstalledIfDifferent(moved, settings)) return; }
                    catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
                    { MessageBox.Show(error.Message, "Open kPOS from " + moved.Root); return; }
                }
            }
        }
    }
}
