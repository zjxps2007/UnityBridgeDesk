using System.Text.Json;

namespace UnityBridgeDesk.Infrastructure.Storage;

// This boundary allows tests to interrupt a write or replacement without modifying production files.
public interface IAtomicFileOperations
{
    Task WriteNewAndFlushAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    void Commit(string temporaryPath, string destinationPath, string? backupPath);
}

public sealed class AtomicFileOperations : IAtomicFileOperations
{
    public async Task WriteNewAndFlushAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        file.Flush(flushToDisk: true);
    }

    public void Commit(string temporaryPath, string destinationPath, string? backupPath)
    {
        if (File.Exists(destinationPath)) File.Replace(temporaryPath, destinationPath, backupPath);
        else File.Move(temporaryPath, destinationPath, overwrite: false);
    }
}

// One in-process queue and an OS file-share lock per document. Failures return codes, never raw exception text.
public sealed class AtomicJsonStore<T> where T : class
{
    private const int MaximumDocumentBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Action<T> validate;
    private readonly IAtomicFileOperations files;
    public string DocumentPath { get; }
    public string BackupPath => DocumentPath + ".bak";

    public AtomicJsonStore(string documentPath, Action<T> validate, IAtomicFileOperations? files = null)
    {
        if (!Path.IsPathFullyQualified(documentPath)) throw new ArgumentException("Storage path must be absolute.");
        DocumentPath = Path.GetFullPath(documentPath);
        this.validate = validate;
        this.files = files ?? new AtomicFileOperations();
    }

    public async Task<ReadResult<T>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<SaveResult> SaveAsync(T value, bool createOnly = false, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            byte[] bytes;
            try
            {
                ArgumentNullException.ThrowIfNull(value);
                validate(value);
                bytes = JsonSerializer.SerializeToUtf8Bytes(new VersionedDocument<T>(DeskJson.FormatVersion, value), DeskJson.Options);
                if (bytes.Length > MaximumDocumentBytes) return new(SaveStatus.InvalidData);
            }
            catch (Exception error) when (InvalidData(error)) { return new(SaveStatus.InvalidData); }

            Directory.CreateDirectory(Path.GetDirectoryName(DocumentPath)!);
            await using var writerLock = new FileStream(DocumentPath + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            if (createOnly && (File.Exists(DocumentPath) || File.Exists(BackupPath))) return new(SaveStatus.AlreadyExists);
            var prior = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (prior.Status == ReadStatus.UnsupportedVersion) return new(SaveStatus.UnsupportedVersion);
            if (prior.Status == ReadStatus.Unavailable) return new(SaveStatus.IoFailure);
            temporary = DocumentPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await files.WriteNewAndFlushAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            var candidate = await ReadOneAsync(temporary, cancellationToken).ConfigureAwait(false);
            if (candidate.Issue != StoreIssue.None) return new(SaveStatus.InvalidData);
            cancellationToken.ThrowIfCancellationRequested();
            // A corrupt primary must not overwrite the last known good backup.
            files.Commit(temporary, DocumentPath, prior.Status == ReadStatus.Current ? BackupPath : null);
            return new(SaveStatus.Saved);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(SaveStatus.IoFailure); }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Uncommitted temp remains recoverable. */ }
            }
            gate.Release();
        }
    }

    private async Task<ReadResult<T>> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        var primary = await ReadOneAsync(DocumentPath, cancellationToken).ConfigureAwait(false);
        if (primary.Issue == StoreIssue.None) return new(ReadStatus.Current, primary.Value, StoreIssue.None, StoreIssue.None);
        if (primary.Issue == StoreIssue.UnsupportedVersion)
            return new(ReadStatus.UnsupportedVersion, null, primary.Issue, StoreIssue.None);
        var backup = await ReadOneAsync(BackupPath, cancellationToken).ConfigureAwait(false);
        if (backup.Issue == StoreIssue.None) return new(ReadStatus.RecoveredBackup, backup.Value, primary.Issue, StoreIssue.None);
        var status = backup.Issue == StoreIssue.UnsupportedVersion ? ReadStatus.UnsupportedVersion :
            primary.Issue == StoreIssue.IoFailure || backup.Issue == StoreIssue.IoFailure ? ReadStatus.Unavailable :
            primary.Issue == StoreIssue.Missing && backup.Issue == StoreIssue.Missing ? ReadStatus.Missing : ReadStatus.Corrupt;
        return new(status, null, primary.Issue, backup.Issue);
    }

    private async Task<(T? Value, StoreIssue Issue)> ReadOneAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (stream.Length > MaximumDocumentBytes) return (null, StoreIssue.InvalidDocument);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("formatVersion", out var version) ||
                !version.TryGetInt32(out var number)) return (null, StoreIssue.InvalidDocument);
            if (number != DeskJson.FormatVersion) return (null, StoreIssue.UnsupportedVersion);
            // Duplicate members are ambiguous even when the serializer would take the last value.
            RejectDuplicateMembers(json.RootElement);
            var document = json.Deserialize<VersionedDocument<T>>(DeskJson.Options);
            if (document?.Data is null) return (null, StoreIssue.InvalidDocument);
            validate(document.Data);
            return (document.Data, StoreIssue.None);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return (null, StoreIssue.Missing); }
        catch (Exception error) when (InvalidData(error)) { return (null, StoreIssue.InvalidDocument); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return (null, StoreIssue.IoFailure); }
    }

    private static bool InvalidData(Exception error) => error is JsonException or ArgumentException or NullReferenceException or InvalidOperationException;

    private static void RejectDuplicateMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate document member.");
                RejectDuplicateMembers(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray()) RejectDuplicateMembers(element);
    }
}
