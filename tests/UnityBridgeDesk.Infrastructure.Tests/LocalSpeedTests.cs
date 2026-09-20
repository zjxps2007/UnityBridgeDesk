using System.Diagnostics;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Infrastructure.SpeedBench;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class LocalSpeedTests
{
    [TestMethod]
    public async Task CleanupRemovesReadOnlyUpmResidue()
    {
        string data = SampleData.TestDirectory(), token = Guid.NewGuid().ToString("N"); Guid id = Guid.NewGuid();
        string root = await LocalWorkspace.Create(data, id, token, default);
        string file = Path.Combine(root, "temp", "upm-test.lock");
        File.WriteAllText(file, "12345"); File.SetAttributes(file, FileAttributes.ReadOnly | FileAttributes.Archive);
        await LocalWorkspace.Delete(data, root, id, token);
        Assert.IsFalse(Directory.Exists(root));
    }
    [TestMethod]
    public async Task FailedDeletionPreservesOwnershipAndCanBeRetried()
    {
        string data = SampleData.TestDirectory(), token = Guid.NewGuid().ToString("N"); Guid id = Guid.NewGuid();
        string root = await LocalWorkspace.Create(data, id, token, default);
        string file = Path.Combine(root, "temp", "locked.tmp");
        using (var locked = new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => LocalWorkspace.Delete(data, root, id, token));
            await LocalWorkspace.Check(root, id, token);
        }
        await LocalWorkspace.Recover(data, null, default);
        Assert.IsFalse(Directory.Exists(root));
    }
    [TestMethod]
    public async Task MissingOwnerUsesMatchingFailedRunEvidenceAndPreservesResults()
    {
        var (data, root, evidence) = await LegacyResidue();
        string keep = Path.Combine(data, "personal.txt"); File.WriteAllText(keep, "keep");
        await LocalWorkspace.Recover(data, null, default);
        Assert.IsFalse(Directory.Exists(root));
        Assert.IsTrue(File.Exists(Path.Combine(evidence, "request.json")));
        Assert.AreEqual("keep", File.ReadAllText(keep));
    }
    [TestMethod]
    public async Task MissingOwnerNeverRecoversALiveRecordedWorker()
    {
        var (data, root, _) = await LegacyResidue(liveWorker: true);
        await Assert.ThrowsExactlyAsync<IOException>(() => LocalWorkspace.Recover(data, null, default));
        Assert.IsTrue(File.Exists(Path.Combine(root, "temp", "upm-test.lock")));
        Assert.IsFalse(File.Exists(Path.Combine(root, ".local-owner.json")));
    }
    [TestMethod]
    public async Task MissingOwnerRejectsMismatchedRecordedPath()
    {
        var (data, root, evidence) = await LegacyResidue();
        var request = await SpeedFiles.Read<LocalTrialRequest>(Path.Combine(evidence, "request.json"));
        await SpeedFiles.Write(Path.Combine(evidence, "request.json"), request with { WorkRoot = data });
        await Assert.ThrowsExactlyAsync<IOException>(() => LocalWorkspace.Recover(data, null, default));
        Assert.IsTrue(Directory.Exists(root));
    }
    [TestMethod]
    public async Task UnknownOwnerlessDirectoryIsPreservedWithRecoveryGuidance()
    {
        string data = SampleData.TestDirectory(), root = Path.Combine(LocalWorkspace.Parent(data), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "keep.txt"), "unknown");
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => LocalWorkspace.Recover(data, null, default));
        StringAssert.Contains(error.Message, "소유");
        Assert.AreEqual("unknown", File.ReadAllText(Path.Combine(root, "keep.txt")));
    }
    [TestMethod]
    public async Task CleanupJournalSurvivesMissingInnerMarkerAndIsRemovedAfterRecovery()
    {
        string data = SampleData.TestDirectory(), token = Guid.NewGuid().ToString("N"); Guid id = Guid.NewGuid();
        string root = await LocalWorkspace.Create(data, id, token, default);
        string marker = Path.Combine(root, ".local-owner.json"), journal = root + ".cleanup.json";
        File.Copy(marker, journal); File.Delete(marker);
        await LocalWorkspace.Recover(data, null, default);
        Assert.IsFalse(Directory.Exists(root)); Assert.IsFalse(File.Exists(journal));
    }
    [TestMethod]
    public async Task CompletedCleanupJournalIsRemovedWithoutRecreatingWorkspace()
    {
        string data = SampleData.TestDirectory(), token = Guid.NewGuid().ToString("N"); Guid id = Guid.NewGuid();
        string root = await LocalWorkspace.Create(data, id, token, default);
        var owner = await SpeedFiles.Read<LocalWorkspaceOwner>(Path.Combine(root, ".local-owner.json"));
        await LocalWorkspace.Delete(data, root, id, token);
        await SpeedFiles.Write(root + ".cleanup.json", owner);
        await LocalWorkspace.Recover(data, null, default);
        Assert.IsFalse(Directory.Exists(root)); Assert.IsFalse(File.Exists(root + ".cleanup.json"));
    }
    private static async Task<(string Data, string Root, string Evidence)> LegacyResidue(bool liveWorker = false)
    {
        string data = SampleData.TestDirectory(), token = Guid.NewGuid().ToString("N"); Guid id = Guid.NewGuid(), runId = Guid.NewGuid();
        string root = await LocalWorkspace.Create(data, id, token, default);
        string file = Path.Combine(root, "temp", "upm-test.lock");
        File.WriteAllText(file, "12345"); File.SetAttributes(file, FileAttributes.ReadOnly);
        File.Delete(Path.Combine(root, ".local-owner.json"));
        var trial = new SpeedTrial(id, 1, 1, "v0.2.1", "F01", "first");
        var request = new GuestRequest(runId, trial, new(), "6000.3.23f1", "", "", "cli", "connector", "0.2.1", "fixture", token);
        string runRoot = Path.Combine(data, "speed", "local-runs", runId.ToString("N")), evidence = Path.Combine(runRoot, id.ToString("N"));
        await SpeedFiles.Write(Path.Combine(evidence, "request.json"), new LocalTrialRequest(request, root, Path.Combine(data, "Unity.exe"), "hash"));
        using var process = Process.GetCurrentProcess();
        var identity = liveWorker ? new LocalProcessIdentity(process.Id, process.StartTime.ToUniversalTime()) : new(int.MaxValue, DateTimeOffset.UtcNow);
        await SpeedFiles.Write(Path.Combine(evidence, "worker.json"), new ProcessResult(ProcessOutcome.Cancelled, null, "", "", 1, identity.Pid, identity.StartedAt));
        await SpeedFiles.Write(Path.Combine(evidence, "editor-process.json"), new LocalProcessIdentity(int.MaxValue, DateTimeOffset.UtcNow));
        var run = new SpeedRun(runId, DateTimeOffset.UtcNow, LocalWorkspace.Schema, "local-direct-measurement", new(), null, [], [trial],
            [new(trial, "cleanup-failed", null, "read-only lock", false, 1)], "cleanup-failed", "test");
        await SpeedFiles.Write(Path.Combine(runRoot, "run.json"), run);
        return (data, root, evidence);
    }
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
