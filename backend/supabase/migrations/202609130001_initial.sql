-- Gwent Vision contribution service: schema contract v1.
-- No credentials belong in migrations. Apply to a disposable staging project first.

create schema if not exists extensions;
create extension if not exists pgcrypto with schema extensions;

begin;

create schema if not exists private;
revoke all on schema private from public, anon, authenticated;

create table private.source_kinds (
    kind text primary key check (kind ~ '^[a-z][a-z0-9_]{1,39}$'),
    description text not null,
    may_publish_mmr_curve boolean not null default false
);

insert into private.source_kinds (kind, description, may_publish_mmr_curve) values
    ('live_game', 'Direct observation from an opted-in Gwent Vision installation.', true),
    ('stream_archive', 'Detector output from a broadcast or VOD archive.', false),
    ('collaborator_import', 'Versioned dataset supplied through a reviewed collaborator contract.', false)
on conflict (kind) do update set
    description = excluded.description,
    may_publish_mmr_curve = excluded.may_publish_mmr_curve;

-- Every accepted producer/shape is registered before ingestion. Collaborator
-- datasets receive their own migration row instead of sharing an unversioned bin.
create table private.dataset_contracts (
    dataset_namespace text not null check (dataset_namespace ~ '^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$'),
    metadata_schema_version integer not null check (metadata_schema_version between 1 and 1000),
    source_kind text not null references private.source_kinds(kind),
    description text not null,
    retention_days integer not null default 395 check (retention_days between 1 and 395),
    enabled boolean not null default true,
    approved_at timestamptz not null default now(),
    primary key (dataset_namespace, metadata_schema_version),
    unique (dataset_namespace, metadata_schema_version, source_kind)
);

insert into private.dataset_contracts
    (dataset_namespace, metadata_schema_version, source_kind, description, retention_days) values
    ('gwent-vision-live', 1, 'live_game', 'GVM direct-play match records.', 395),
    ('gwent-vision-stream', 1, 'stream_archive', 'Versioned Gwent Vision stream-match records.', 395)
on conflict (dataset_namespace, metadata_schema_version) do update set
    source_kind = excluded.source_kind,
    description = excluded.description,
    retention_days = excluded.retention_days;

create table private.seasons (
    id bigint generated always as identity primary key,
    slug text not null unique check (slug ~ '^[a-z0-9][a-z0-9._-]{0,39}$'),
    display_name text not null check (length(display_name) between 1 and 80),
    patch text not null check (length(patch) between 1 and 32),
    starts_at timestamptz not null,
    ends_at timestamptz not null,
    state text not null default 'planned' check (state in ('planned', 'open', 'closed', 'retired')),
    archive_verified_at timestamptz,
    archive_digest bytea check (archive_digest is null or octet_length(archive_digest) = 32),
    check (ends_at > starts_at)
);

create table private.installations (
    id bigint generated always as identity primary key,
    handle uuid not null default gen_random_uuid() unique,
    key_algorithm text not null default 'ecdsa-p256-sha256'
        check (key_algorithm = 'ecdsa-p256-sha256'),
    key_fingerprint bytea not null unique check (octet_length(key_fingerprint) = 32),
    public_key_spki bytea not null check (octet_length(public_key_spki) between 64 and 512),
    registered_at timestamptz not null default now(),
    disabled_at timestamptz,
    deletion_requested_at timestamptz
);

create table private.registration_challenges (
    id uuid primary key default gen_random_uuid(),
    nonce_digest bytea not null unique check (octet_length(nonce_digest) = 32),
    issued_at timestamptz not null default now(),
    expires_at timestamptz not null,
    used_at timestamptz,
    check (expires_at > issued_at)
);

-- Short-lived, keyed network digests support abuse limits without retaining IP
-- addresses. Rows are removed continuously and never enter analyst exports.
create table private.request_windows (
    route text not null check (route in ('register', 'upload', 'highlight', 'device', 'public', 'analyst')),
    requester_digest bytea not null check (octet_length(requester_digest) = 32),
    window_started timestamptz not null,
    hits integer not null default 1 check (hits > 0),
    primary key (route, requester_digest, window_started)
);

create table private.installation_seasons (
    id bigint generated always as identity primary key,
    installation_id bigint not null references private.installations(id) on delete cascade,
    season_id bigint not null references private.seasons(id) on delete cascade,
    publish_anonymous_curve boolean not null default true,
    curve_id uuid not null default gen_random_uuid(),
    season_code_lookup bytea not null check (octet_length(season_code_lookup) = 32),
    consent_notice_version integer not null check (consent_notice_version > 0),
    consented_at timestamptz not null,
    code_rotated_at timestamptz,
    unique (installation_id, season_id),
    unique (season_id, curve_id),
    unique (season_id, season_code_lookup)
);

