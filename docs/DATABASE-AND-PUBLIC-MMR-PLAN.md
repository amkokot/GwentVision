# Database and public MMR plan

Design snapshot: 2026-09-13. The database migrations and Edge Function are now
implemented locally for staging; this document contains no credentials and the
desktop uploader remains disabled.

## Repository baseline

- The private working tree is version 0.3.0. It is intentionally not a Git
  checkout and contains local recordings, diagnostics, caches, and other private
  state.
- `release/GwentVision` is the tracked public checkout for
  `https://github.com/amkokot/GwentVision`. Its clean `main` branch and
  `origin/main` both point to `d485ffe`; the current public release is v0.2.64.
- The private tree currently has 23 source files absent from the public checkout
  and 67 changed source files. The additions include local match storage (GVM3),
  installation identity, Match Data, round-score confirmation, MMR capture, and
  stream mode.
- GitHub Pages is not enabled for the public repository.
- Continue to use `tools/Export-ReleaseRepository.ps1` into a fresh staging sibling.
  Never publish the private working tree or replace the tracked public checkout.

## Recommended deployment

Use one Supabase Free project for private PostgreSQL storage and Edge Functions,
and GitHub Pages for a static public chart site. Keep analyst tooling in a separate
private repository or local workspace.

```text
Gwent Vision installation / reviewed importer
  |  locally generated device key + signed, source-labelled batch
  v
Supabase ingestion Edge Function
  |-- validates signature, schema, season, limits, and revision
  |-- writes private compact match rows
  `-- refreshes sanitized per-season curve points
          |
          v
GitHub Pages chart site (public projection only)

