using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Kpos.Tests;

[TestClass]
public sealed class HostConnectionsTests
{
  [TestMethod]
public void StoreTerminal_ReturnsTerminalName_WhenStoreTerminalConnected()
{
var connections = new HostConnections(true);
var id = Guid.NewGuid();
connections.Heartbeat(new TerminalHello(id, "Front Register"), "Store Terminal");
Assert.AreEqual("Front Register", connections.StoreTerminal(id));
}

[TestMethod]
public void StoreTerminal_ThrowsArgumentException_WhenTerminalNotConnected()
{
var connections = new HostConnections(true);
Assert.ThrowsException<ArgumentException>(() => connections.StoreTerminal(Guid.NewGuid()));
}

[TestMethod]
public void StoreTerminal_ThrowsArgumentException_WhenRoleIsNotStoreTerminal()
{
var connections = new HostConnections(true);
var id = Guid.NewGuid();
connections.Heartbeat(new TerminalHello(id, "Viewer"), "Management Terminal");
Assert.ThrowsException<ArgumentException>(() => connections.StoreTerminal(id));
}

[TestMethod]
public void StoreTerminal_PreservesUpdatedNameFromHeartbeat()
{
var connections = new HostConnections(true);
var id = Guid.NewGuid();
connections.Heartbeat(new TerminalHello(id, "Register 1"), "Store Terminal");
connections.Heartbeat(new TerminalHello(id, "Register 1 Updated"), "Store Terminal");
Assert.AreEqual("Register 1 Updated", connections.StoreTerminal(id));
var snapshot = connections.Snapshot();
Assert.AreEqual(1, snapshot.Length);
Assert.AreEqual("Register 1 Updated", snapshot[0].Name);
}
}
