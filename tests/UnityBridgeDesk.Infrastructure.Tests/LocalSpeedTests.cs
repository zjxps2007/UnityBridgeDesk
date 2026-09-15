using System.Diagnostics;
using UnityBridgeDesk.Infrastructure.SpeedBench;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class LocalSpeedTests
{
    [TestMethod]
    public async Task FreshWorkspacesRejectReuseAndOnlyDeleteTheOwnedTrial()
    {
        string data = SampleData.TestDirectory(), token = Guid.NewGuid().ToString("N"); Guid id = Guid.NewGuid();
        string root = await LocalWorkspace.Create(data, id, token, default);
        await Assert.ThrowsExactlyAsync<IOException>(() => LocalWorkspace.Create(data, id, token, default));
        string outside = Path.Combine(data, "personal-project"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "keep.txt"), "original");
        await Assert.ThrowsExactlyAsync<IOException>(() => LocalWorkspace.Delete(data, outside, id, token));
        await Assert.ThrowsExactlyAsync<IOException>(() => LocalWorkspace.Delete(data, root, id, new string('0', 32)));
        File.WriteAllText(Path.Combine(root, "temp", "previous.dmp"), "dump");
        await LocalWorkspace.Delete(data, root, id, token);
        Assert.IsFalse(Directory.Exists(root)); Assert.AreEqual("original", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        string next = await LocalWorkspace.Create(data, Guid.NewGuid(), token, default);
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(next, "temp")).Length);
        Assert.DoesNotContain("USERPROFILE", LocalWorkspace.EnvironmentFor(next).Keys);
    }
    [TestMethod]
    public async Task LiveWorkerPreventsDeletionAndDeadOwnerCanBeRecovered()
    {
        string data = SampleData.TestDirectory(), token = Guid.NewGuid().ToString("N"); Guid id = Guid.NewGuid();
        string root = await LocalWorkspace.Create(data, id, token, default);
        using var process = Process.GetCurrentProcess();
        await SpeedFiles.Write(Path.Combine(root, "worker-process.json"), new LocalProcessIdentity(process.Id, process.StartTime.ToUniversalTime()));
        await Assert.ThrowsExactlyAsync<IOException>(() => LocalWorkspace.Delete(data, root, id, token));
        Assert.IsTrue(Directory.Exists(root));
        await SpeedFiles.Write(Path.Combine(root, "worker-process.json"), new LocalProcessIdentity(int.MaxValue, DateTimeOffset.UtcNow));
        await LocalWorkspace.Recover(data, null, default); Assert.IsFalse(Directory.Exists(root));
    }
    [TestMethod]
    public async Task OnlyHeartbeatOfThisProjectAndRecordedDeadEditorIsRemoved()
    {
        string data = SampleData.TestDirectory(), token = Guid.NewGuid().ToString("N"); Guid id = Guid.NewGuid();
        string root = await LocalWorkspace.Create(data, id, token, default), instances = Path.Combine(data, "instances"); Directory.CreateDirectory(instances);
        await SpeedFiles.Write(Path.Combine(root, "editor-process.json"), new LocalProcessIdentity(int.MaxValue, DateTimeOffset.UtcNow));
        await SpeedFiles.Write(Path.Combine(instances, "own.json"), new { pid = int.MaxValue, projectPath = Path.Combine(root, "project") });
        await SpeedFiles.Write(Path.Combine(instances, "foreign-project.json"), new { pid = int.MaxValue, projectPath = data });
        await SpeedFiles.Write(Path.Combine(instances, "foreign-pid.json"), new { pid = Environment.ProcessId, projectPath = Path.Combine(root, "project") });
        await LocalWorkspace.CleanInstance(root, instances);
        Assert.IsFalse(File.Exists(Path.Combine(instances, "own.json")));
        Assert.IsTrue(File.Exists(Path.Combine(instances, "foreign-project.json"))); Assert.IsTrue(File.Exists(Path.Combine(instances, "foreign-pid.json")));
    }
    [TestMethod]
    public async Task LocalWorkerRefusesMissingIsolationBeforeLaunchingUnity()
    {
        string data = SampleData.TestDirectory(), token = Guid.NewGuid().ToString("N"); Guid id = Guid.NewGuid();
        string root = await LocalWorkspace.Create(data, id, token, default);
        var trial = new SpeedTrial(id, 1, 1, "v0.2.1", "F01", "first");
        var request = new GuestRequest(Guid.NewGuid(), trial, new(), "6000.3.23f1", "", "", "cli", "connector", "0.2.1", "fixture", token);
        string input = Path.Combine(root, "request.json"), output = Path.Combine(root, "result.json");
        await SpeedFiles.Write(input, new LocalTrialRequest(request, root, Path.Combine(data, "Unity.exe"), "hash"));
        await Assert.ThrowsExactlyAsync<IOException>(() => SpeedGuest.RunLocalFile(input, output));
        Assert.IsFalse(File.Exists(output)); Assert.IsFalse(Directory.Exists(Path.Combine(root, "project")));
    }
}
