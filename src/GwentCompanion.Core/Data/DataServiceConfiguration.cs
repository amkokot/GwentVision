using System.Text.Json;

namespace GwentCompanion.Core.Data;

/// <summary>Public, non-secret connection information shipped with the application.</summary>
public sealed record DataServiceConfiguration(int Schema, bool Enabled, Guid ServiceId, string? ApiBaseUrl)
{
    public const int CurrentSchema = 1;
    public static DataServiceConfiguration Disabled => new(CurrentSchema, false, Guid.Empty, null);

    public Uri? Endpoint => Enabled && Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var uri) ? uri : null;

    public static DataServiceConfiguration Load(string path)
    {
        if (!File.Exists(path)) return Disabled;
        var value = JsonSerializer.Deserialize<DataServiceConfiguration>(File.ReadAllText(path));
        if (value is not { Schema: CurrentSchema })
            throw new InvalidDataException("The data-service configuration is unsupported or damaged.");
        if (!value.Enabled) return Disabled;
        if (value.ServiceId == Guid.Empty || value.Endpoint is not { Scheme: "https" } endpoint ||
            !endpoint.AbsolutePath.TrimEnd('/').EndsWith("/functions/v1/gw-api", StringComparison.Ordinal))
            throw new InvalidDataException("The enabled data-service configuration is invalid.");
        return value with { ApiBaseUrl = endpoint.AbsoluteUri.TrimEnd('/') };
    }
}

/// <summary>Private local synchronization state, separated by deployed service identity.</summary>
public sealed record DataContributionServerState(
    int Schema,
    Guid ServiceId,
    Guid? InstallationHandle,
    Dictionary<string, string> SeasonCodes,
    Dictionary<Guid, long> UploadedRevisions,
    string? LastReceiptSeason,
    Guid? LastReceiptId,
    DateTimeOffset? LastReceiptAtUtc)
{
    public const int CurrentSchema = 1;

    public static DataContributionServerState Empty(Guid serviceId) => new(CurrentSchema, serviceId, null,
        new(StringComparer.Ordinal), [], null, null, null);

    public static DataContributionServerState Load(string path, Guid serviceId)
    {
        if (serviceId == Guid.Empty || !File.Exists(path)) return Empty(serviceId);
        var value = JsonSerializer.Deserialize<DataContributionServerState>(File.ReadAllText(path));
        if (value is not { Schema: CurrentSchema } || value.ServiceId != serviceId ||
            value.InstallationHandle == Guid.Empty ||
            value.SeasonCodes is null || value.UploadedRevisions is null ||
            value.LastReceiptSeason is null != value.LastReceiptId is null ||
            value.LastReceiptId is null != value.LastReceiptAtUtc is null ||
            value.UploadedRevisions.Any(pair => pair.Key == Guid.Empty || pair.Value < 1) ||
            value.SeasonCodes.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
            return Empty(serviceId);
        return value with
        {
            SeasonCodes = new(value.SeasonCodes, StringComparer.Ordinal),
            UploadedRevisions = new(value.UploadedRevisions),
        };
    }

    public static void Save(string path, DataContributionServerState value)
    {
        if (value.Schema != CurrentSchema || value.ServiceId == Guid.Empty ||
            value.InstallationHandle == Guid.Empty || value.LastReceiptSeason is null != value.LastReceiptId is null ||
            value.LastReceiptId is null != value.LastReceiptAtUtc is null)
            throw new InvalidDataException("Invalid data-service synchronization state.");
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