create table private.data_sources (
    id bigint generated always as identity primary key,
    source_kind text not null references private.source_kinds(kind),
    dataset_namespace text not null check (dataset_namespace ~ '^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$'),
    metadata_schema_version integer not null check (metadata_schema_version between 1 and 1000),
    producer text not null check (length(producer) between 1 and 80),
    producer_version text not null check (length(producer_version) between 1 and 80),
    installation_id bigint references private.installations(id) on delete cascade,
    submitted_by uuid references auth.users(id) on delete set null,
    source_locator_digest bytea check (source_locator_digest is null or octet_length(source_locator_digest) = 32),
    private_metadata jsonb not null default '{}'::jsonb check (jsonb_typeof(private_metadata) = 'object'),
    created_at timestamptz not null default now(),
    unique (id, source_kind),
    foreign key (dataset_namespace, metadata_schema_version, source_kind)
        references private.dataset_contracts (dataset_namespace, metadata_schema_version, source_kind),
    check (
        (source_kind = 'live_game' and installation_id is not null and source_locator_digest is null) or
        (source_kind = 'stream_archive' and installation_id is null and source_locator_digest is not null) or
        (source_kind = 'collaborator_import')
    )
);

create unique index data_sources_live_identity
    on private.data_sources (dataset_namespace, metadata_schema_version, installation_id)
    where source_kind = 'live_game';
create unique index data_sources_stream_identity
    on private.data_sources (dataset_namespace, metadata_schema_version, source_locator_digest)
    where source_kind = 'stream_archive';
create unique index data_sources_collaborator_identity
    on private.data_sources (dataset_namespace, metadata_schema_version,
        coalesce(submitted_by, '00000000-0000-0000-0000-000000000000'::uuid),
        coalesce(source_locator_digest, '\x'::bytea), producer)
    where source_kind = 'collaborator_import';

create table private.upload_batches (
    id bigint generated always as identity primary key,
    receipt_id uuid not null default gen_random_uuid() unique,
    source_id bigint not null references private.data_sources(id) on delete cascade,
    season_id bigint not null references private.seasons(id) on delete restrict,
    client_batch_id uuid not null,
    body_digest bytea not null check (octet_length(body_digest) = 32),
    producer_version text not null check (length(producer_version) between 1 and 80),
    submitted_at timestamptz not null default now(),
    accepted_count integer not null default 0 check (accepted_count >= 0),
    unchanged_count integer not null default 0 check (unchanged_count >= 0),
    rejected_count integer not null default 0 check (rejected_count >= 0),
    status text not null check (status in ('accepted', 'partial', 'rejected')),
    unique (source_id, client_batch_id)
);

create table private.matches (
    id bigint generated always as identity,
    season_id bigint not null references private.seasons(id) on delete restrict,
    source_id bigint not null references private.data_sources(id) on delete cascade,
    observation_key text not null check (length(observation_key) between 1 and 160),
    client_match_id uuid,
    revision bigint not null check (revision > 0),
    played_at timestamptz,
    game_date_utc date not null,
    patch text not null check (length(patch) between 1 and 32),
    patch_inferred boolean not null,
    result text check (result is null or result in ('VICTORY', 'DEFEAT', 'DRAW')),
    player_faction text,
    opponent_faction text,
    faction_mmr_after integer check (faction_mmr_after between 0 and 10000),
    faction_mmr_change integer check (faction_mmr_change between -1000 and 1000),
    faction_mmr_peak integer check (faction_mmr_peak between 0 and 10000),
    standard_rank integer check (standard_rank between 0 and 30),
    mmr_confirmed boolean not null default false,
    capture_complete boolean not null default false,
    payload_format text not null check (payload_format in ('gvm1', 'gvm2', 'gvm3', 'json-v1', 'json-gzip-v1')),
    compact_payload bytea not null check (octet_length(compact_payload) between 1 and 4194304),
    quality_flags jsonb not null default '{}'::jsonb check (jsonb_typeof(quality_flags) = 'object'),
    received_at timestamptz not null default now(),
    check (played_at is null or game_date_utc = (played_at at time zone 'UTC')::date),
    check (mmr_confirmed = false or faction_mmr_after is not null and played_at is not null),
    primary key (season_id, id),
    unique (source_id, season_id, observation_key)
) partition by list (season_id);

create index matches_season_source_time on private.matches (season_id, source_id, played_at);
create index matches_season_player_faction on private.matches (season_id, player_faction) where player_faction is not null;
create index matches_confirmed_rating on private.matches (season_id, player_faction, played_at)
    where mmr_confirmed and faction_mmr_after is not null;

