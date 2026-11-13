using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class UpdateInstallerTests
{
    [TestMethod]
    [DataRow("kPOS.exe", null, true)]
    [DataRow("../kPOS.exe", null, false)]
    [DataRow("kPOS.exe", "../register.db", false)]
    public void UpdateZipAcceptsOnlyTheRootExecutable(string name, string? extra, bool accepted)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var content = zip.CreateEntry(name).Open())
                content.WriteByte(1);
            if (extra is not null)
                zip.CreateEntry(extra);
        }
        buffer.Position = 0;
        using var package = new ZipArchive(buffer, ZipArchiveMode.Read);
        if (accepted)
            Assert.AreEqual("kPOS.exe", UpdateInstaller.SingleExecutable(package).FullName);
        else
            Assert.ThrowsExactly<InvalidDataException>(() => UpdateInstaller.SingleExecutable(package));
    }
}
