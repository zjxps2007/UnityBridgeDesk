using UnityBridgeDesk.Infrastructure.Storage;

namespace UnityBridgeDesk.Desktop.Shell;

public sealed record ShellLoadResult(ReadResult<ShellPreferences> Preferences, ReadResult<DeskDrafts> Drafts)
{
    public ShellSession CreateSession() => new(Preferences.Value, Drafts.Value);
}

public sealed class ShellPersistence
{
    private readonly AtomicJsonStore<ShellPreferences> preferences;
    private readonly AtomicJsonStore<DeskDrafts> drafts;
    public bool CanSavePreferences { get; private set; }
    public bool CanSaveDrafts { get; private set; }
    private bool futurePreferences;
    private bool futureDrafts;

    public ShellPersistence(string dataRoot)
    {
        if (!Path.IsPathFullyQualified(dataRoot)) throw new ArgumentException("Data directory must be absolute.");
        preferences = new(Path.Combine(dataRoot, "settings", "shell.json"), x => x.Validate());
        drafts = new(Path.Combine(dataRoot, "drafts", "desk.json"), x => x.Validate());
    }
    public async Task<ShellLoadResult> LoadAsync()
    {
        var settings = await preferences.LoadAsync().ConfigureAwait(false);
        var inputs = await drafts.LoadAsync().ConfigureAwait(false);
        CanSavePreferences = Writable(settings.Status); CanSaveDrafts = Writable(inputs.Status);
        futurePreferences = settings.Status == ReadStatus.UnsupportedVersion;
        futureDrafts = inputs.Status == ReadStatus.UnsupportedVersion;
        return new(settings, inputs);
    }
    // Called only by the user's explicit recovery action, never by automatic load/default creation.
    public void AllowReset()
    {
        if (!futurePreferences) CanSavePreferences = true;
        if (!futureDrafts) CanSaveDrafts = true;
    }
    public async Task<(SaveStatus Preferences, SaveStatus Drafts)> SaveAsync(ShellSession session)
    {
        var settings = session.Preferences;
        var inputs = session.Drafts;
        var a = CanSavePreferences ? (await preferences.SaveAsync(settings).ConfigureAwait(false)).Status : SaveStatus.UnsupportedVersion;
        var b = CanSaveDrafts ? (await drafts.SaveAsync(inputs).ConfigureAwait(false)).Status : SaveStatus.UnsupportedVersion;
        return (a, b);
    }
    private static bool Writable(ReadStatus status) => status is ReadStatus.Current or ReadStatus.Missing or ReadStatus.RecoveredBackup;
}