-- Non-match collaborator material stays outside the match table. A new dataset
-- namespace/version must first be approved in private.dataset_contracts.
create table private.dataset_observations (
    id bigint generated always as identity primary key,
    source_id bigint not null,
    source_kind text not null default 'collaborator_import' check (source_kind = 'collaborator_import'),
    season_id bigint references private.seasons(id) on delete restrict,
    observation_key text not null check (length(observation_key) between 1 and 160),
    observed_at timestamptz,
    payload jsonb not null check (jsonb_typeof(payload) = 'object' and pg_column_size(payload) <= 1048576),
    quality_flags jsonb not null default '{}'::jsonb check (jsonb_typeof(quality_flags) = 'object'),
    received_at timestamptz not null default now(),
    foreign key (source_id, source_kind) references private.data_sources (id, source_kind) on delete cascade,
    unique (source_id, observation_key)
);

create table private.analysts (
    user_id uuid primary key references auth.users(id) on delete cascade,
    scopes text[] not null default array['read_matches']::text[],
    added_at timestamptz not null default now(),
    disabled_at timestamptz,
    check (cardinality(scopes) between 1 and 20)
);

create table private.analyst_access_log (
    id bigint generated always as identity primary key,
    user_id uuid references auth.users(id) on delete set null,
    season_id bigint not null references private.seasons(id) on delete restrict,
    source_kind text references private.source_kinds(kind),
    after_id bigint not null check (after_id >= 0),
    requested_limit integer not null check (requested_limit between 1 and 500),
    requested_at timestamptz not null default now()
);

create table private.maintenance_state (
    singleton boolean primary key default true check (singleton),
    last_size_check_at timestamptz,
    database_bytes bigint check (database_bytes >= 0),
    pressure_mode boolean not null default false,
    last_retired_season_id bigint references private.seasons(id) on delete set null,
    last_error text
);

insert into private.maintenance_state (singleton) values (true) on conflict do nothing;

-- Only these two sanitized tables are available to anonymous page visitors.
create table public.published_seasons (
    season_id bigint primary key,
    slug text not null unique,
    display_name text not null,
    patch text not null,
    starts_at date not null,
    ends_at date not null,
    active boolean not null default false
);

create table public.published_curve_points (
    season_id bigint not null references public.published_seasons(season_id) on delete cascade,
    curve_id uuid not null,
    bucket_at timestamptz not null,
    metric text not null check (metric in ('total_mmr', 'faction_mmr')),
    faction text not null default '',
    value integer not null check (value between 0 and 40000),
    point_order integer not null check (point_order > 0),
    primary key (season_id, curve_id, metric, faction, point_order),
    unique (season_id, curve_id, metric, faction, bucket_at),
    check ((metric = 'total_mmr' and faction = '') or (metric = 'faction_mmr' and faction <> '')),
    check (extract(minute from bucket_at)::integer % 15 = 0 and extract(second from bucket_at) = 0)
);

create index published_curve_points_feed on public.published_curve_points (season_id, metric, faction, bucket_at);

alter table private.source_kinds enable row level security;
alter table private.dataset_contracts enable row level security;
alter table private.seasons enable row level security;
alter table private.installations enable row level security;
alter table private.registration_challenges enable row level security;
alter table private.request_windows enable row level security;
alter table private.installation_seasons enable row level security;
alter table private.data_sources enable row level security;
alter table private.upload_batches enable row level security;
alter table private.matches enable row level security;
alter table private.dataset_observations enable row level security;
alter table private.analysts enable row level security;
alter table private.analyst_access_log enable row level security;
alter table private.maintenance_state enable row level security;
alter table public.published_seasons enable row level security;
alter table public.published_curve_points enable row level security;

revoke all on all tables in schema private from public, anon, authenticated;
revoke all on public.published_seasons, public.published_curve_points from public, authenticated;
grant usage on schema public to anon, authenticated;
grant select on public.published_seasons, public.published_curve_points to anon, authenticated, service_role;

create policy "anonymous season feed" on public.published_seasons
    for select to anon, authenticated using (true);
create policy "anonymous curve feed" on public.published_curve_points
    for select to anon, authenticated using (true);

comment on table private.data_sources is
    'Immutable provenance boundary: live games, stream archives, and collaborator imports must never be silently pooled.';
comment on table private.dataset_contracts is
    'Allowlist of reviewed, versioned producer schemas and their maximum retention.';
comment on table private.dataset_observations is
    'Private non-match collaborator data; each shape requires an enabled dataset contract.';
comment on column private.data_sources.private_metadata is
    'Validated source-specific metadata. Never copied to the public projection.';
comment on table public.published_curve_points is
    'Anonymous opt-in projection only: no installation ID, match ID, deck, opponent, code, or exact timestamp.';

commit;
