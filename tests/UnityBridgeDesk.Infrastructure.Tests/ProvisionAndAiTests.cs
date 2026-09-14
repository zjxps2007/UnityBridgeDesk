using System.Text.Json;
using System.Text.Json.Nodes;
using UnityBridgeDesk.Infrastructure.Ai;
using UnityBridgeDesk.Infrastructure.Execution;
using UnityBridgeDesk.Infrastructure.Installation;
using UnityBridgeDesk.Infrastructure.Storage;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class ProvisionAndAiTests
{
    [TestMethod] public void RollbackPreservesUnrelatedChangesAndRefusesReplacementFromAnotherWriter()
    {
        string root = SampleData.TestDirectory(), manifest = Path.Combine(root, "manifest.json");
        File.WriteAllText(manifest, "{\"dependencies\":{\"keep\":\"1\"}}");
        var change = new PackageChange(root, manifest, ProjectFiles.HashFile(manifest), null, "file:C:/connector", "hash", "0.2.1", root);
        Assert.IsTrue(PackageProvisioner.Apply(change));
        var node = JsonNode.Parse(File.ReadAllText(manifest))!; node["dependencies"]!["keep"] = "2";
        File.WriteAllText(manifest, node.ToJsonString());
        Assert.IsTrue(PackageProvisioner.Rollback(change));
        node = JsonNode.Parse(File.ReadAllText(manifest))!;
        Assert.AreEqual("2", node["dependencies"]!["keep"]!.GetValue<string>());
        node["dependencies"]!["com.zjxps2007.unity-bridge-connector"] = "user-change";
        File.WriteAllText(manifest, node.ToJsonString());
        Assert.IsFalse(PackageProvisioner.Rollback(change));
        Assert.IsTrue(File.ReadAllText(manifest).Contains("user-change"));
    }
    [TestMethod] public void ApplyRejectsChangedManifestAfterReview()
    {
        string root = SampleData.TestDirectory(), path = Path.Combine(root, "manifest.json");
        File.WriteAllText(path, "{\"dependencies\":{}}");
        var change = new PackageChange(root, path, ProjectFiles.HashFile(path), null, "file:C:/connector", "hash", "0.2.1", root);
        File.AppendAllText(path, " ");
        Assert.ThrowsExactly<IOException>(() => PackageProvisioner.Apply(change));
    }
    [TestMethod] public async Task FreshClonesHaveNoPriorTrialMarkerAndBaselineRemainsUnchanged()
    {
        string root = SampleData.TestDirectory(), source = Path.Combine(root, "source");
        foreach (string name in new[] { "Assets", "Packages", "ProjectSettings" }) Directory.CreateDirectory(Path.Combine(source, name));
        File.WriteAllText(Path.Combine(source, "Assets", "한글.txt"), "baseline");
        string hash = await ProjectFiles.FingerprintAsync(source);
        await ProjectFiles.CloneAsync(source, Path.Combine(root, "one"), hash, default);
        File.WriteAllText(Path.Combine(root, "one", "Assets", "previous-trial.txt"), "must not leak");
        await ProjectFiles.CloneAsync(source, Path.Combine(root, "two"), hash, default);
        Assert.IsFalse(File.Exists(Path.Combine(root, "two", "Assets", "previous-trial.txt")));
        Assert.AreEqual(hash, await ProjectFiles.FingerprintAsync(source));
        await Assert.ThrowsExactlyAsync<IOException>(() => ProjectFiles.CloneAsync(source, Path.Combine(root, "two"), hash, default));
    }
    [TestMethod] public void CleanupRequiresManagedDescendantAndMatchingOwnership()
    {
        string root = SampleData.TestDirectory(), child = Path.Combine(root, "trial"); Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, ".desk-owner"), "mine");
        Assert.ThrowsExactly<IOException>(() => ProjectFiles.DeleteOwnedFolder(root, root, "mine"));
        Assert.ThrowsExactly<IOException>(() => ProjectFiles.DeleteOwnedFolder(root, child, "wrong"));
        ProjectFiles.DeleteOwnedFolder(root, child, "mine"); Assert.IsFalse(Directory.Exists(child));
    }
    [TestMethod] public void CodexFramingPreservesChunksNullUsageAndExplicitSession()
    {
        string id = Guid.NewGuid().ToString(); bool stopped = false;
        var events = new CodexEvents(id, 10, null, (_, _) => { }, () => stopped = true);
        string text = JsonSerializer.Serialize(new { type = "thread.started", thread_id = id }) + "\n{\"type\":\"turn.completed\"}\n";
        foreach (char c in text) events.Feed(c.ToString()); events.Finish();
        Assert.IsTrue(events.Completed); Assert.IsFalse(stopped); Assert.IsNull(events.InputTokens); Assert.IsNull(events.OutputTokens);
    }
    [TestMethod] public void ForeignSessionMalformedAndDuplicateCompletionFail()
    {
        foreach (string invalid in new[] { "{bad}", "{\"type\":\"thread.started\",\"thread_id\":\"" + Guid.NewGuid() + "\"}", "{\"type\":\"turn.completed\"}\n{\"type\":\"turn.completed\"}" })
        {
            bool stopped = false; var events = new CodexEvents(Guid.NewGuid().ToString(), 10, null, (_, _) => { }, () => stopped = true);
            events.Feed(invalid + "\n"); events.Finish(); Assert.IsTrue(stopped); Assert.IsNotNull(events.Failure);
        }
    }
    [TestMethod] public void ToolCallsDeduplicateAndBudgetStopsNewCall()
    {
        bool stopped = false; var events = new CodexEvents(null, 1, null, (_, _) => { }, () => stopped = true);
        events.Feed("{\"type\":\"item.started\",\"item\":{\"id\":\"1\",\"type\":\"command_execution\"}}\n");
        events.Feed("{\"type\":\"item.completed\",\"item\":{\"id\":\"1\",\"type\":\"command_execution\"}}\n");
        Assert.AreEqual(1, events.ToolCalls); Assert.IsFalse(stopped);
        events.Feed("{\"type\":\"item.started\",\"item\":{\"id\":\"2\",\"type\":\"command_execution\"}}\n");
        Assert.IsTrue(stopped); Assert.AreEqual("ToolCallBudgetExceeded", events.Failure);
    }
    [TestMethod] public void SecretRedactionCoversTypicalKeysAndHeaders()
    {
        string text = EventJournal.Redact("api_key=secret123 Authorization: Bearer very-secret sk-abcdefghijklmnopqrst");
        Assert.IsFalse(text.Contains("secret123")); Assert.IsFalse(text.Contains("very-secret")); Assert.IsFalse(text.Contains("sk-"));
    }
}