Allowlisted analyst login --> protected export/query function --> private rows
```

The public repository and executable may contain only:

- the public Edge Function URL and a non-secret service UUID;
- the server API version and public verification/configuration data.

A Supabase secret key or database password must never enter source control, a
GitHub Pages bundle, application settings, a release ZIP, a log, or a support
message. A publishable key is used only by the allowlisted analyst login utility;
normal installations and the public chart call the Edge Function without it.

## Installation identity and request authentication

Keep the existing installation UUID for deduplication, and add a device key pair:

1. On first normal startup, generate an ECDSA P-256 key pair locally. Protect the
   private key with Windows DPAPI for the current user under
   `%LOCALAPPDATA%/GwentVision`. Store the public key and its fingerprint alongside
   the existing identity. Updates must preserve these files.
2. Do not contact the server merely because the program started. Register the
   public key automatically on the first explicit, consented database push. This
   supplies automatic setup without an undisclosed network request.
3. Registration uses a short-lived server nonce and proof-of-possession signature.
   The server maps the public-key fingerprint to an internal contributor integer.
4. Sign uploads over a canonical tuple containing API version, HTTP operation,
   installation handle, batch UUID, season ID, payload SHA-256, and nonce/timestamp.
5. Store used batch UUIDs/nonces. Replaying the same batch is harmless; changing a
   batch or claiming another installation fails signature verification.
6. Never use one private key shared by all installations. An open-source desktop
   application cannot keep such a key secret. Device signatures prevent one user
   from modifying another device's records; they cannot prove that a submitter is
   a real person or that an open-source client was not modified. Treat uploads as
   unverified observational data and enforce per-device, per-IP, batch-size, and
   monthly match-count limits.

The app keeps unsent files locally. A successful response records the server
receipt and highest uploaded revision for each match. Retrying, pushing twice, or
correcting a newer local revision must never add another observation.

## Seasonal identification code

On the first accepted upload for an installation and season, the server creates:

- a random per-season public curve ID, never reused across seasons;
- a human-enterable code using 12 unambiguous Crockford Base32 characters;
- only an HMAC-SHA-256 lookup value for the code in the database, using a
  server-only pepper.

Return the code to the app, store it locally, and show Copy code and Open chart
actions. It identifies a public curve; it does not authorize uploads or analyst
access. A signed device request can rotate a lost/exposed code without changing
the contributor or curve. The code itself must not appear in logs or the public
curve feed.

## Private schema

Keep raw data outside any schema exposed by the Data API. A practical first schema
is:

| Table | Purpose and critical constraint |
| --- | --- |
| `private.seasons` | Stable season ID, patch, UTC boundaries, open/closed/retired state. Never infer a season solely from upload time. |
| `private.installations` | Internal integer, public key, key fingerprint, creation/disable state. No player name or Gwent account ID. |
| `private.installation_seasons` | One row per installation/season, curve ID and hashed season code. Unique on both pairs. |
| `private.source_kinds` / `private.data_sources` | Immutable `live_game`, `stream_archive`, or versioned `collaborator_import` provenance. Store dataset namespace, producer/version, metadata-schema version, and only a digest of a stream/source locator. |
| `private.dataset_contracts` | Allowlist of approved producer namespaces and schema versions, with a description, enabled state, and retention ceiling. A collaborator cannot introduce an arbitrary payload shape without a reviewed contract. |
| `private.upload_batches` | Idempotency UUID, body digest, accepted/rejected counts, server time. |
| `private.matches` | Source, season, observation key, optional client match UUID, revision, played time, extracted rating/result fields, quality flags, and compact payload. Unique on `(source, season, observation_key)`; only a greater revision may replace it. |
| `private.dataset_observations` | Bounded JSON objects for approved non-match collaborator datasets. Kept separate from matches and restricted to a `collaborator_import` source. |
| `private.analysts` / `private.analyst_access_log` | Allowlisted Supabase Auth user IDs and scoped role, plus a 30-day record of authorized export pages. |
| `public.published_curve_points` | The only anonymously readable data: season curve ID, rounded time bucket, metric/faction, value, and point order. No installation UUID, code, deck, opponent, leader, match ID, or raw timestamp. |
| `private.maintenance_state` | Last size check, pressure flag, last archive, and error state. |

Every analysis must select, stratify, or explicitly model source kind; data from
direct play, streams, and collaborator datasets must never be silently pooled.
Only opted-in direct `live_game` data can produce a public personal MMR curve.

The desktop upload preparation must decode supported GVM files locally and emit
the strict lower-camel `json-v1` transport record. The ingestion function parses
every allowed transport field rather than trusting duplicate index fields supplied
by the client. It rejects unknown fields and validates collection limits, rating
ranges, patch/season agreement, timestamps, evidence enums, and decoded size before
deriving its database index fields. Stream and collaborator submissions use
separate reviewed producer credentials and versioned metadata contracts. Keep
GVM1/2/3-to-transport compatibility tests with the desktop client.

## Rating projection

Standard rank and MMR are separate tagged states. A match with `Rank` but no
confirmed faction MMR remains valid private match data, but creates no MMR curve
point. Do not convert rank to an MMR value.

For contributor `c`, season `s`, faction `f`, and match time `t`, define the
observed season peak as:

```text
peak_f(t) = maximum confirmed faction season-peak reported at or before t
            (fall back to the running maximum of confirmed MMR-after only when
             the explicit season-peak field was unavailable)

