using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

/// <summary>GVM3 adds rating uncertainty; GVM1/2 remain readable. SHA-256, Brotli and a per-file string dictionary.</summary>
public static class CompactMatchCodec
{
    private const int Limit = 4 * 1024 * 1024;
    public static byte[] Encode(CompactMatch match)
    {
        using var raw = new MemoryStream();
        using (var w = new BinaryWriter(raw, Encoding.UTF8, true))
        {
            var strings = new List<string>();
            void Add(string? s) { if (s is not null && !strings.Contains(s, StringComparer.Ordinal)) strings.Add(s); }
            foreach (var s in new[] { match.DetectorVersion, match.RulesVersion, match.Patch, match.Result }) Add(s);
            foreach (var p in new[] { match.User, match.Opponent })
            {
                Add(p.Faction); Add(p.Leader); Add(p.Stratagem);
                foreach (var c in p.Observations.Concat(p.Reference).Concat(p.Hypothesis)) Add(c.CardId);
            }
            foreach (var e in match.Actions) { Add(e.CardId); Add(e.Kind); }
            w.Write7BitEncodedInt(strings.Count);
            foreach (var s in strings) w.Write(s);
            var ids = strings.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i + 1, StringComparer.Ordinal);
            void S(string? s) => w.Write7BitEncodedInt(s is null ? 0 : ids[s]);
            void N(int? n) { w.Write(n.HasValue); if (n.HasValue) w.Write7BitEncodedInt(n.Value); }
            void Cards(MatchCard[] cards)
            {
                w.Write7BitEncodedInt(cards.Length);
                foreach (var c in cards) { S(c.CardId); w.Write7BitEncodedInt(c.Copies); w.Write((byte)c.Evidence); w.Write((byte)c.Origin); w.Write(c.Confidence); }
            }
            w.Write(match.InstallationId.ToByteArray()); w.Write(match.MatchId.ToByteArray()); w.Write(match.GameDateUtc.DayNumber);
            S(match.DetectorVersion); S(match.RulesVersion); S(match.Patch); w.Write(match.PatchInferred);
            w.Write7BitEncodedInt64(match.Revision); w.Write(match.CaptureStopped); w.Write(match.ResultObserved); S(match.Result);
            N(match.MmrAfter); N(match.MmrChange); N(match.MmrPeak); w.Write(match.FactionMmr); N(match.Rank);
            foreach (var p in new[] { match.User, match.Opponent })
            { S(p.Faction); S(p.Leader); S(p.Stratagem); Cards(p.Observations); Cards(p.Reference); Cards(p.Hypothesis); }
            w.Write7BitEncodedInt(match.Rounds.Length);
            foreach (var r in match.Rounds) { w.Write7BitEncodedInt(r.Number); N(r.UserScore); N(r.OpponentScore); w.Write(r.FinalConfirmed); }
            w.Write7BitEncodedInt(match.Actions.Length);
            foreach (var e in match.Actions) { S(e.CardId); w.Write((byte)(e.Side is null ? 0 : (int)e.Side + 1)); w.Write7BitEncodedInt(e.Round); S(e.Kind); }
            w.Write(match.SequenceTruncated);
            w.Write(match.StartedAtUtc.HasValue);
            if (match.StartedAtUtc is { } started) w.Write(started.UtcTicks);
            w.Write(match.MmrUnconfirmed);
        }
        if (raw.Length > Limit) throw new InvalidDataException("Match exceeds supported record size.");
        var payload = raw.ToArray();
        using var output = new MemoryStream(); output.Write("GVM3"u8); output.Write(SHA256.HashData(payload));
        using (var compressed = new BrotliStream(output, CompressionLevel.SmallestSize, true)) compressed.Write(payload);
        return output.ToArray();
    }

    public static CompactMatch Decode(byte[] bytes)
    {
        if (bytes.Length is < 36 or > Limit ||
            !(bytes.AsSpan(0, 4).SequenceEqual("GVM1"u8) || bytes.AsSpan(0, 4).SequenceEqual("GVM2"u8) || bytes.AsSpan(0, 4).SequenceEqual("GVM3"u8)))
            throw new InvalidDataException("Unknown or invalid match format.");
        using var input = new MemoryStream(bytes, 36, bytes.Length - 36);
        using var compressed = new BrotliStream(input, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = compressed.Read(buffer)) != 0)
        { if (raw.Length + read > Limit) throw new InvalidDataException("Oversized match payload."); raw.Write(buffer, 0, read); }
        if (!CryptographicOperations.FixedTimeEquals(bytes.AsSpan(4, 32), SHA256.HashData(raw.ToArray())))
            throw new InvalidDataException("Match checksum failed.");
        raw.Position = 0;
        using var r = new BinaryReader(raw, Encoding.UTF8);
        int Count(int max = 65536)
        { var n = r.Read7BitEncodedInt(); return n is >= 0 && n <= max ? n : throw new InvalidDataException("Invalid collection length."); }
        var strings = Enumerable.Range(0, Count()).Select(_ => r.ReadString()).ToArray();
        string? S() { var n = Count(strings.Length); return n == 0 ? null : strings[n - 1]; }
        string Required() => S() ?? throw new InvalidDataException("Missing required string.");
        int? N() => r.ReadBoolean() ? r.Read7BitEncodedInt() : null;
        MatchCard[] Cards() => Enumerable.Range(0, Count()).Select(_ =>
        {
            var card = new MatchCard(Required(), Count(), (MatchCardEvidence)r.ReadByte(), (CardProvenance)r.ReadByte(), r.ReadByte());
            if (card.Copies == 0 || !Enum.IsDefined(card.Evidence) || !Enum.IsDefined(card.Origin)) throw new InvalidDataException("Invalid card evidence.");
            return card;
        }).ToArray();
        MatchPlayer Player() => new(S(), S(), S(), Cards(), Cards(), Cards());
        var installation = new Guid(r.ReadBytes(16)); var id = new Guid(r.ReadBytes(16)); var date = DateOnly.FromDayNumber(r.ReadInt32());
        if (installation == Guid.Empty || id == Guid.Empty) throw new InvalidDataException("Missing match identity.");
        var version = Required(); var rules = Required(); var patch = Required(); var inferred = r.ReadBoolean();
        var revision = r.Read7BitEncodedInt64(); var stopped = r.ReadBoolean(); var ended = r.ReadBoolean(); var result = S();
        var mmr = N(); var delta = N(); var peak = N(); var faction = r.ReadBoolean(); var rank = N();
        var user = Player(); var opponent = Player();
        var rounds = Enumerable.Range(0, Count(3)).Select(_ => new MatchRound(Count(3), N(), N(), r.ReadBoolean())).ToArray();
        var actions = Enumerable.Range(0, Count()).Select(_ =>
        {
            var card = S(); var side = r.ReadByte(); var round = Count(3); var kind = Required();
            if (side > 2) throw new InvalidDataException("Invalid player side.");
            return new MatchAction(card, side == 0 ? null : (PlayerSide)(side - 1), round, kind);
        }).ToArray();
        var truncated = r.ReadBoolean();
        DateTimeOffset? started = bytes[3] >= (byte)'2' && r.ReadBoolean() ? new(r.ReadInt64(), TimeSpan.Zero) : null;
        var mmrUnconfirmed = bytes[3] >= (byte)'3' && r.ReadBoolean();
        if (started is { } stamp && DateOnly.FromDateTime(stamp.UtcDateTime) != date)
            throw new InvalidDataException("Match date and start time disagree.");
        if (raw.Position != raw.Length) throw new InvalidDataException("Unexpected match payload bytes.");
        foreach (var p in new[] { user, opponent })
            if (p.Observations.Any(c => c.Evidence != MatchCardEvidence.Observed) ||
                p.Reference.Any(c => c.Evidence != MatchCardEvidence.SelectedReference) ||
                p.Hypothesis.Any(c => c.Evidence is not MatchCardEvidence.Inferred and not MatchCardEvidence.ManualHypothesis))
                throw new InvalidDataException("Mixed observed and inferred evidence.");
        return new(installation, id, date, version, rules, patch, inferred, revision, stopped, ended, result,
            mmr, delta, peak, faction, rank, user, opponent, rounds, actions, truncated, started, mmrUnconfirmed);
    }
}
