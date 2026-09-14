using System.Collections.Immutable;

namespace UnityBridgeDesk.Core.Models;

public sealed record ProjectRef(ProjectId Id, string DisplayName, string RootPath, string? UnityBuild)
{
    public void Validate()
    {
        ContractGuard.Id(Id.Value);
        ContractGuard.Text(DisplayName);
        if (!Path.IsPathFullyQualified(RootPath)) throw new ArgumentException("Project path must be absolute.");
    }
}

// Labels are display metadata; nullable observations never assert a verified version/hash.
public sealed record BridgeReleaseRef(ReleaseId Id, string Label, string? CliPath,
    string? CliSha256, string? ObservedCliVersion, string? ConnectorSource,
    string? ConnectorSha256, string? ObservedConnectorVersion)
{
    // Optional v1 extension: older documents leave the axis unset.
    public ComparisonAxis ComparisonAxis { get; init; }
    public string? ConnectorHashScheme { get; init; }
    public string? ConnectorDeclaredSource { get; init; }
    public string? ConnectorGitHead { get; init; }
    public void Validate()
    {
        ContractGuard.Id(Id.Value);
        ContractGuard.Text(Label);
        if (!Enum.IsDefined(ComparisonAxis)) throw new ArgumentException("Unknown comparison axis.");
        if (ConnectorHashScheme is not null and not "connector-tree-v1") throw new ArgumentException("Unknown connector hash scheme.");
        if (CliPath is not null && !Path.IsPathFullyQualified(CliPath))
            throw new ArgumentException("CLI path must be absolute.");
        ValidateHash(CliSha256);
        ValidateHash(ConnectorSha256);
    }

    internal static void ValidateHash(string? hash)
    {
        if (hash is not null && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
            throw new ArgumentException("A SHA-256 digest must contain 64 hexadecimal characters.");
    }
}

// CredentialId points to a future credential store. No credential material belongs here.
public sealed record AiProfileRef(AiProfileId Id, string Provider, string? Model,
    string? ReasoningEffort, CredentialId? Credential)
{
    public void Validate()
    {
        ContractGuard.Id(Id.Value);
        ContractGuard.Text(Provider);
        if (Credential is { } credential) ContractGuard.Id(credential.Value);
    }
}

public sealed record ExecutionBudget(TimeSpan? Timeout, int? MaxAttempts,
    long? MaxTokens, int? MaxCalls)
{
    public void Validate()
    {
        if (Timeout <= TimeSpan.Zero || MaxAttempts <= 0 || MaxTokens <= 0 || MaxCalls <= 0)
            throw new ArgumentException("Configured budgets must be positive; unset budgets remain null.");
    }
}

public sealed record DeskSettings(ProjectId? SelectedProject, string Palette,
    ImmutableArray<ProjectRef> Projects, ImmutableArray<BridgeReleaseRef> Releases,
    ImmutableArray<AiProfileRef> AiProfiles)
{
    public static DeskSettings Empty => new(null, "pastel", [], [], []);

    public void Validate()
    {
        ContractGuard.Text(Palette);
        if (Projects.IsDefault || Releases.IsDefault || AiProfiles.IsDefault)
            throw new ArgumentException("Catalog collections must be present.");
        foreach (var project in Projects) project.Validate();
        foreach (var release in Releases) release.Validate();
        foreach (var profile in AiProfiles) profile.Validate();
        if (Projects.Select(x => x.Id).Distinct().Count() != Projects.Length ||
            Releases.Select(x => x.Id).Distinct().Count() != Releases.Length ||
            AiProfiles.Select(x => x.Id).Distinct().Count() != AiProfiles.Length)
            throw new ArgumentException("Catalog identities must be unique.");
        if (SelectedProject is { } selected && !Projects.Any(x => x.Id == selected))
            throw new ArgumentException("Selected project must exist in the catalog.");
    }
}
