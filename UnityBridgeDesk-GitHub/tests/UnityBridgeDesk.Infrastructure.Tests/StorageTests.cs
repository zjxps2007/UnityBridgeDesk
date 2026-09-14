using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Storage;
using UnityBridgeDesk.Tests;

[assembly: Parallelize(Workers = 4, Scope = ExecutionScope.MethodLevel)]

namespace UnityBridgeDesk.Infrastructure.Tests;

public enum InjectedFault { None, PartialWrite, CorruptCandidate, BeforeCommit, AfterCommit }
internal sealed class FaultingFiles : IAtomicFileOperations
{
    private readonly AtomicFileOperations real = new();
    internal InjectedFault Fault { get; set; }
    public async Task WriteNewAndFlushAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (Fault is InjectedFault.PartialWrite or InjectedFault.CorruptCandidate)
        {
            await File.WriteAllTextAsync(path, "{\"partial\":", cancellationToken);
            if (Fault == InjectedFault.PartialWrite) throw new IOException("injected test failure; test-secret-must-not-escape");
            return;
        }
        await real.WriteNewAndFlushAsync(path, bytes, cancellationToken);
    }
    public void Commit(string temporaryPath, string destinationPath, string? backupPath)
    {
        if (Fault == InjectedFault.BeforeCommit) throw new IOException("injected test failure; test-secret-must-not-escape");
        real.Commit(temporaryPath, destinationPath, backupPath);
        if (Fault == InjectedFault.AfterCommit) throw new IOException("injected uncertain commit result");
    }
}

[TestClass]
public sealed class StorageTests
{
    private static AtomicJsonStore<DeskSettings> Store(IAtomicFileOperations? files = null) =>
        new(Path.Combine(SampleData.TestDirectory(), "settings.json"), x => x.Validate(), files);
    private static DeskSettings Settings(string palette = "pastel")
    {
        var project = SampleData.Project();
        return new(project.Id, palette, [project], [SampleData.Release()], [SampleData.Profile()]);
    }

