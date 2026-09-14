using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Core.Tests;

[TestClass]
public sealed class ResourcesTests
{
    [TestMethod]
    public void ExternalUnityAndOutputArtifactsAreExcludedFromTemporaryCleanup()
    {
        var route = SampleData.Route();
        var root = Path.GetFullPath("managed-execution");
        var resources = new ExecutionResources(route, root);
        resources.RegisterProcess(new(route, 100, DateTimeOffset.UtcNow, ResourceOwnership.JobOwned));
        resources.RegisterProcess(new(route, 101, DateTimeOffset.UtcNow, ResourceOwnership.External));
        resources.RegisterPath(new(route, Path.Combine(root, "scratch"), PathPurpose.Temporary, ResourceOwnership.JobOwned));
        resources.RegisterPath(new(route, Path.Combine(root, "result"), PathPurpose.Artifact, ResourceOwnership.JobOwned));
        resources.RegisterPath(new(route, Path.GetFullPath("user-project"), PathPurpose.Output, ResourceOwnership.External));
        var candidates = resources.GetCleanupCandidates();
        Assert.AreEqual(100, candidates.Processes.Single().ProcessId);
        Assert.AreEqual(Path.Combine(root, "scratch"), candidates.TemporaryPaths.Single().AbsolutePath);
    }

    [TestMethod]
    public void ForeignOwnershipTraversalAndRootCleanupAreRejected()
    {
        var route = SampleData.Route();
        var root = Path.GetFullPath("managed-execution");
        var resources = new ExecutionResources(route, root);
        Assert.Throws<ArgumentException>(() => resources.RegisterProcess(new(SampleData.Route(), 100, DateTimeOffset.UtcNow, ResourceOwnership.JobOwned)));
        foreach (var path in new[] { root, Path.Combine(root, "..", "user-project"), root + "-sibling" })
            Assert.Throws<ArgumentException>(() => resources.RegisterPath(new(route, path, PathPurpose.Temporary, ResourceOwnership.JobOwned)));
        Assert.IsEmpty(resources.GetCleanupCandidates().Processes);
        Assert.IsEmpty(resources.GetCleanupCandidates().TemporaryPaths);
    }

    [TestMethod]
    public void PidWithoutStartIdentityAndOwnershipPromotionAreRejected()
    {
        var route = SampleData.Route();
        var resources = new ExecutionResources(route, Path.GetFullPath("managed-execution"));
        Assert.Throws<ArgumentException>(() => resources.RegisterProcess(new(route, 100, default, ResourceOwnership.JobOwned)));
        var external = new ProcessRegistration(route, 100, DateTimeOffset.UtcNow, ResourceOwnership.External);
        resources.RegisterProcess(external);
        Assert.Throws<InvalidOperationException>(() => resources.RegisterProcess(external with { Ownership = ResourceOwnership.JobOwned }));
    }
}
