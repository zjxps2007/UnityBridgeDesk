namespace UnityBridgeDesk.Core.Models;

public readonly record struct ProjectId(Guid Value) { public static ProjectId New() => new(Guid.NewGuid()); }
public readonly record struct ReleaseId(Guid Value) { public static ReleaseId New() => new(Guid.NewGuid()); }
public readonly record struct AiProfileId(Guid Value) { public static AiProfileId New() => new(Guid.NewGuid()); }
public readonly record struct CredentialId(Guid Value) { public static CredentialId New() => new(Guid.NewGuid()); }
public readonly record struct RunId(Guid Value) { public static RunId New() => new(Guid.NewGuid()); }
public readonly record struct ExecutionId(Guid Value) { public static ExecutionId New() => new(Guid.NewGuid()); }
public readonly record struct TrialId(Guid Value) { public static TrialId New() => new(Guid.NewGuid()); }

public enum ToolKind { Unknown, Installation, AiWork, Benchmark }
[Flags]
public enum BenchmarkModes { None = 0, FixedCommands = 1, AiCreation = 2 }
public enum ExecutionMode { Unknown, Installation, AiWork, FixedCommands, AiCreation }

public static class ContractGuard
{
    public static void Id(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("An identity must not be empty.");
    }

    public static void Text(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A required value is missing.");
    }

    public static void Known<T>(T value) where T : struct, Enum
    {
        if (!Enum.IsDefined(value) || Convert.ToInt32(value) == 0)
            throw new ArgumentException("An enum value is unknown or unsupported.");
    }
}
