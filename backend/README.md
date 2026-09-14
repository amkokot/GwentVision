# Data service

This directory contains the reviewable database contract for the planned Gwent
Vision contribution service. It contains no project URL, key, password, personal
data, or deploy credential.

The migrations establish the private raw-data boundary, transactional ingestion
and deletion routines, season-partitioned matches, strict analyst access, rolling
MMR projections, and the small anonymous projection that a GitHub Pages site may
read. `supabase/functions/gw-api` supplies the signed public write boundary and
sanitized read routes. Monthly patch seasons and partitions are created on the
first valid upload. `supabase/operations` contains deliberate owner-run analyst,
archive, maintenance, and status procedures.

Nothing here contains a project URL, key, password, personal data, or deployment
credential. Follow `docs/DATABASE-SETUP-RUNBOOK.md` to create a disposable staging
project, set its server-side secrets, deploy, and complete the acceptance tests
before production is enabled. `cache/data-service.json` contains the production
Edge URL and a non-secret service UUID, enabling one-click registration and
upload for installed apps without granting database access.

Every observation belongs to a source in `private.data_sources`:

- `live_game`: collected while an opted-in Gwent Vision installation observes
  the user's own game;
- `stream_archive`: extracted from an archived broadcast or VOD;
- `collaborator_import`: submitted through a reviewed, versioned collaborator
  dataset contract.

`dataset_namespace`, producer version, metadata schema version, and a private
source-locator digest make these sources filterable and prevent silent pooling of
data with different sampling mechanisms. Only opted-in `live_game` contributors
may receive a public monthly MMR curve.

Reviewed non-match collaborator data belongs in `private.dataset_observations`,
never in the match table. Each such dataset needs an enabled row in
`private.dataset_contracts`; this supplies a namespace, schema version, retention
limit, and review boundary before the service accepts it.
