using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityBridgeDesk.Infrastructure.Storage;

public static class DeskJson
{
    public const int FormatVersion = 1;
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            MaxDepth = 48
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

public sealed record VersionedDocument<T>(int FormatVersion, T Data);
public enum ReadStatus { Current, RecoveredBackup, Missing, Corrupt, UnsupportedVersion, Unavailable }
public enum StoreIssue { None, Missing, InvalidDocument, UnsupportedVersion, IoFailure }
public enum SaveStatus { Saved, AlreadyExists, InvalidData, UnsupportedVersion, IoFailure }
public sealed record ReadResult<T>(ReadStatus Status, T? Value, StoreIssue PrimaryIssue, StoreIssue BackupIssue);
public sealed record SaveResult(SaveStatus Status);