total_mmr(t) = sum of the four largest peak_f(t) values, with every unseen faction equal to 0
```

The total is available as soon as one faction MMR is known. If only two factions
have been observed, it is their two season maxima plus two zeros. Apply all
observations with the same timestamp before calculating the point.

`total_mmr(t)` is non-decreasing. Store a public total point only when it first
becomes known or increases; visually it is a step curve with flat intervals and
strictly increasing stored values. Current faction-MMR curves may rise or fall.
Unconfirmed MMR readings may be retained privately with a quality flag, but they
do not update peaks, total MMR, public curves, or analyst default estimates.

## Edge Functions

The checked-in `gw-api` Edge Function implements this versioned API:

- `POST /register/challenge` and `POST /register`: enroll a device public key.
- `POST /upload`: accept one bounded, signed patch batch; create its monthly
  patch season and partition when needed, transact idempotent upserts, and return
  receipt counts plus the season code when created.
- `POST /code/rotate`: signed replacement of the current season code.
- `POST /curve/visibility`: apply the independent public-curve toggle without
  requiring a new match upload.
- `POST /data/delete`: signed deletion request for one installation or season.
- `GET /public/seasons`: open seasons and display metadata.
- `GET /public/curves?season=...`: cacheable, sanitized, rounded/downsampled curve
  points. Paginate or compress once the response becomes large.
- `POST /public/highlight`: hash an entered code server-side and return its current
  season curve ID; use uniform failure responses and rate limits.
- `GET /analyst/export`: authenticated and allowlisted paginated access to raw or
  decoded records. Do not distribute the Supabase secret key to analysts.

Restrict CORS to the GitHub Pages origin and local development origins, while
remembering that CORS is not authorization. Keep raw tables ungranted to `anon`
and ordinary `authenticated` users. Analysts authenticate individually; an
allowlist/RBAC policy grants their read scope. The Free Supabase dashboard roles
are broad, so only database administrators should be project members.

## Application work

1. Use the implemented DPAPI-protected upload identity and API-v1 transport.
2. Keep the versioned GVM decoder and strict transport serializer covered by
   upload preparation tests.
3. Build an upload manifest from completed records for the latest local patch. Include raw
   match UUID/revision and send each latest revision once.
4. Keep the implemented first-start consent screen and persistent Settings
   controls. Consent is required before any request. Anonymous curve publication
   defaults on; a separate monthly toggle schedules one upload on the first actual
   match after the 18th in the user's local calendar. Only a successful receipt
   marks the month complete.
5. Enable Push to database only when local records exist. Show selected season,
   number of new/updated matches, payload size, progress, server receipt, and clear
   failure/retry states.
6. Display and persist the season code after success. Add Copy, Rotate, and Open
   monthly chart actions.
7. Use HTTPS timeouts, cancellation, bounded responses, exponential backoff with
   jitter, and no automatic retry storm. Do not log payloads, codes, signatures,
   tokens, or server responses containing them.
8. Preserve local match files after upload. A server receipt is synchronization
   state, not permission to delete the user's local history.

## Public GitHub Pages site

The static `site/` project and Pages deployment workflow implement the public
dashboard with dependency-free canvas charts.
The page should provide:

- season selector, Total MMR/Faction MMR switch, and faction selector;
- all season curves as thin low-opacity lines;
- a code field that highlights one curve without exposing the code-to-installation
  mapping;
- current total and faction values beside the graph;
- a combined view of every individual faction-MMR curve, colored by faction;
- separate daily match-share and win-rate lines, with cells suppressed below five
  distinct contributing installations so curve density is never mislabeled as popularity;
- clear states for below-Pro-Rank/no MMR, partial faction coverage, unrecognized
  codes, no data, and a paused backend;
- accessible text summaries and a privacy/data-method page.

Round public times to at least 15-minute buckets and publish only the active
season unless an older season is deliberately retained. Downsample background
curves server-side, while returning the highlighted curve at full bucketed
resolution. The browser necessarily receives whatever is drawn; therefore raw
timestamps and private fields cannot be hidden by JavaScript.

## Retention and free-tier operation

Supabase Free currently provides a 500 MB database quota and places a project in
read-only mode above that threshold. Keep the existing 400 MB pressure threshold.
Partition match rows by season so retiring a closed season drops a partition and
its index instead of issuing a large row-by-row delete.

- Run the full size/retention check monthly.
- Keep a weekly scheduled job installed, but have it return after reading the tiny
  pressure flag until the monthly check has observed at least 400 MB.
- In pressure mode, check weekly, export and verify the oldest closed season, then
  drop its match partition and obsolete public projection. Never drop the active
  season.
- Alert at 350 MB and treat 450 MB as an operational incident; waiting for 500 MB
  risks read-only mode.
- The Free plan has no downloadable automatic backups and may pause after a week
  of low activity. Make an encrypted off-platform `pg_dump` before retirement and
  test restoration. Public traffic may usually prevent pausing, but it is not an
  availability guarantee.

At current documented limits, Supabase Free also includes 500,000 Edge Function
invocations and 5 GB egress monthly. GitHub Pages is free for this public repository
and has a 100 GB/month soft bandwidth limit. These are adequate for a trial, not an
uptime commitment. Measure real row, index, function, and public-feed usage after a
10,000-match synthetic import before launch.

## Adopted privacy policy

- Raw contribution data is pseudonymous because its random installation identity
  remains linkable for deduplication; never describe the private rows as completely
  anonymous. Do not collect a player name or Gwent account ID.
- Public curve publication is independently controllable and defaults on. The
  projection uses a per-season unlinkable ID and 15-minute time buckets.
- Keep only the active season public and remove curve/code lookup data no later
  than 30 days after close. Keep raw data for at most 13 months, subject to earlier
  capacity retirement of the oldest closed season after a verified encrypted export.
- Keep detailed operational logs for no more than 30 days. The implemented keyed
  network-abuse counters expire after ten minutes. Provide signed stop-sharing,
  code-rotation, and deletion routes.
- Increment the notice version and request consent again after a material policy
  change. Invalid preferences fail closed.

The complete wording and field-level policy is in `docs/DATA-CONTRIBUTION-PRIVACY.md`.

## Required owner actions, in order

The exact dashboard links, commands, secret names, and staging checks are in
`docs/DATABASE-SETUP-RUNBOOK.md`.

1. Review the adopted privacy wording. Server assignment derives the UTC calendar
   month from the validated patch number, so no recurring season configuration is
   required. The app-side automatic trigger still uses the user's local calendar.
2. Create one Supabase project in the region closest to the expected users. Save
   the database password in a password manager. Record the project URL. Create a
   modern `sb_publishable_...` key only for the protected analyst login utility.
3. Create server-only random values for the season-code HMAC pepper and operational
   signing/maintenance secrets directly in Supabase Edge Function Secrets. Never
   paste them into chat or commit them.
4. Choose the initial analyst email addresses. Create individual Supabase Auth
   accounts and add their user UUIDs to the analyst allowlist after migrations are
   deployed. Do not give analysts a shared secret/service-role key.
5. In `amkokot/GwentVision`, open Settings > Pages and select GitHub Actions as the
   source. The project site will initially use
   `https://amkokot.github.io/GwentVision/` unless a custom domain is later added.
