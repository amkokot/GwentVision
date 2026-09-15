-- Add the reviewed collaborator match channel. Collaborators authenticate as
-- individual Supabase users, and this private allowlist limits each identity to
-- an approved, versioned dataset namespace.
begin;

insert into private.dataset_contracts
    (dataset_namespace, metadata_schema_version, source_kind, description, retention_days, enabled)
values
    ('gwent-vision-collaborator-match', 1, 'collaborator_import',
     'Strict Gwent Vision match JSON supplied by an approved collaborator pipeline.', 395, true)
on conflict (dataset_namespace, metadata_schema_version) do update set
    source_kind = excluded.source_kind,
    description = excluded.description,
    retention_days = excluded.retention_days;

create table private.collaborator_producers (
    user_id uuid not null references auth.users(id) on delete cascade,
    dataset_namespace text not null,
    metadata_schema_version integer not null,
    producer text not null check (length(producer) between 1 and 80),
    max_matches_per_season integer not null default 25000
        check (max_matches_per_season between 1 and 100000),
    added_at timestamptz not null default now(),
    disabled_at timestamptz,
    primary key (user_id, dataset_namespace, metadata_schema_version),
    foreign key (dataset_namespace, metadata_schema_version)
        references private.dataset_contracts (dataset_namespace, metadata_schema_version)
);

alter table private.collaborator_producers enable row level security;
revoke all on table private.collaborator_producers from public, anon, authenticated;

create or replace function public.gw_consume_rate_limit(
    p_route text, p_requester_digest_hex text)
returns boolean
language plpgsql volatile security definer
set search_path = ''
as $$
declare
    v_limit integer;
    v_window timestamptz := date_trunc('minute', now());
    v_hits integer;
begin
    v_limit := case p_route
        when 'register' then 12
        when 'upload' then 120
        when 'highlight' then 120
        when 'device' then 120
        when 'public' then 600
        when 'analyst' then 60
        when 'collaborator' then 30
        else null
    end;
    if v_limit is null then
        raise exception 'unknown rate-limit route' using errcode = '22023';
    end if;

    delete from private.request_windows where window_started < now() - interval '10 minutes';
    insert into private.request_windows(route, requester_digest, window_started, hits)
    values (p_route, private.hex_bytes(p_requester_digest_hex, 32), v_window, 1)
    on conflict (route, requester_digest, window_started) do update
        set hits = private.request_windows.hits + 1
    returning hits into v_hits;
    return v_hits <= v_limit;
end;
$$;

create or replace function public.gw_accept_collaborator_match_upload(
    p_user_id uuid,
    p_dataset_namespace text,
    p_metadata_schema_version integer,
    p_source_locator_digest_hex text,
    p_season_slug text,
    p_client_batch_id uuid,
    p_body_digest_hex text,
    p_producer_version text,
    p_matches jsonb)
returns table(receipt_id uuid, accepted_count integer, unchanged_count integer)
language plpgsql security definer
set search_path = ''
as $$
declare
    v_producer private.collaborator_producers%rowtype;
    v_source_id bigint;
    v_season_id bigint;
    v_patch text;
    v_patch_max text;
    v_existing private.upload_batches%rowtype;
    v_changed integer := 0;
    v_unchanged integer := 0;
    v_row_count integer;
    v_new_matches integer;
    v_receipt uuid;
    v_game_date date;
    v_played_at timestamptz;
    v_payload bytea;
    item jsonb;
