# Gwent Vision database setup runbook

The repository side is ready for a staging deployment. This runbook deliberately
contains placeholders rather than credentials. Begin with synthetic data in a
staging Supabase project; create production only after every acceptance check
passes.

## What is already prepared

- `backend/supabase/migrations/202609130001_initial.sql` creates the private raw
  store, source provenance, seasonal partitions, analyst allowlist, and anonymous
  public curve projection.
- `backend/supabase/migrations/202609130002_service_routines.sql` creates the
  service-role-only registration, upload, idempotency, curve, code rotation,
  deletion, analyst export, rate-limit, and storage-maintenance routines.
- `backend/supabase/migrations/202609130003_automatic_patch_seasons.sql` makes
  the validated monthly patch number the season ID and creates its partition on
  the first accepted upload.
- `backend/supabase/migrations/202609130004_service_role_public_projection.sql`
  explicitly lets the server-only Edge Function read the sanitized public
  season and curve projection when automatic table exposure is disabled.
- `backend/supabase/migrations/202609130005_registration_challenge_column_fix.sql`
  qualifies registration cleanup columns for PostgreSQL's PL/pgSQL parser.
- `backend/supabase/migrations/202609130006_canonical_public_key_base64.sql`
  returns stored device public keys as strict, single-line Base64.
- `backend/supabase/migrations/202609130007_safe_season_activation.sql` makes
  the intentional public-season activation update explicit for Supabase's
  protected-database policy.
- `backend/supabase/migrations/202609140001_collaborator_match_import.sql` adds
  individual collaborator producer authorization and transactional, versioned
  match imports without granting direct database access.
- `backend/supabase/migrations/202609140002_collaborator_rate_limit_route.sql`
  permits the collaborator API route in the short-lived request counter used
  for abuse protection.
- `backend/supabase/functions/gw-api` is the signed HTTP boundary.
- `backend/supabase/operations` contains guarded analyst, archive, cron, and
  status scripts.
- `src/GwentCompanion.Platform.Windows/Data/DataContributionClient.cs` provides
  automatic device registration, patch-season selection, signed uploads,
  revision deduplication, code recovery, and curve-visibility updates.
- `cache/data-service.json` is the only public connection file included with an
  installed app. It contains only the public production URL and a non-secret
  service identifier.
- `.github/workflows/data-service.yml` is a manual deployment workflow for
  protected `data-staging` and `data-production` GitHub Environments.

The production endpoint and non-secret service UUID are embedded in the working
build; no credential is embedded. An end user installs the app, answers the
one-time consent prompt, and clicks **Push to database**. The app handles every
connection, registration, season, signing, and deduplication step automatically.

## 1. Create the Supabase projects