6. Add the public API base as a GitHub repository **variable** for the chart site.
   If CI will deploy database changes,
   put the Supabase access token and database password in a protected production
   GitHub Environment as **secrets**, make deployments manual/default-branch only,
   and never expose secrets to pull-request jobs.
7. Supply the developer only the public project URL; ordinary app users need no
   account or key. Apply generated
   SQL migrations and Edge Functions from a trusted machine or reviewed protected
   workflow; keep secret values in the service dashboards.
8. Test first against a separate free staging project using synthetic identities
   and matches. Exercise duplicate pushes, greater/older revisions, reinstall/update
   persistence, corrupt/oversized input, replay, revoked keys, clock skew, rate
   limits, rank-only matches, missing faction peaks, code rotation, deletion, RLS,
   analyst isolation, and public-field leakage.
9. Import 10,000 synthetic representative matches. Measure
   `pg_total_relation_size`, total database size, Edge Function invocations, egress,
   and chart response/render time. Adjust limits and downsampling before accepting
   real uploads.
10. Review the privacy notice and contribution consent, then freeze API/schema
    version 1. Do not make a public app release before the production endpoints,
    retention job, encrypted backup, deletion route, and analyst access test all
    work.
11. Export private version 0.3.0 or its successor to a fresh
    `release/GwentVision-next`, run the public-release scanner and both validation
    suites there, and review the complete diff for private media, local paths,
    credentials, and generated runtime files.
