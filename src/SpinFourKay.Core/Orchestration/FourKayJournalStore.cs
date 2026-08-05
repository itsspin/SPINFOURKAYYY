using System.Text.Json;
using System.Text.Json.Serialization;
using SpinFourKay.Core.IO;

namespace SpinFourKay.Core.Orchestration;

public interface IFourKayJournalStore
{
    Task<FourKayPreparedState> SaveNewAsync(
        string stateDirectory,
        FourKayPreparedState state,
        CancellationToken cancellationToken = default);

    Task<FourKayPreparedState> UpdateAsync(
        FourKayPreparedState state,
        CancellationToken cancellationToken = default);

    Task<FourKayPreparedState> LoadAsync(
        string journalPath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FourKayPreparedState>> ListActiveAsync(
        string stateDirectory,
        CancellationToken cancellationToken = default);

    Task<FourKayPreparedState?> LoadLatestActiveAsync(
        string stateDirectory,
        CancellationToken cancellationToken = default);

    Task<FourKayPreparedState> MarkRestoredAsync(
        FourKayPreparedState state,
        string? recoveryBackupManifestPath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Persists crash/reboot recovery state independently of the application's UI.
/// </summary>
public sealed class FourKayJournalStore : IFourKayJournalStore
{
    public const string JournalFileName = "journal.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<FourKayPreparedState> SaveNewAsync(
        string stateDirectory,
        FourKayPreparedState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentNullException.ThrowIfNull(state);

        string root = Path.GetFullPath(stateDirectory);
        string sessionsRoot = Path.Combine(root, "sessions");
        string sessionDirectory = EnsureChildPath(
            sessionsRoot,
            Path.Combine(sessionsRoot, state.SessionId.ToString("N")));
        string journalPath = Path.Combine(sessionDirectory, JournalFileName);
        if (File.Exists(journalPath))
        {
            throw new IOException($"Recovery journal '{journalPath}' already exists.");
        }

        Directory.CreateDirectory(sessionDirectory);
        FourKayPreparedState located = state with { JournalPath = journalPath };
        await WriteAsync(located, cancellationToken).ConfigureAwait(false);
        return located;
    }

    public async Task<FourKayPreparedState> UpdateAsync(
        FourKayPreparedState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(state.JournalPath))
        {
            throw new ArgumentException(
                "The recovery state has no journal path.",
                nameof(state));
        }

        if (!File.Exists(state.JournalPath))
        {
            throw new FileNotFoundException(
                "The recovery journal cannot be updated because it is missing.",
                state.JournalPath);
        }

        FourKayPreparedState persisted = await LoadAsync(
            state.JournalPath,
            cancellationToken).ConfigureAwait(false);
        if (persisted.UiCompatibilityMode != state.UiCompatibilityMode)
        {
            throw new InvalidOperationException(
                "The recovery journal UI compatibility mode is immutable. Restore "
                    + "the existing session before choosing a different UI mode.");
        }

        if (persisted.KeepPreparedConfiguration
            != state.KeepPreparedConfiguration)
        {
            throw new InvalidOperationException(
                "The recovery journal's prepared-configuration policy is immutable.");
        }

        await WriteAsync(state, cancellationToken).ConfigureAwait(false);
        return state;
    }

    public async Task<FourKayPreparedState> LoadAsync(
        string journalPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        string fullPath = Path.GetFullPath(journalPath);

        await using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        FourKayPreparedState state =
            await JsonSerializer.DeserializeAsync<FourKayPreparedState>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The recovery journal is empty.");

        if (state.FormatVersion != FourKayPreparedState.CurrentFormatVersion)
        {
            throw new NotSupportedException(
                $"Recovery journal version {state.FormatVersion} is not supported.");
        }

        return state with { JournalPath = fullPath };
    }

    public async Task<IReadOnlyList<FourKayPreparedState>> ListActiveAsync(
        string stateDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        string sessionsRoot = Path.Combine(Path.GetFullPath(stateDirectory), "sessions");
        if (!Directory.Exists(sessionsRoot))
        {
            return [];
        }

        List<FourKayPreparedState> states = [];
        foreach (string sessionDirectory in Directory.EnumerateDirectories(sessionsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string journalPath = Path.Combine(sessionDirectory, JournalFileName);
            if (!File.Exists(journalPath))
            {
                continue;
            }

            FourKayPreparedState state = await LoadAsync(journalPath, cancellationToken)
                .ConfigureAwait(false);
            if (state.Status is FourKayJournalStatus.Preparing or FourKayJournalStatus.Prepared)
            {
                states.Add(state);
            }
        }

        return states
            .OrderByDescending(state => state.PreparedAtUtc)
            .ToArray();
    }

    public async Task<FourKayPreparedState?> LoadLatestActiveAsync(
        string stateDirectory,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FourKayPreparedState> states = await ListActiveAsync(
            stateDirectory,
            cancellationToken).ConfigureAwait(false);
        return states.Count > 0 ? states[0] : null;
    }

    public async Task<FourKayPreparedState> MarkRestoredAsync(
        FourKayPreparedState state,
        string? recoveryBackupManifestPath,
        CancellationToken cancellationToken = default)
    {
        FourKayPreparedState restored = state with
        {
            Status = FourKayJournalStatus.Restored,
            RestoredAtUtc = DateTimeOffset.UtcNow,
            RecoveryBackupManifestPath = recoveryBackupManifestPath,
        };
        return await UpdateAsync(restored, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAsync(
        FourKayPreparedState state,
        CancellationToken cancellationToken)
    {
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        await AtomicFile.WriteAllBytesAsync(state.JournalPath, content, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string EnsureChildPath(string root, string candidate)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.StartsWith(
            fullRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A journal path escaped its managed directory.");
        }

        return fullCandidate;
    }
}
