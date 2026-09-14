using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GwentCompanion.Core.Data;

public sealed record DataContributionMatchEnvelope(string Format, string RecordBase64);

public sealed record DataContributionUploadPayload(
    int Schema,
    Guid InstallationHandle,
    Guid BatchId,
    string Season,
    string ProducerVersion,
    int ConsentNoticeVersion,
    bool PublishAnonymousCurve,
    DataContributionMatchEnvelope[] Matches);

/// <summary>Deterministic API-v1 transport shared by manual and scheduled uploads.</summary>
public static partial class DataContributionTransport
{
    public const int ApiVersion = 1;
    public const string MatchFormat = "json-v1";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] SerializeMatch(CompactMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (match.InstallationId == Guid.Empty || match.MatchId == Guid.Empty || match.Revision < 1)
            throw new InvalidDataException("Match transport identity is incomplete.");
        return JsonSerializer.SerializeToUtf8Bytes(match, Json);
    }

    public static byte[] SerializeUploadPayload(Guid installationHandle, Guid batchId, string season,
        string producerVersion, int consentNoticeVersion, bool publishAnonymousCurve,
        IEnumerable<CompactMatch> matches)
    {
        if (installationHandle == Guid.Empty || batchId == Guid.Empty)
            throw new InvalidDataException("Upload transport identity is incomplete.");
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(producerVersion);
        if (!SeasonSlug().IsMatch(season) || producerVersion.Length is < 1 or > 80 ||
            consentNoticeVersion < 1)
            throw new InvalidDataException("Upload transport metadata is invalid.");
        var records = matches?.ToArray() ?? throw new ArgumentNullException(nameof(matches));
        if (records.Length is < 1 or > 250)
            throw new InvalidDataException("An upload batch must contain between 1 and 250 matches.");
        if (records.Select(match => match.InstallationId).Distinct().Count() != 1)
            throw new InvalidDataException("An upload batch cannot mix local installation identities.");
        var envelopes = records.Select(match => new DataContributionMatchEnvelope(
            MatchFormat, Convert.ToBase64String(SerializeMatch(match)))).ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(new DataContributionUploadPayload(ApiVersion,
            installationHandle, batchId, season, producerVersion, consentNoticeVersion,
            publishAnonymousCurve, envelopes), Json);
    }

    public static string CanonicalDeviceRequest(string operation, Guid installationHandle, Guid requestId,
        string? season, ReadOnlySpan<byte> payload, DateTimeOffset timestamp)
    {
        if (operation is not ("upload" or "code-rotate" or "curve-visibility" or "data-delete") ||
            installationHandle == Guid.Empty || requestId == Guid.Empty)
            throw new InvalidDataException("Signed request metadata is invalid.");
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var instant = timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
        return $"GWENTVISION\n{ApiVersion}\n{operation}\n{installationHandle:D}\n{requestId:D}\n{season ?? string.Empty}\n{digest}\n{instant}";
    }

    public static string CanonicalRegistration(Guid challengeId, string nonceBase64Url,
        ReadOnlySpan<byte> publicKeySpki)
    {
        if (challengeId == Guid.Empty || string.IsNullOrWhiteSpace(nonceBase64Url))
            throw new InvalidDataException("Registration challenge is incomplete.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(publicKeySpki)).ToLowerInvariant();
        return $"GWENTVISION\n{ApiVersion}\nregister\n{challengeId:D}\n{nonceBase64Url}\n{fingerprint}";
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonSlug();
}
