using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Platform.Windows.Security;

namespace GwentCompanion.Platform.Windows.Data;

public sealed record DataContributionUploadResult(string Season, int Uploaded, int Unchanged, int Skipped,
    Guid? ReceiptId, DateTimeOffset? ReceiptAtUtc, string? SeasonCode);

/// <summary>One-click client for the public, device-signed contribution API.</summary>
public sealed class DataContributionClient : IDisposable
{
    private const int MaxResponseBytes = 1_048_576;
    private readonly DataServiceConfiguration _configuration;
    private readonly InstallationSigningIdentity _signingIdentity;
    private readonly string _statePath;
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _utcNow;
    private DataContributionServerState _state;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DataContributionClient(DataServiceConfiguration configuration, InstallationSigningIdentity signingIdentity,
        string statePath, HttpMessageHandler? handler = null, Func<DateTimeOffset>? utcNow = null)
    {
        if (!configuration.Enabled || configuration.Endpoint is null)
            throw new InvalidOperationException("The data service is not enabled in this build.");
        _configuration = configuration;
        _signingIdentity = signingIdentity ?? throw new ArgumentNullException(nameof(signingIdentity));
        _statePath = statePath;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _state = DataContributionServerState.Load(statePath, configuration.ServiceId);
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(configuration.Endpoint.AbsoluteUri.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GwentVision/0.3");
    }

    public async Task<DataContributionUploadResult> UploadCurrentSeasonAsync(string matchDirectory,
        Guid localInstallationId, string producerVersion, int consentNoticeVersion,
        bool publishAnonymousCurve, CancellationToken cancellationToken = default)
    {
        var skipped = 0;
        var candidates = new List<(CompactMatch Match, int PatchIndex)>();
        if (Directory.Exists(matchDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(matchDirectory, "*.gvm", SearchOption.AllDirectories))
            {
                try
                {
                    var record = LocalMatchStore.Read(path);
                    if (record.InstallationId != localInstallationId || !record.CaptureStopped) continue;
                    if (!TryMonthlyPatch(record, out var patchIndex)) { skipped++; continue; }
                    candidates.Add((record, patchIndex));
                }
                catch (Exception) when (File.Exists(path)) { skipped++; }
            }
        }

        if (candidates.Count == 0)
        {
            var last = _state.LastReceiptSeason ?? "current patch";
            return new(last, 0, 0, skipped, null, null, _state.SeasonCodes.GetValueOrDefault(last));
        }

        var currentPatchIndex = candidates.Max(candidate => candidate.PatchIndex);
        var season = candidates.First(candidate => candidate.PatchIndex == currentPatchIndex).Match.Patch;
        var records = candidates.Where(candidate => candidate.PatchIndex == currentPatchIndex)
            .Select(candidate => candidate.Match)
            .Where(record => !_state.UploadedRevisions.TryGetValue(record.MatchId, out var revision) || record.Revision > revision)
            .ToList();

        var handle = await EnsureRegisteredAsync(cancellationToken).ConfigureAwait(false);
        var accepted = 0; var unchanged = 0;
        Guid? receipt = null; DateTimeOffset? received = null;
        foreach (var batch in records.OrderBy(record => record.GameDateUtc).ThenBy(record => record.MatchId).Chunk(250))
        {
            var batchId = Guid.NewGuid();
            var payload = DataContributionTransport.SerializeUploadPayload(handle, batchId, season,
                producerVersion, consentNoticeVersion, publishAnonymousCurve, batch);
            var timestamp = _utcNow();
            var canonical = DataContributionTransport.CanonicalDeviceRequest("upload", handle, batchId,
                season, payload, timestamp);
            var reply = await PostSignedAsync<UploadReply>("upload", payload,
                _signingIdentity.Sign(Encoding.UTF8.GetBytes(canonical)), timestamp, cancellationToken).ConfigureAwait(false);
            if (reply.Schema != DataContributionTransport.ApiVersion || reply.ReceiptId == Guid.Empty)
                throw new InvalidDataException("The upload service returned an invalid receipt.");
            accepted += reply.Accepted; unchanged += reply.Unchanged;
            receipt = reply.ReceiptId; received = _utcNow();
            foreach (var record in batch) _state.UploadedRevisions[record.MatchId] = record.Revision;
            if (!string.IsNullOrWhiteSpace(reply.SeasonCode)) _state.SeasonCodes[season] = reply.SeasonCode;
            _state = _state with { LastReceiptSeason = season, LastReceiptId = receipt, LastReceiptAtUtc = received };
            SaveState();
        }

        if (!_state.SeasonCodes.ContainsKey(season) && receipt is not null)
        {
            var rotated = await SignedSeasonOperationAsync<CodeReply>("code/rotate", "code-rotate",
                handle, season, publishAnonymousCurve: null, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(rotated.SeasonCode))
                throw new InvalidDataException("The upload service did not issue a season code.");
            _state.SeasonCodes[season] = rotated.SeasonCode;
            SaveState();
        }

        if (_state.SeasonCodes.ContainsKey(season))
            await SignedSeasonOperationAsync<VisibilityReply>("curve/visibility", "curve-visibility",
                handle, season, publishAnonymousCurve, cancellationToken).ConfigureAwait(false);

        var savedReceipt = string.Equals(_state.LastReceiptSeason, season, StringComparison.Ordinal)
            ? _state.LastReceiptId : null;
        var savedReceiptAt = savedReceipt is null ? null : _state.LastReceiptAtUtc;
        return new(season, accepted, unchanged, skipped, receipt ?? savedReceipt,
            received ?? savedReceiptAt, _state.SeasonCodes.GetValueOrDefault(season));
    }

    public async Task<bool> UpdateCurrentSeasonVisibilityAsync(bool publishAnonymousCurve,
        CancellationToken cancellationToken = default)
    {
        var season = _state.LastReceiptSeason;
        if (_state.InstallationHandle is not { } handle || string.IsNullOrWhiteSpace(season) ||
            !_state.SeasonCodes.ContainsKey(season)) return false;
        await SignedSeasonOperationAsync<VisibilityReply>("curve/visibility", "curve-visibility",
            handle, season, publishAnonymousCurve, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static bool TryMonthlyPatch(CompactMatch match, out int index)
    {
        index = -1;
        if (!DeckPatchMetadata.TrySeasonWindow(match.Patch, out var startsAt, out var endsAt)) return false;
        if (match.StartedAtUtc is { } playedAt)
        {
            if (playedAt < startsAt || playedAt >= endsAt) return false;
        }
        else
        {
            var firstUtcDate = DateOnly.FromDateTime(startsAt.UtcDateTime);
            var lastUtcDate = DateOnly.FromDateTime(endsAt.UtcDateTime);
            if (match.GameDateUtc < firstUtcDate || match.GameDateUtc > lastUtcDate) return false;
        }
        var parts = match.Patch.Split('.');
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var month)) return false;
        index = (major - 12) * 12 + month - 1;
        return true;
    }

    private async Task<Guid> EnsureRegisteredAsync(CancellationToken token)
    {
        if (_state.InstallationHandle is { } existing) return existing;
        var challenge = await PostJsonAsync<ChallengeReply>("register/challenge", new { }, token).ConfigureAwait(false);
        if (challenge.Schema != DataContributionTransport.ApiVersion || challenge.ChallengeId == Guid.Empty ||
            string.IsNullOrWhiteSpace(challenge.Nonce) || challenge.ExpiresAt <= _utcNow())
            throw new InvalidDataException("The registration service returned an invalid challenge.");
        var canonical = DataContributionTransport.CanonicalRegistration(challenge.ChallengeId, challenge.Nonce,
            _signingIdentity.PublicKeySpki);
        var reply = await PostJsonAsync<RegistrationReply>("register", new
        {
            challengeId = challenge.ChallengeId,
            nonce = challenge.Nonce,
            publicKeySpki = Convert.ToBase64String(_signingIdentity.PublicKeySpki),
            signature = Convert.ToBase64String(_signingIdentity.Sign(Encoding.UTF8.GetBytes(canonical))),
        }, token).ConfigureAwait(false);
        if (reply.Schema != DataContributionTransport.ApiVersion || reply.InstallationHandle == Guid.Empty)
            throw new InvalidDataException("The registration service returned an invalid identity.");
        _state = _state with { InstallationHandle = reply.InstallationHandle };
        SaveState();
        return reply.InstallationHandle;
    }

    private async Task<T> SignedSeasonOperationAsync<T>(string route, string operation, Guid handle, string season,
        bool? publishAnonymousCurve, CancellationToken token)
    {
        var requestId = Guid.NewGuid();
        var payload = publishAnonymousCurve is null
            ? JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, installationHandle = handle, requestId, season }, Json)
            : JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, installationHandle = handle, requestId, season,
                publishAnonymousCurve = publishAnonymousCurve.Value }, Json);
        var timestamp = _utcNow();
        var canonical = DataContributionTransport.CanonicalDeviceRequest(operation, handle, requestId, season, payload, timestamp);
        return await PostSignedAsync<T>(route, payload, _signingIdentity.Sign(Encoding.UTF8.GetBytes(canonical)),
            timestamp, token).ConfigureAwait(false);
    }

    private Task<T> PostSignedAsync<T>(string route, byte[] payload, byte[] signature, DateTimeOffset timestamp,
        CancellationToken token) => PostJsonAsync<T>(route, new
        {
            payloadBase64 = Convert.ToBase64String(payload),
            signature = Convert.ToBase64String(signature),
            timestamp = timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                System.Globalization.CultureInfo.InvariantCulture),
        }, token);

    private async Task<T> PostJsonAsync<T>(string route, object body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body, options: Json) };
        return await SendAsync<T>(request, token).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken token)
    {
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response.Content, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string? message = null;
            try { message = JsonSerializer.Deserialize<ApiError>(bytes, Json)?.Error; } catch (JsonException) { }
            throw new HttpRequestException(message ?? $"Data service returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("The data service returned an empty response.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes) throw new InvalidDataException("Data service response is too large.");
        await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > MaxResponseBytes) throw new InvalidDataException("Data service response is too large.");
            output.Write(buffer, 0, count);
        }
    }

    private void SaveState() => DataContributionServerState.Save(_statePath, _state);
    public void Dispose() => _http.Dispose();

    private sealed record ApiError(string? Error);
    private sealed record ChallengeReply(int Schema, Guid ChallengeId, string Nonce, DateTimeOffset ExpiresAt);
    private sealed record RegistrationReply(int Schema, Guid InstallationHandle, DateTimeOffset RegisteredAt);
    private sealed record UploadReply(int Schema, Guid ReceiptId, int Accepted, int Unchanged, Guid CurveId, string? SeasonCode);
    private sealed record CodeReply(int Schema, Guid CurveId, string SeasonCode);
    private sealed record VisibilityReply(int Schema, Guid CurveId, bool PublishAnonymousCurve);
}
