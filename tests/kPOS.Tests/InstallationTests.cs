using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class InstallationTests
{
    [TestMethod]
    public void DirectoryChangeCopiesAllRecordsAndKeepsTheOriginalDatabase()
    {
        using var fixture = new Folders();
        fixture.Source.Create();
        string settings = Path.Combine(fixture.Root, "profile", "installation-folder.txt");
        using (var data = new RegisterData(fixture.Source.Database))
        {
            data.TaxRate = 7.25m;
            data.SaveInventoryEntry(data.FindProduct("101")!.Barcode, data.CurrentBusinessDate(), InventoryEntry.OpeningCount, 17);
            data.SubmitHomeOrder(new(Guid.NewGuid(), data.FindProduct("101")!.Barcode, 4));
        }
        File.WriteAllText(Path.Combine(fixture.Source.Root, "kPOS.exe"), "fixture executable");
        File.WriteAllText(Path.Combine(fixture.Source.Root, "custom-settings.txt"), "preserve this too");
        File.WriteAllText(Path.Combine(fixture.Source.DailyArchives, "day.zip"), "archive fixture");
        File.WriteAllText(Path.Combine(fixture.Source.Backups, "backup.zip"), "backup fixture");
        fixture.Source.Remember(settings);
        using (RegisterSession.Open(fixture.Source))
        {
            Assert.Throws<IOException>(() => RegisterSession.Open(fixture.Source));
            RegisterFolders moved = RegisterInstallation.CopyTo(fixture.Source, fixture.Target + Path.DirectorySeparatorChar, settings);
            Assert.AreEqual(moved.Root, File.ReadAllText(settings));
            using var copy = new RegisterData(moved.Database);
            Assert.AreEqual(7.25m, copy.TaxRate);
            Assert.AreEqual(1, copy.ReadHomeOrders().Count);
            DateOnly day = copy.CurrentBusinessDate();
            Assert.AreEqual(17L, copy.ReadOrderItems(day, day, day).Single(item => item.Product.Code == "101").OnHand);
            Assert.AreEqual("archive fixture", File.ReadAllText(Path.Combine(moved.DailyArchives, "day.zip")));
            Assert.AreEqual("backup fixture", File.ReadAllText(Path.Combine(moved.Backups, "backup.zip")));
            Assert.AreEqual("preserve this too", File.ReadAllText(Path.Combine(moved.Root, "custom-settings.txt")));
            Assert.AreEqual("fixture executable", File.ReadAllText(Path.Combine(moved.Root, "kPOS.exe")));
            Assert.IsFalse(File.Exists(Path.Combine(moved.Root, RegisterSession.LockName)));
        }
        Assert.IsTrue(File.Exists(fixture.Source.Database));
        using var original = new RegisterData(fixture.Source.Database);
        Assert.AreEqual(7.25m, original.TaxRate);
    }

    [TestMethod]
    public void InvalidOrLockedMovesLeaveTheSavedPathAndExistingFilesIntact()
    {
        using var fixture = new Folders();
        fixture.Source.Create();
        string settings = Path.Combine(fixture.Root, "profile", "installation-folder.txt");
        using var open = new RegisterData(fixture.Source.Database);
        fixture.Source.Remember(settings);
        Assert.Throws<IOException>(() => RegisterInstallation.CopyTo(fixture.Source, fixture.Target, settings));
        Assert.AreEqual(fixture.Source.Root, File.ReadAllText(settings));
        Assert.IsFalse(Directory.Exists(fixture.Target));
        Assert.IsFalse(Directory.EnumerateDirectories(fixture.Root, ".kpos-move-*").Any());
        Assert.Throws<IOException>(() => RegisterInstallation.ValidateDestination(fixture.Source, fixture.Source.Root));
        Assert.Throws<IOException>(() => RegisterInstallation.ValidateDestination(fixture.Source, Path.Combine(fixture.Source.Root, "nested")));
        Assert.Throws<IOException>(() => RegisterInstallation.ValidateDestination(fixture.Source, fixture.Root));
        Directory.CreateDirectory(fixture.Target);
        File.WriteAllText(Path.Combine(fixture.Target, "existing.txt"), "keep");
        Assert.Throws<IOException>(() => RegisterInstallation.CopyTo(fixture.Source, fixture.Target, settings));
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(fixture.Target, "existing.txt")));
        Assert.AreEqual(fixture.Source.Root, File.ReadAllText(settings));
    }

    [TestMethod]
    public void MissingSavedDirectoryFallsBackToFreshSetupAndValidInstallationsAreFound()
    {
        using var fixture = new Folders();
        string settings = Path.Combine(fixture.Root, "installation-folder.txt");
        File.WriteAllText(settings, Path.Combine(fixture.Root, "deleted-register"));
        Assert.IsNull(RegisterFolders.FindExisting(settings, []));
        fixture.Source.Create();
        using (var data = new RegisterData(fixture.Source.Database)) { }
        Assert.AreEqual(fixture.Source.Root, RegisterFolders.FindExisting(settings, [fixture.Source.Root])?.Root);
        fixture.Source.Remember(settings);
        Assert.AreEqual(fixture.Source.Root, RegisterFolders.FindExisting(settings, [])?.Root);
        Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "kPOS"),
            RegisterInstallation.DefaultDirectory);
    }

    [TestMethod]
    public void InstallationCreatesAWorkingRegisterAndRemembersOnlyItsCompletedFolder()
    {
        using var fixture = new Folders();
        string settings = Path.Combine(fixture.Root, "profile", "installation-folder.txt");
        RegisterInstallation.Install(fixture.Source, settings, executable: null);
        Assert.AreEqual(fixture.Source.Root, File.ReadAllText(settings));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Source.Root, RegisterSession.LockName)));
        using var data = new RegisterData(fixture.Source.Database);
        Assert.IsNotNull(data.FindProduct("101"));
        Assert.IsTrue(Directory.Exists(fixture.Source.Backups));
        Assert.IsTrue(Directory.Exists(fixture.Source.DailyArchives));
    }

    private sealed class Folders : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("kpos-install-test-").FullName;
        public RegisterFolders Source { get; }
        public string Target => Path.Combine(Root, "new-register");
        public Folders() => Source = new(Path.Combine(Root, "old-register"));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
