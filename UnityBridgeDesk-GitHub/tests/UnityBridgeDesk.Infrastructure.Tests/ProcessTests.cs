using System.Diagnostics;
using System.Text.Json;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Infrastructure.Execution;
using UnityBridgeDesk.Infrastructure.Bridge;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class ProcessTests
{
    private static string Worker => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/UnityBridgeDesk.Worker/bin/Release/net10.0-windows/UnityBridgeDesk.Worker.exe"));
    private static ProcessCommand Command(string root, params string[] args) => new(Guid.NewGuid(), Worker, ["--fixture", ..args], root);
    [TestMethod] public async Task ArgumentArrayPreservesKoreanQuotesAndShellCharacters()
    {
        var result = await new WorkerRunner(Worker).RunAsync(Command(SampleData.TestDirectory(), "echo", "한글 공백", "a\"b", "$(not-a-command)", "&"), TimeSpan.FromSeconds(10));
        Assert.AreEqual(ProcessOutcome.Exited, result.Outcome, result.Error);
        CollectionAssert.AreEqual(new[] { "한글 공백", "a\"b", "$(not-a-command)", "&" }, JsonSerializer.Deserialize<string[]>(result.Output));
    }
    [TestMethod] public async Task SimultaneousLargeOutputDrainsAndExitCodeIsPreserved()
    {
        var runner = new WorkerRunner(Worker); var root = SampleData.TestDirectory();
        var result = await runner.RunAsync(Command(root, "large"), TimeSpan.FromSeconds(15));
        Assert.AreEqual(ProcessOutcome.Exited, result.Outcome, result.Error);
        Assert.AreEqual(200000, result.Output.Length); Assert.AreEqual(200000, result.Error.Length);
        result = await runner.RunAsync(Command(root, "fail"), TimeSpan.FromSeconds(10));
        Assert.AreEqual(7, result.ExitCode);
    }
    [TestMethod] public async Task CancelKillsOnlyOwnedChildAndNextInvocationHasFreshOutput()
    {
        var runner = new WorkerRunner(Worker); var root = SampleData.TestDirectory();
        using var cancel = new CancellationTokenSource(); int? pid = null;
        var task = runner.RunAsync(Command(root, "hang"), TimeSpan.FromSeconds(20), frame =>
        { if (frame.Kind == "started") { using var d = JsonDocument.Parse(frame.Text); pid = d.RootElement.GetProperty("pid").GetInt32(); cancel.Cancel(); } }, cancel.Token);
        var result = await task;
        Assert.AreEqual(ProcessOutcome.Cancelled, result.Outcome);
        Assert.IsNotNull(pid);
        Assert.IsFalse(Alive(pid.Value)); Assert.IsTrue(Alive(Environment.ProcessId));
        var next = await runner.RunAsync(Command(root, "echo", "next"), TimeSpan.FromSeconds(10));
        Assert.AreEqual("next", JsonSerializer.Deserialize<string[]>(next.Output)!.Single());
    }
    [TestMethod] public async Task TimeoutAndOutputLimitDoNotBecomeSuccessfulExits()
    {
        var runner = new WorkerRunner(Worker); var root = SampleData.TestDirectory();
        Assert.AreEqual(ProcessOutcome.TimedOut, (await runner.RunAsync(Command(root, "hang"), TimeSpan.FromMilliseconds(500))).Outcome);
        Assert.AreEqual(ProcessOutcome.OutputLimit, (await runner.RunAsync(Command(root, "large") with { OutputLimit = 1024 }, TimeSpan.FromSeconds(10))).Outcome);
    }
    [TestMethod] public async Task ChangedExecutableIsRejectedBeforeExecution()
    {
        var result = await new WorkerRunner(Worker).RunAsync(Command(SampleData.TestDirectory(), "echo") with { ExpectedSha256 = new string('0', 64) }, TimeSpan.FromSeconds(10));
        Assert.AreEqual(ProcessOutcome.StartFailed, result.Outcome); Assert.IsNull(result.ProcessId);
    }
    [TestMethod] public async Task FrozenPythonAnsiOutputDecodesWithoutReplacingKoreanCharacters()
    {
        var result=await new WorkerRunner(Worker).RunAsync(Command(SampleData.TestDirectory(),"ansi") with{OutputCodePage=949},TimeSpan.FromSeconds(10));
        Assert.AreEqual(ProcessOutcome.Exited,result.Outcome);Assert.AreEqual("한글 공백",result.Output);
    }
    [TestMethod] public void DiscoveryRejectsStaleHeartbeatWrongPidAndPortChangeWithoutDeleting()
    {
        string root = SampleData.TestDirectory(); string file = Path.Combine(root, "heartbeat.json");
        using var current = Process.GetCurrentProcess();
        var target = new BridgeTarget(root, current.MainModule!.FileName, Worker, new string('0',64), current.Id);
        void Write(long timestamp, int pid) => File.WriteAllText(file, JsonSerializer.Serialize(new { projectPath = root, pid, port = 9876, state = "ready", timestamp, unityVersion = "fixture", connectorVersion = "0.2.1", compileErrors = false }));
        var discovery = new InstanceDiscovery(root);
        Write(1, current.Id);
        Assert.AreEqual(BridgeFailure.StaleInstance, Assert.ThrowsExactly<BridgeException>(() => discovery.Find(target)).Failure);
        Write(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), current.Id + 1);
        Assert.AreEqual(BridgeFailure.WrongProcess, Assert.ThrowsExactly<BridgeException>(() => discovery.Find(target)).Failure);
        Write(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), current.Id);
        Assert.AreEqual(BridgeFailure.PortChanged, Assert.ThrowsExactly<BridgeException>(() => discovery.Find(target, 9877)).Failure);
        Assert.AreEqual(current.Id, discovery.Find(target).Pid); Assert.IsTrue(File.Exists(file));
    }
    private static bool Alive(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; } }
    [TestMethod] public void DiscoveryRetriesOnlyTransientMissingFileAndRecordsTheRetry()
    {
        string root=SampleData.TestDirectory();using var current=Process.GetCurrentProcess();
        var target=new BridgeTarget(root,current.MainModule!.FileName,Worker,new string('0',64),current.Id);
        var discovery=new InstanceDiscovery(root,attempt=>
        {
            if(attempt==2)File.WriteAllText(Path.Combine(root,"heartbeat.json"),JsonSerializer.Serialize(new{projectPath=root,pid=current.Id,port=8090,state="ready",timestamp=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),unityVersion="fixture",connectorVersion="0.2.1",compileErrors=false}));
        });
        Assert.AreEqual(2,discovery.Find(target).DiscoveryRetries);
    }
}
