# Collaborator match imports

The collaborator channel accepts match records from an approved external
pipeline while keeping them separate from direct Gwent Vision observations.
Every stored row is marked `collaborator_import` and carries a private source,
dataset contract, schema version, and producer version. Collaborator records do
not create public MMR curves.

## Access

Each uploader needs an individual Supabase Auth account and an enabled row in
`private.collaborator_producers`. The allowlist binds that account to one reviewed
dataset namespace and schema version. The uploader receives only:

- the Supabase project URL;
- the publishable key, which is a public client identifier;
- their own email and password.

The database password, secret API key, code pepper, and Supabase project access
are never shared. Obtain a short-lived access token with password authentication:

```http
POST https://PROJECT_REF.supabase.co/auth/v1/token?grant_type=password
apikey: sb_publishable_REPLACE
content-type: application/json

{"email":"uploader@example.com","password":"..."}
```

Send the returned `access_token` as `Authorization: Bearer TOKEN` to the import
route. A disabled or unapproved account receives no write access even when its
login is valid.

## Upload interface

```http
POST https://PROJECT_REF.supabase.co/functions/v1/gw-api/collaborator/import
Authorization: Bearer TOKEN
content-type: application/json
```

The request body is:

```json
{
  "schema": 1,
  "batchId": "9f431412-858f-43cf-b972-35b9c3e74bc5",
  "season": "14.9",
  "datasetNamespace": "gwent-vision-collaborator-match",
  "metadataSchemaVersion": 1,
  "producerVersion": "pipeline-2026.09",
  "sourceLocator": "organization-primary-export",
  "matches": [
    { "format": "json-v1", "recordBase64": "eyJtYXRjaElkIjoiLi4uIn0=" }
  ]
}
```

`sourceLocator` is a stable, non-personal name for one logical upstream source.
The Edge Function stores only its keyed digest. Keep it unchanged between months
so duplicate match IDs remain idempotent. `batchId` identifies one exact request;
reusing it with different content is rejected. One batch contains 1–250 records,
one patch only, and no more than 9 MiB of HTTP data. The default account limit is
25,000 matches per patch season.

A successful response provides a receipt and counts:

```json
{"schema":1,"receiptId":"...","accepted":123,"unchanged":4}
```

An existing match is updated only when the same `matchId` arrives with a greater
`revision`. Repeating an unchanged file is safe and reports those rows as
`unchanged`. A rejected batch is atomic and stores no partial rows.

## Match record (`json-v1`)

`recordBase64` is standard Base64 of one UTF-8 JSON object. The decoded record
uses the same bounded match model as Gwent Vision. Unknown fields are rejected.
External pipelines may omit `installationId`; if supplied it must be a UUID and
is removed before storage.

```json
{
  "matchId": "80a58ff2-72c0-42e0-afb7-09ce287d13ce",
  "gameDateUtc": "2026-09-14",
  "startedAtUtc": "2026-09-14T20:11:00.000Z",
  "detectorVersion": "external-pipeline-1.0",
  "rulesVersion": "gwent-14.9",
  "patch": "14.9",
  "patchInferred": false,
  "revision": 1,
  "captureStopped": true,
  "resultObserved": true,
  "result": "VICTORY",
  "mmrAfter": 2472,
  "mmrChange": 7,
  "mmrPeak": 2472,
  "factionMmr": true,
  "mmrUnconfirmed": false,
  "rank": null,
  "user": {
    "faction": "Skellige",
    "leader": "Onslaught",
    "stratagem": null,
    "observations": [],
    "reference": [],
    "hypothesis": []
  },
  "opponent": {
    "faction": "Nilfgaard",
    "leader": "Imprisonment",
    "stratagem": null,
    "observations": [
      {"cardId":"202879","copies":1,"evidence":"Observed","origin":"ConfirmedStartingDeck","confidence":255,"copyCountIsEstimate":true}
    ],
    "reference": [],
    "hypothesis": [
      {"cardId":"162312","copies":1,"evidence":"Inferred","origin":"ProbableStartingDeck","confidence":180,"copyCountIsEstimate":true}
    ]
  },
  "rounds": [
    {"number":1,"userScore":32,"opponentScore":27,"finalConfirmed":true}
  ],
  "actions": [],
  "sequenceTruncated": false
}
```

Allowed results are `VICTORY`, `DEFEAT`, and `DRAW`. Factions are `Monsters`,
`Nilfgaard`, `Northern Realms`, `Scoia'tael`, `Skellige`, and `Syndicate`.
Use `factionMmr: false`, `mmrAfter: null`, `mmrChange: null`, and `mmrPeak: null`
for players below Pro Rank. When `startedAtUtc` is absent, the record remains
useful for match analyses but is excluded from time-based confirmed-MMR curves.

Cards stay separated by evidence: `observations` accepts `Observed`, `reference`
accepts `SelectedReference`, and `hypothesis` accepts `Inferred` or
`ManualHypothesis`. `origin` must be one of `Unknown`, `ConfirmedStartingDeck`,
`ProbableStartingDeck`, `Spawned`, `Created`, `Copied`, `Transformed`, `Replayed`,
`Summoned`, or `Stolen`. Confidence is an integer from 0 to 255.
When present, `copyCountIsEstimate` is `false` only for `SelectedReference`
cards and `true` for observed or inferred collections.

The remaining bounds are enforced by the Edge Function: three rounds, 100 cards
per evidence collection, 256 actions, 256 KiB decoded JSON per match, MMR from
0–10,000, MMR change from -1,000–1,000, rank from 0–30, and score from 0–999.

## Supplied utility

Store one decoded match object per line in an NDJSON file, then run:

```powershell
./backend/collaborator/Import-GwentVisionMatches.ps1 `
  -SupabaseUrl https://PROJECT_REF.supabase.co `
  -PublishableKey sb_publishable_REPLACE `
  -Season 14.9 `
  -Email uploader@example.com `
  -InputPath ./private-import/14.9.ndjson `
  -SourceLocator organization-primary-export `
  -ProducerVersion pipeline-2026.09
```

The password prompt is private. Keep source files, receipts, and exported data
outside the public repository.
