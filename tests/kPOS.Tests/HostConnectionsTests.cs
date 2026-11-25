using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kpos.Tests;

[TestClass]
public sealed class HostConnectionsTests
{
    [TestMethod]
    public void StoreTerminalRequiresConnectedStoreTerminalAndUpdatesLastSeen()
    {
        var connections = new HostConnections(checkoutEnabled: true);
        var id = Guid.NewGuid();

        Assert.ThrowsExactly<ArgumentException>(() => connections.StoreTerminal(id));

        connections.Heartbeat(new TerminalHello(id, "Register 2"), "Store Terminal");
        DateTimeOffset initialSeen = connections.Snapshot().Single().LastSeen;

        string name = connections.StoreTerminal(id);

        Assert.AreEqual("Register 2", name);
        Assert.IsTrue(connections.Snapshot().Single().LastSeen >= initialSeen);
    }

    [TestMethod]
    public void StoreTerminalRejectsNonStoreRoles()
    {
        var connections = new HostConnections(checkoutEnabled: true);
        var id = Guid.NewGuid();
        connections.Heartbeat(new TerminalHello(id, "Office PC"), "Remote Connection");

        Assert.ThrowsExactly<ArgumentException>(() => connections.StoreTerminal(id));
    }
}