12. Apply that reviewed tree to the clean tracked public checkout, create a feature
    branch/PR, let public CI pass without secrets, merge, enable Pages, and only then
   tag the release. Verify the downloaded ZIP contains the public connection configuration
    and no secret material.

## Implemented locally for 0.3.0

- Versioned, fail-closed consent preferences persist beside the installation
  identity in `%LOCALAPPDATA%/GwentVision/data-contribution.json`.
- A separate ECDSA P-256 key pair persists in `upload-identity.json`; Windows DPAPI
  protects its private PKCS#8 bytes for the current user and signatures use a
  canonical SHA-256/DER contract. No shared private key is embedded in the app.
- The first ordinary startup presents a dark themed Share match data / Keep data
  local dialog before any upload capability can be used.
- Settings contains an anonymous-curve toggle, on by default, and an independent
  automatic monthly-sharing toggle.
- The first actual game after the 18th reaches a receipt-gated uploader; starting
  tracking on a menu does not qualify. With no endpoint configured it reports
  that the upload remains due and sends nothing.
- Successful monthly receipts have a separate durable state file; attempted or
  failed requests cannot suppress a later retry, while a successful month cannot
  be scheduled twice locally. Server idempotency remains authoritative.
- `ContributionSource` validates direct-game, stream, and collaborator provenance.
- `DataContributionTransport` emits deterministic lower-camel JSON with string
  evidence/provenance enums and the exact canonical signing tuple expected by the
  Edge Function.
- `DataContributionClient` selects the latest valid local patch, registers the device key,
  sends only completed records and newer revisions, persists receipts and the
  season code, recovers a code lost during a commit/save interruption, and applies
  the anonymous-curve toggle. The release package includes its public configuration.
- The Supabase migrations establish the private/source/public boundary,
  season partitions, idempotent revision-aware upload transaction, top-four peak
  projection, analyst allowlist, signed deletion support, rate counters, automatic
  monthly patch rollover, and the 400 MiB archive-gated retention path. They have
  not been deployed.
- `backend/supabase/functions/gw-api` implements strict signed registration,
  contribution, code, deletion, public curve, and analyst routes. Unknown match
  fields are rejected, raw tables remain service-role-only, and public curve
  responses are paginated.
- A manual GitHub Actions workflow can deploy either a protected staging or
  production Environment without exposing runtime secrets to public builds.
- The static Pages dashboard includes total, single-faction, and all-faction
  individual curves, season-code highlighting, and privacy-thresholded daily
  faction match-share and win-rate plots.

## Go-live acceptance gates

- A copied/replayed upload produces no duplicate match.
- An older revision cannot overwrite a newer one.
- One installation cannot update another installation's rows.
- Rank-only players produce no MMR or total-MMR points.
- Total MMR is the top-four sum of confirmed season peaks and never decreases.
- Unseen factions contribute zero; one to three known peaks still produce a total.
- The public endpoint cannot reveal decks, opponents, match IDs, installation IDs,
  exact timestamps, codes, or cross-season identity.
- Anonymous/authenticated public callers cannot query any private table or analyst
  endpoint.
- Losing/rotating a season code does not change upload authority or create a new
  contributor.
- Starting a new app version preserves the installation UUID, private device key,
  upload receipts, local decks, and season codes.
- The 400 MB maintenance path is proven on staging, including archive verification
  before partition removal.

## Current service references

- Supabase API keys: https://supabase.com/docs/guides/getting-started/api-keys
- Supabase RLS: https://supabase.com/docs/guides/database/postgres/row-level-security
- Supabase database size: https://supabase.com/docs/guides/platform/database-size
- Supabase Free pausing: https://supabase.com/docs/guides/platform/free-project-pausing
- Supabase Edge Function pricing: https://supabase.com/docs/guides/functions/pricing
- GitHub Pages publishing: https://docs.github.com/en/pages/getting-started-with-github-pages/configuring-a-publishing-source-for-your-github-pages-site
- GitHub Pages limits: https://docs.github.com/en/pages/getting-started-with-github-pages/github-pages-limits