begin
    if jsonb_typeof(p_matches) <> 'array' or jsonb_array_length(p_matches) not between 1 and 250 then
        raise exception 'batch must contain 1 to 250 matches' using errcode = '22023';
    end if;
    if length(p_producer_version) not between 1 and 80 then
        raise exception 'invalid producer version' using errcode = '22023';
    end if;

    select * into v_producer
      from private.collaborator_producers
     where user_id = p_user_id
       and dataset_namespace = p_dataset_namespace
       and metadata_schema_version = p_metadata_schema_version
       and disabled_at is null
     for update;
    if not found then
        raise exception 'collaborator producer is not approved for this contract' using errcode = 'P0001';
    end if;
    if not exists (
        select 1 from private.dataset_contracts
         where dataset_namespace = p_dataset_namespace
           and metadata_schema_version = p_metadata_schema_version
           and source_kind = 'collaborator_import' and enabled
    ) then
        raise exception 'collaborator dataset contract is disabled' using errcode = 'P0001';
    end if;

    select min(value->>'patch'), max(value->>'patch') into v_patch, v_patch_max
      from jsonb_array_elements(p_matches);
    if v_patch is null or v_patch <> v_patch_max or p_season_slug <> v_patch then
        raise exception 'one batch must use one patch as its season' using errcode = '22023';
    end if;
    v_season_id := private.ensure_patch_season(v_patch);

    insert into private.data_sources(source_kind, dataset_namespace, metadata_schema_version,
        producer, producer_version, submitted_by, source_locator_digest)
    values ('collaborator_import', p_dataset_namespace, p_metadata_schema_version,
        v_producer.producer, p_producer_version, p_user_id,
        private.hex_bytes(p_source_locator_digest_hex, 32))
    on conflict do nothing;
    select id into v_source_id
      from private.data_sources
     where source_kind = 'collaborator_import'
       and dataset_namespace = p_dataset_namespace
       and metadata_schema_version = p_metadata_schema_version
       and submitted_by = p_user_id
       and source_locator_digest = private.hex_bytes(p_source_locator_digest_hex, 32)
       and producer = v_producer.producer
     for update;
    if not found then
        raise exception 'collaborator source could not be resolved' using errcode = 'P0001';
    end if;
    update private.data_sources set producer_version = p_producer_version where id = v_source_id;

    select count(*) into v_new_matches
      from jsonb_array_elements(p_matches) candidate
     where not exists (
        select 1 from private.matches existing
         where existing.source_id = v_source_id and existing.season_id = v_season_id
           and existing.observation_key = candidate->>'observationKey'
     );
    if (select count(*) from private.matches where source_id = v_source_id and season_id = v_season_id)
       + v_new_matches > v_producer.max_matches_per_season then
        raise exception 'collaborator season match limit exceeded' using errcode = '22023';
    end if;

    select * into v_existing from private.upload_batches
     where source_id = v_source_id and client_batch_id = p_client_batch_id;
    if found then
        if v_existing.body_digest <> private.hex_bytes(p_body_digest_hex, 32) then
            raise exception 'batch id was reused with different content' using errcode = 'P0001';
        end if;
        return query select v_existing.receipt_id, v_existing.accepted_count,
            v_existing.unchanged_count;
        return;
    end if;

    for item in select value from jsonb_array_elements(p_matches)
    loop
        v_game_date := (item->>'gameDateUtc')::date;
        v_played_at := nullif(item->>'playedAt', '')::timestamptz;
        v_payload := decode(item->>'payloadBase64', 'base64');
        if item->>'patch' <> v_patch or
           v_game_date < (select starts_at::date from private.seasons where id = v_season_id) or
           v_game_date > (select ends_at::date from private.seasons where id = v_season_id) or
           v_game_date > (now() at time zone 'UTC')::date or
           (v_played_at is not null and (
                v_played_at < (select starts_at from private.seasons where id = v_season_id) or
                v_played_at >= (select ends_at from private.seasons where id = v_season_id) or
                v_played_at > now() + interval '5 minutes')) or
           octet_length(v_payload) not between 1 and 4194304 then
            raise exception 'match does not satisfy the patch season contract' using errcode = '22023';
        end if;

        insert into private.matches(season_id, source_id, observation_key, client_match_id, revision,
            played_at, game_date_utc, patch, patch_inferred, result, player_faction, opponent_faction,
            faction_mmr_after, faction_mmr_change, faction_mmr_peak, standard_rank, mmr_confirmed,
            capture_complete, payload_format, compact_payload, quality_flags)
        values (v_season_id, v_source_id, item->>'observationKey', (item->>'clientMatchId')::uuid,
            (item->>'revision')::bigint, v_played_at, v_game_date, item->>'patch',
            (item->>'patchInferred')::boolean, nullif(item->>'result', ''), nullif(item->>'playerFaction', ''),
            nullif(item->>'opponentFaction', ''), (item->>'factionMmrAfter')::integer,
            (item->>'factionMmrChange')::integer, (item->>'factionMmrPeak')::integer,
            (item->>'standardRank')::integer, (item->>'mmrConfirmed')::boolean,
            (item->>'captureComplete')::boolean, item->>'payloadFormat', v_payload,
            coalesce(item->'qualityFlags', '{}'::jsonb))
        on conflict (source_id, season_id, observation_key) do update set
            client_match_id = excluded.client_match_id, revision = excluded.revision,
            played_at = excluded.played_at, game_date_utc = excluded.game_date_utc,
            patch = excluded.patch, patch_inferred = excluded.patch_inferred, result = excluded.result,
            player_faction = excluded.player_faction, opponent_faction = excluded.opponent_faction,
            faction_mmr_after = excluded.faction_mmr_after,
            faction_mmr_change = excluded.faction_mmr_change,
            faction_mmr_peak = excluded.faction_mmr_peak, standard_rank = excluded.standard_rank,
            mmr_confirmed = excluded.mmr_confirmed, capture_complete = excluded.capture_complete,
            payload_format = excluded.payload_format, compact_payload = excluded.compact_payload,
            quality_flags = excluded.quality_flags, received_at = now()
        where excluded.revision > private.matches.revision;
        get diagnostics v_row_count = row_count;
        if v_row_count = 1 then v_changed := v_changed + 1;
        else v_unchanged := v_unchanged + 1;
        end if;
    end loop;

    insert into private.upload_batches(source_id, season_id, client_batch_id, body_digest,
        producer_version, accepted_count, unchanged_count, rejected_count, status)
    values (v_source_id, v_season_id, p_client_batch_id, private.hex_bytes(p_body_digest_hex, 32),
        p_producer_version, v_changed, v_unchanged, 0, 'accepted')
    returning private.upload_batches.receipt_id into v_receipt;
    if v_changed > 0 then
        update private.seasons set archive_verified_at = null, archive_digest = null where id = v_season_id;
    end if;
    return query select v_receipt, v_changed, v_unchanged;
end;
$$;

revoke all on function public.gw_accept_collaborator_match_upload(
    uuid, text, integer, text, text, uuid, text, text, jsonb) from public, anon, authenticated;
grant execute on function public.gw_accept_collaborator_match_upload(
    uuid, text, integer, text, text, uuid, text, text, jsonb) to service_role;
revoke all on function public.gw_consume_rate_limit(text, text) from public, anon, authenticated;
grant execute on function public.gw_consume_rate_limit(text, text) to service_role;

comment on table private.collaborator_producers is
    'Per-user allowlist for reviewed collaborator match contracts; never exposed by the Data API.';
comment on function public.gw_accept_collaborator_match_upload(
    uuid, text, integer, text, text, uuid, text, text, jsonb) is
    'Accepts validated collaborator matches for an approved identity and versioned contract.';

commit;
