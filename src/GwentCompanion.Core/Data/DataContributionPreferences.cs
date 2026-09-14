using System.Text.Json;

namespace GwentCompanion.Core.Data;

public enum DataContributionConsent
{
    Undecided = 0,
    Accepted = 1,
    Declined = 2,
}

/// <summary>
/// Durable, fail-closed consent choices for match-data contribution. This file is
/// stored with the installation identity rather than in the replaceable app tree.
/// </summary>
public sealed record DataContributionPreferences(
    int Schema,
    int NoticeVersion,
    DataContributionConsent Consent,
    bool PublishAnonymousCurve,
    bool AutomaticMonthlyUpload,
    DateTimeOffset? DecidedAtUtc)
{
    public const int CurrentSchema = 1;
    public const int CurrentNoticeVersion = 1;

    public static DataContributionPreferences Initial => new(
        CurrentSchema,
        CurrentNoticeVersion,
        DataContributionConsent.Undecided,
        PublishAnonymousCurve: true,
        AutomaticMonthlyUpload: true,
        DecidedAtUtc: null);

    public bool CanShare => Consent == DataContributionConsent.Accepted;

    public DataContributionPreferences Decide(bool accepted, DateTimeOffset atUtc) => this with
    {
        NoticeVersion = CurrentNoticeVersion,
        Consent = accepted ? DataContributionConsent.Accepted : DataContributionConsent.Declined,
        AutomaticMonthlyUpload = accepted && AutomaticMonthlyUpload,
        DecidedAtUtc = atUtc.ToUniversalTime(),
    };

    public static DataContributionPreferences Load(string path)
    {
        if (!File.Exists(path)) return Initial;
        var value = JsonSerializer.Deserialize<DataContributionPreferences>(File.ReadAllText(path));
        if (value is not { Schema: CurrentSchema } ||
            !Enum.IsDefined(value.Consent) || value.NoticeVersion is < 1 or > CurrentNoticeVersion ||
            value.Consent == DataContributionConsent.Undecided && value.DecidedAtUtc is not null ||
            value.Consent != DataContributionConsent.Undecided && value.DecidedAtUtc is null)
            throw new InvalidDataException("Unknown or damaged data-contribution preferences; sharing remains off until they are repaired.");
        return value.NoticeVersion == CurrentNoticeVersion ? value : value with
        {
            NoticeVersion = CurrentNoticeVersion,
            Consent = DataContributionConsent.Undecided,
            AutomaticMonthlyUpload = false,
            DecidedAtUtc = null,
        };
    }

    public static void Save(string path, DataContributionPreferences value)
    {
        if (value.Schema != CurrentSchema || value.NoticeVersion != CurrentNoticeVersion || !Enum.IsDefined(value.Consent) ||
            value.Consent == DataContributionConsent.Undecided && value.DecidedAtUtc is not null ||
            value.Consent != DataContributionConsent.Undecided && value.DecidedAtUtc is null)
            throw new InvalidDataException("Invalid data-contribution preferences.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value, new JsonSerializerOptions { WriteIndented = true });
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class DataContributionSchedule
{
    /// <summary>The monthly upload is due on the first played match after the 18th, in the user's local calendar.</summary>
    public static bool IsDue(DataContributionPreferences preferences, DateOnly localGameDate, string? lastSuccessfulPeriod) =>
        preferences.CanShare && preferences.AutomaticMonthlyUpload && localGameDate.Day > 18 &&
        !string.Equals(Period(localGameDate), lastSuccessfulPeriod, StringComparison.Ordinal);

    public static string Period(DateOnly date) => date.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Local server receipts. Attempts never advance this state.</summary>
public sealed record DataContributionSyncState(
    int Schema,
    string? LastSuccessfulAutomaticPeriod,
    Guid? LastReceiptId,
    DateTimeOffset? LastReceiptAtUtc)
{
    public const int CurrentSchema = 1;
    public static DataContributionSyncState Empty => new(CurrentSchema, null, null, null);

    public DataContributionSyncState RecordSuccess(string period, Guid receiptId, DateTimeOffset receivedAtUtc)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(period, "^[0-9]{4}-(0[1-9]|1[0-2])$") || receiptId == Guid.Empty)
            throw new InvalidDataException("Invalid automatic-upload receipt.");
        return new(CurrentSchema, period, receiptId, receivedAtUtc.ToUniversalTime());
    }

    public static DataContributionSyncState Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        var value = JsonSerializer.Deserialize<DataContributionSyncState>(File.ReadAllText(path));
        if (value is not { Schema: CurrentSchema } ||
            value.LastSuccessfulAutomaticPeriod is null != value.LastReceiptId is null ||
            value.LastReceiptId is null != value.LastReceiptAtUtc is null ||
            value.LastSuccessfulAutomaticPeriod is { } period &&
                !System.Text.RegularExpressions.Regex.IsMatch(period, "^[0-9]{4}-(0[1-9]|1[0-2])$"))
            throw new InvalidDataException("Unknown or damaged data-contribution receipt state.");
        return value;
    }

    public static void Save(string path, DataContributionSyncState value)
    {
        if (value.Schema != CurrentSchema ||
            value.LastSuccessfulAutomaticPeriod is null != value.LastReceiptId is null ||
            value.LastReceiptId is null != value.LastReceiptAtUtc is null)
            throw new InvalidDataException("Invalid data-contribution receipt state.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value, new JsonSerializerOptions { WriteIndented = true });
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