    [TestMethod]
    public async Task ImmutableSettingsRoundTripWithUnknownValuesAndCredentialReference()
    {
        var store = Store();
        var original = Settings();
        Assert.AreEqual(SaveStatus.Saved, (await store.SaveAsync(original)).Status);
        var result = await store.LoadAsync();
        Assert.AreEqual(ReadStatus.Current, result.Status);
        Assert.AreEqual(original.SelectedProject, result.Value!.SelectedProject);
        Assert.AreEqual(original.AiProfiles[0].Credential, result.Value.AiProfiles[0].Credential);
        Assert.IsNull(result.Value.AiProfiles[0].Model);
        Assert.IsNull(result.Value.Releases[0].CliSha256);
        var json = await File.ReadAllTextAsync(store.DocumentPath);
        StringAssert.Contains(json, "\"formatVersion\": 1");
        Assert.IsFalse(json.Contains("apiKey", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("accessToken", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [DataRow(InjectedFault.PartialWrite, SaveStatus.IoFailure)]
    [DataRow(InjectedFault.CorruptCandidate, SaveStatus.InvalidData)]
    [DataRow(InjectedFault.BeforeCommit, SaveStatus.IoFailure)]
    public async Task FailedWritePreservesExactPreviousSettings(InjectedFault fault, SaveStatus expected)
    {
        var files = new FaultingFiles();
        var store = Store(files);
        await store.SaveAsync(Settings());
        var before = await File.ReadAllBytesAsync(store.DocumentPath);
        files.Fault = fault;
        var result = await store.SaveAsync(Settings("mint"));
        Assert.AreEqual(expected, result.Status);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(store.DocumentPath));
        Assert.AreEqual(ReadStatus.Current, (await store.LoadAsync()).Status);
        Assert.IsFalse(JsonSerializer.Serialize(result).Contains("test-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task FailureReportedAfterReplacementStillLeavesRecoverableDocuments()
    {
        var files = new FaultingFiles();
        var store = Store(files);
        await store.SaveAsync(Settings("lavender"));
        files.Fault = InjectedFault.AfterCommit;
        Assert.AreEqual(SaveStatus.IoFailure, (await store.SaveAsync(Settings("mint"))).Status);
        Assert.AreEqual("mint", (await store.LoadAsync()).Value!.Palette);
        await File.WriteAllTextAsync(store.DocumentPath, "corrupt");
        var recovered = await store.LoadAsync();
        Assert.AreEqual(ReadStatus.RecoveredBackup, recovered.Status);
        Assert.AreEqual("lavender", recovered.Value!.Palette);
    }

    [TestMethod]
    public async Task CorruptPrimaryRecoversBackupWithoutAutoRewritingIt()
    {
        var store = Store();
        await store.SaveAsync(Settings("lavender"));
        await store.SaveAsync(Settings("mint"));
        await File.WriteAllTextAsync(store.DocumentPath, "{truncated");
        var backupBefore = await File.ReadAllBytesAsync(store.BackupPath);
        var recovered = await store.LoadAsync();
        Assert.AreEqual(ReadStatus.RecoveredBackup, recovered.Status);
        Assert.AreEqual("lavender", recovered.Value!.Palette);
        Assert.AreEqual("{truncated", await File.ReadAllTextAsync(store.DocumentPath));
        Assert.AreEqual(SaveStatus.Saved, (await store.SaveAsync(Settings("peach"))).Status);
        CollectionAssert.AreEqual(backupBefore, await File.ReadAllBytesAsync(store.BackupPath));
    }

    [TestMethod]
    public async Task MissingAndCorruptSettingsAreDifferentAndNeverSilentlyBecomeDefaults()
    {
        var store = Store();
        var missing = await store.LoadAsync();
        Assert.AreEqual(ReadStatus.Missing, missing.Status);
        Assert.IsNull(missing.Value);
        await File.WriteAllTextAsync(store.DocumentPath, "broken");
        var corrupt = await store.LoadAsync();
        Assert.AreEqual(ReadStatus.Corrupt, corrupt.Status);
        Assert.IsNull(corrupt.Value);
    }

    [TestMethod]
    public async Task FutureFormatBlocksFallbackAndOverwrite()
    {
        var store = Store();
        await store.SaveAsync(Settings());
        await store.SaveAsync(Settings("mint"));
        var future = "{\"formatVersion\":99,\"data\":{\"future\":true}}";
        await File.WriteAllTextAsync(store.DocumentPath, future);
        var result = await store.LoadAsync();
        Assert.AreEqual(ReadStatus.UnsupportedVersion, result.Status);
        Assert.IsNull(result.Value);
        Assert.AreEqual(SaveStatus.UnsupportedVersion, (await store.SaveAsync(Settings())).Status);
        Assert.AreEqual(future, await File.ReadAllTextAsync(store.DocumentPath));
    }

    [TestMethod]
    [DataRow("secret")]
    [DataRow("missing")]
    [DataRow("null")]
    [DataRow("duplicate")]
    [DataRow("wrong-version-type")]
    public async Task UnexpectedSecretFieldsAndMalformedSchemasAreRejected(string mutation)
    {
        var store = Store();
        await store.SaveAsync(Settings());
        var node = JsonNode.Parse(await File.ReadAllTextAsync(store.DocumentPath))!;
        var data = node["data"]!.AsObject();
        switch (mutation)
        {
            case "secret": data["aiProfiles"]![0]!["apiKey"] = "synthetic-test-key"; break;
            case "missing": data.Remove("palette"); break;
            case "null": data["projects"] = null; break;
            case "wrong-version-type": node["formatVersion"] = "1"; break;
        }
        var text = node.ToJsonString();
        if (mutation == "duplicate") text = text.Replace("\"formatVersion\":1", "\"formatVersion\":1,\"formatVersion\":1", StringComparison.Ordinal);
        await File.WriteAllTextAsync(store.DocumentPath, text);
        var result = await store.LoadAsync();
        Assert.AreEqual(ReadStatus.Corrupt, result.Status);
        Assert.IsNull(result.Value);
        Assert.IsFalse(JsonSerializer.Serialize(result).Contains("synthetic-test-key", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExclusiveWriterFailureLeavesPreviousSettingsUnchanged()
    {
        var store = Store();
        await store.SaveAsync(Settings());
        var before = await File.ReadAllBytesAsync(store.DocumentPath);
        using var lockFile = new FileStream(store.DocumentPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.AreEqual(SaveStatus.IoFailure, (await store.SaveAsync(Settings("mint"))).Status);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(store.DocumentPath));
    }

    [TestMethod]
    public async Task PreCancelledSaveDoesNotTouchExistingSettings()
    {
        var store = Store();
        await store.SaveAsync(Settings());
        var before = await File.ReadAllBytesAsync(store.DocumentPath);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.SaveAsync(Settings("mint"), cancellationToken: cancellation.Token));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(store.DocumentPath));
    }

    [TestMethod]
    public async Task ConcurrentSavesLeaveOneWholeValidDocumentAndBackup()
    {
        var store = Store();
        await store.SaveAsync(Settings());
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => store.SaveAsync(Settings("palette-" + i))));
        Assert.IsTrue(results.All(x => x.Status == SaveStatus.Saved));
        var final = await store.LoadAsync();
        Assert.AreEqual(ReadStatus.Current, final.Status);
        StringAssert.StartsWith(final.Value!.Palette, "palette-");
        await File.WriteAllTextAsync(store.DocumentPath, "broken");
        Assert.AreEqual(ReadStatus.RecoveredBackup, (await store.LoadAsync()).Status);
    }
}