1. Sign in at [Supabase](https://supabase.com/dashboard/projects), turn on MFA for
   the owner account, and create the staging database at
   [database.new](https://database.new/). A descriptive name such as
   `gwent-vision-data-staging` is useful.
2. Select the region closest to most contributors. Region choice affects latency
   and is awkward to change, so use the same region for staging and production.
3. Generate a unique database password and save it in a password manager. Do not
   paste it into source files, issue comments, chat, or app settings.
4. Record the project reference shown in the dashboard URL. It is the short value
   in `https://supabase.com/dashboard/project/PROJECT_REF`.
5. Use the second Free project for `gwent-vision-data-production` only after the
   staging checks below pass. Supabase currently allows two Free projects across
   organizations where the account is an Owner or Administrator.

The Free quota is 500 MB of database content per project and 500,000 Edge
Function invocations plus 5 GB egress per organization each month. A new database
itself normally consumes roughly 40–60 MB, and a Free project becomes read-only
above 500 MB. The service enters pressure mode at 400 MiB to leave operating room.
Review [Supabase billing and quotas](https://supabase.com/docs/guides/platform/billing-on-supabase)
and [database-size behavior](https://supabase.com/docs/guides/platform/database-size)
before launch.

## 2. Create server-only secrets

In the staging project, open **Project Settings > API Keys**. Create or copy a
modern secret key (`sb_secret_...`). This key belongs only in the Edge Function
secret named `GV_SERVER_KEY`; it must never enter Gwent Vision, GitHub Pages, or
the repository.

Generate the season-code pepper locally in PowerShell:

```powershell
$randomBytes = New-Object byte[] 32
$generator = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $generator.GetBytes($randomBytes) }
finally { $generator.Dispose() }
[Convert]::ToBase64String($randomBytes)
```

Copy the result directly into a password manager. In the Supabase dashboard open
**Edge Functions > Secrets** and create these values:

| Name | Value |
| --- | --- |
| `GV_SERVER_KEY` | The staging project's `sb_secret_...` key. |
| `GV_CODE_PEPPER` | The base64 value generated above. Use a different value in production. |
| `GV_ALLOWED_ORIGINS` | `https://amkokot.github.io,http://localhost:5173` |

Supabase supplies `SUPABASE_URL` automatically. The checked-in
`backend/supabase/functions/gw-api/example.env` is a field-name reference only.
See the official [Edge Function secrets guide](https://supabase.com/docs/guides/functions/secrets).

## 3. Deploy staging from this working tree

This route is available before the repository changes are merged. Install Node.js
20 or newer if necessary, open PowerShell, and run:

```powershell
Set-Location backend
npx supabase@latest login
npx supabase@latest link --project-ref PROJECT_REF
npx supabase@latest db push --dry-run
npx supabase@latest db push
npx supabase@latest functions deploy gw-api --no-verify-jwt
```

The login command opens a browser. Use the staging project reference when linking
and enter the database password only when the CLI prompts. Read the dry-run plan
before applying it. The official workflow is documented in the
[Supabase CLI guide](https://supabase.com/docs/guides/local-development/cli/getting-started),
[migration guide](https://supabase.com/docs/guides/local-development/database-migrations),
and [Edge Function deployment guide](https://supabase.com/docs/guides/functions/deploy).

After deployment, the service URL is:

```text
https://PROJECT_REF.supabase.co/functions/v1/gw-api
```

## 4. Confirm automatic patch seasons

No season row, date, or partition is entered manually. Gwent's monthly patch
label is the season slug: patch `14.9` covers September 2026, `14.10` covers
October 2026, and so on. CDPR's published schedule establishes calendar-month
seasons but does not publish a stable machine-readable transition hour. The app
therefore uses midnight in Warsaw, including the applicable CET or CEST offset,
as its operational boundary and keeps that patch marked as inferred. A future
detector-provided game version can replace that inference without changing the
storage contract. The first valid signed upload for a patch atomically creates
the private match partition and sanitized public season row, then closes older
patch seasons. Upload batches whose recorded game time is outside that patch
window are rejected; date-only legacy records retain a one-day UTC-boundary
allowance.
Before the first test upload, `/public/seasons` should return an empty
array; after it, the new patch should appear automatically.

The calendar rule is supported by CDPR's
[Balance Council FAQ](https://www.playgwent.com/en/news/49294/balance-council-faq-what-is-it-how-to-use-it),
the official statement that the
[next season begins with the start of the next month](https://www.playgwent.com/en/news/49447/november-season-is-here),
and the calendar-month entries in the
[GWENT Masters rankings archive](https://masters.playgwent.com/en/rankings/gwentfinity-3/september-season-2026).
The version/date mapping can be cross-checked against the independent
[gwent.one changelog](https://gwent.one/en/cards/changelog/).

Run `backend/supabase/operations/04-enable-maintenance-cron.sql` once. The cron
job wakes weekly on Sunday at 04:17 UTC. Its size read runs every 27 days below
400 MiB and every 6 days after pressure mode begins. Each weekly wake also removes
closed-season public curves and code lookups within 30 days. Supabase hosts
`pg_cron`; review the [Supabase Cron guide](https://supabase.com/docs/guides/cron)
before enabling the job.

Run `backend/supabase/operations/05-storage-status.sql` to force an initial size
measurement and confirm that `pressure_mode` is false and `last_error` is null.

## 5. Perform the first access checks

Replace `PROJECT_REF`, then check the public season endpoint:

```powershell
$api = "https://PROJECT_REF.supabase.co/functions/v1/gw-api"
Invoke-RestMethod "$api/public/seasons"
```

The season request should return only slug, display name, patch, dates, and active state.
It returns an empty season array before the first accepted test upload. After that
upload, verify the generated patch season and its initially empty curve with:

```powershell
Invoke-RestMethod "$api/public/seasons"
Invoke-RestMethod "$api/public/curves?season=14.9&metric=total_mmr"
```

Substitute the patch carried by your synthetic test match. The new season should
return no points until enough valid MMR data has been contributed. A bad origin sent from a browser,
an unknown field in a JSON body, an oversized request, or an invalid season code
must fail without echoing sensitive input.

In **Project Settings > Data API**, leave `private` out of the exposed schemas.
Confirm from an unprivileged client that `private.installations`,
`private.matches`, `private.data_sources`, and `private.analysts` cannot be read.
The migrations also revoke table and function access from `anon` and ordinary
`authenticated` roles, so both the API exposure list and PostgreSQL grants must
be wrong before raw data is exposed.

## 6. Add analysts without sharing database credentials

1. In Supabase open **Authentication > Users** and create or invite one account
   per analyst. Do not create a shared analyst login.
2. Copy `backend/supabase/operations/02-add-analyst.example.sql`, replace the email,
   and run it in SQL Editor for each approved person.
3. Give analysts the public project URL and publishable key plus
   `backend/analyst/Export-GwentVisionData.ps1`. The utility prompts privately for
   their password, paginates the protected endpoint, and expands canonical gzip
   records into NDJSON. Never give them the secret key, database password, or
   Supabase project-member access merely to download data.

The API validates the analyst's Supabase Auth token and checks the UUID and
`read_matches` scope in `private.analysts`. Removing the allowlist row or setting
`disabled_at` immediately removes export access. Analyst exports are pseudonymous
private research files and must stay outside the public repository.

## 6a. Add collaborator producers

1. Create or invite one Supabase Auth account per uploader.
2. Copy `backend/supabase/operations/06-add-collaborator-producer.example.sql`,
   replace the email and producer label, and run it in SQL Editor.
3. Give the uploader the project URL, publishable key, and
   `backend/collaborator/Import-GwentVisionMatches.ps1`.
4. Agree on a stable, non-personal source locator and keep the raw source file
   outside the repository.

The account can write only through the reviewed namespace and version in its
private allowlist row. It receives no raw-table access and cannot publish an MMR
curve. The full interface and record contract are in
[`COLLABORATOR-IMPORT.md`](COLLABORATOR-IMPORT.md).

## 7. Configure protected GitHub deployment

After the database files are merged into `amkokot/GwentVision`:

1. Visit [repository Environments](https://github.com/amkokot/GwentVision/settings/environments)
   and create `data-staging` and `data-production`.
2. In each Environment add `SUPABASE_PROJECT_ID` as an **environment variable**.
3. In each Environment add `SUPABASE_DB_PASSWORD` and `SUPABASE_ACCESS_TOKEN` as
   **environment secrets**. Create the access token at
   [Supabase account access tokens](https://supabase.com/dashboard/account/tokens).
   The current Supabase CLI cannot reliably run `supabase link` with scoped
   access tokens, so use a dedicated legacy token for this deployment workflow.
   Supabase limits new tokens to 90 days. Rotate the token before it expires by
   replacing `SUPABASE_ACCESS_TOKEN` in both GitHub Environments and rerunning
   the workflow. This credential is for deployment only; it is never included
   in the desktop app or public website.
4. Require a reviewer for `data-production` and restrict it to protected branches.
5. Open [Actions](https://github.com/amkokot/GwentVision/actions), select
   **Deploy data service**, choose `data-staging`, and run it. The workflow previews
   migrations before applying them and then deploys `gw-api`.
6. Use `data-production` only after staging passes. Edge Function runtime secrets
   stay in Supabase and are intentionally absent from GitHub.
7. At [Actions variables](https://github.com/amkokot/GwentVision/settings/variables/actions),
   add `PUBLIC_DATA_API` with the production Edge Function URL. At
   [Pages settings](https://github.com/amkokot/GwentVision/settings/pages), choose
   **GitHub Actions** as the source, then run **Deploy MMR site**. The public site
   provides total, single-faction, and all-faction individual MMR plots plus daily
   faction match-share and win-rate plots.

## 8. Configure the one-click installed client

The desktop app does not need a Supabase login or publishable key. For a private
staging build, leave the checked-in production configuration unchanged and put
the staging configuration in
`%LOCALAPPDATA%\GwentVision\data-service.json`. This per-user override is never
exported. Generate its public service identifier with `[Guid]::NewGuid()` in
PowerShell. The production release uses the checked-in retained service UUID and
production URL:

```json
{
  "Schema": 1,
  "Enabled": true,
  "ServiceId": "REPLACE_WITH_GENERATED_UUID",
  "ApiBaseUrl": "https://PROJECT_REF.supabase.co/functions/v1/gw-api"
}
```

The release workflow copies this file into the installer package. It contains no
authority or secret. On first Push, Gwent Vision requests a nonce, proves control
of its DPAPI-protected per-installation ECDSA key, receives an opaque server
handle, derives the patch season from each eligible completed match, and saves
the receipt and season code under `%LOCALAPPDATA%\GwentVision`. Repeated Pushes
send only new revisions. A reinstall or update that preserves that user-data
folder preserves the identity, deck library, receipts, and season code.

The full set of public and analyst values is:

| Name | Source |
| --- | --- |
| Desktop app | Edge Function URL and generated public service UUID only. |
| Public chart | Edge Function URL only. |
| Analyst export | Supabase URL and `sb_publishable_...` key for individual Auth login. |

The website URL may be a GitHub repository variable at
[Actions variables](https://github.com/amkokot/GwentVision/settings/variables/actions).
Public values are not upload authority. Device registration and every mutation
still require the locally held ECDSA private key.

Give the publishable key only to the approved analyst utility; normal users never
see or enter it. Keep the secret key, database password, access token, and code
pepper in their respective service dashboards and password manager.

## 9. Prove staging before accepting real data

Do not enable the production configuration yet. Enable only a staging build and
first exercise these cases using synthetic installations and records:

- first registration, repeated registration with the same key, and an expired or
  reused challenge;
- repeated identical batch, same batch UUID with changed bytes, older revision,
  and greater revision;
- a rank-only player with no MMR, one through four confirmed faction peaks, a
  decreasing faction rating, and an increasing season peak;
- observed, selected-reference, inferred, and manual-hypothesis cards, plus an
  unknown field and invalid evidence category;
- wrong patch, time outside the season, missing start time, clock skew, excessive
  requests, and an oversized batch;
- anonymous-curve opt-out and opt-in, code lookup and rotation, season deletion,
  full installation deletion, analyst denial, and analyst allowlist access;
- at least 10,000 representative synthetic matches, followed by storage, egress,
  invocation, and public-chart response measurements.

For the total-MMR check, the expected value at each point is the sum of the four
largest confirmed faction season maxima known by then, with each unseen faction
contributing zero. With two observed factions, the total is therefore the sum of
those two maxima. Every stored total point must be greater than the previous
point. Players below Pro Rank remain valid private observations and emit no MMR
point.

## 10. Back up and retire old seasons

The maintenance job never deletes a match partition until the season is closed
and `archive_verified_at` is present. For each closed season:

1. Create a data-only dump that includes `private` and `public` data.
2. Encrypt it, store it off Supabase, and calculate its SHA-256.
3. Restore it into a disposable database and verify row counts and several decoded
   records.
4. Copy `backend/supabase/operations/03-mark-archive-verified.example.sql`, insert
   the season slug and verified digest, and run it.

If a later size check sees at least 400 MiB, the oldest eligible closed season is
retired by dropping its match partition. The active season is never selected.
Because Free projects do not provide the backup guarantees needed here, the
verified encrypted export is part of the deletion authorization, not optional
bookkeeping.

## Remaining implementation before public launch

- Exercise the analyst export utility against staging and audit its exported
  fields with the designated group.
- Run the complete synthetic staging matrix and storage load test.
- Repeat the staging deployment into production with a new database password,
  secret key, code pepper, public service UUID, and app endpoint.
- Export a fresh public 0.3.0 staging tree, review its complete diff, merge through
  a pull request, deploy production, and only then ship the build with uploading
  enabled.

GitHub documents the Actions publishing path in its
[Pages publishing-source guide](https://docs.github.com/en/pages/getting-started-with-github-pages/configuring-a-publishing-source-for-your-github-pages-site).
