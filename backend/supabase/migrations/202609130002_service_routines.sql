-- Transactional boundary used by the Gwent Vision Edge Function.
-- The private schema remains outside the Data API. Only service_role may call
-- these SECURITY DEFINER routines; clients never receive the server key.

begin;

alter default privileges in schema private revoke all on tables from public, anon, authenticated;
alter default privileges in schema private revoke all on sequences from public, anon, authenticated;
alter default privileges in schema private revoke execute on functions from public, anon, authenticated;

create or replace function private.hex_bytes(value text, expected_bytes integer)
returns bytea
language plpgsql immutable strict
set search_path = ''
as $$
begin
    if value !~ ('^[0-9a-f]{' || expected_bytes * 2 || '}$') then
        raise exception 'invalid hex value' using errcode = '22023';
    end if;
    return decode(value, 'hex');
end;
$$;

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
        else null
    end;
    if v_limit is null then
        raise exception 'unknown rate-limit route' using errcode = '22023';
    end if;

    -- A ten-minute tail is enough to enforce one-minute windows. The digest is
    -- HMAC-keyed by the Edge Function, so the database never receives an IP.
    delete from private.request_windows where window_started < now() - interval '10 minutes';
    insert into private.request_windows(route, requester_digest, window_started, hits)
    values (p_route, private.hex_bytes(p_requester_digest_hex, 32), v_window, 1)
    on conflict (route, requester_digest, window_started) do update
        set hits = private.request_windows.hits + 1
    returning hits into v_hits;
    return v_hits <= v_limit;
end;
$$;

create or replace function public.gw_create_registration_challenge(p_nonce_digest_hex text)
returns table(challenge_id uuid, expires_at timestamptz)
language plpgsql security definer
set search_path = ''
as $$
begin
    delete from private.registration_challenges challenge
      where challenge.expires_at < now() - interval '1 day'
         or challenge.used_at < now() - interval '1 day';
    return query
    insert into private.registration_challenges(nonce_digest, expires_at)
    values (private.hex_bytes(p_nonce_digest_hex, 32), now() + interval '10 minutes')
    returning id, private.registration_challenges.expires_at;
end;
$$;

create or replace function public.gw_complete_registration(
    p_challenge_id uuid,
    p_nonce_digest_hex text,
    p_key_fingerprint_hex text,
    p_public_key_spki_base64 text)
returns table(installation_handle uuid, registered_at timestamptz)
language plpgsql security definer
set search_path = ''
as $$
declare
    v_key bytea;
    v_fingerprint bytea;
    v_rows integer;
    v_id bigint;
    v_handle uuid;
    v_registered timestamptz;
begin
    v_key := decode(p_public_key_spki_base64, 'base64');
    v_fingerprint := private.hex_bytes(p_key_fingerprint_hex, 32);
    if octet_length(v_key) not between 64 and 512 or extensions.digest(v_key, 'sha256') <> v_fingerprint then
        raise exception 'public key fingerprint mismatch' using errcode = '22023';
    end if;

    update private.registration_challenges
       set used_at = now()
     where id = p_challenge_id
       and nonce_digest = private.hex_bytes(p_nonce_digest_hex, 32)
       and used_at is null
       and expires_at >= now();
    get diagnostics v_rows = row_count;
    if v_rows <> 1 then
        raise exception 'challenge is invalid, expired, or already used' using errcode = 'P0001';
    end if;

    select id, handle, private.installations.registered_at
      into v_id, v_handle, v_registered
      from private.installations
     where key_fingerprint = v_fingerprint
     for update;
    if found then
        if (select public_key_spki from private.installations where id = v_id) <> v_key or
           (select disabled_at from private.installations where id = v_id) is not null then
            raise exception 'installation is unavailable' using errcode = 'P0001';
        end if;
    else
        insert into private.installations(key_fingerprint, public_key_spki)
        values (v_fingerprint, v_key)
        returning id, handle, private.installations.registered_at into v_id, v_handle, v_registered;
    end if;
    return query select v_handle, v_registered;
end;
$$;

create or replace function public.gw_get_installation_key(p_installation_handle uuid)
returns table(key_fingerprint_hex text, public_key_spki_base64 text)
language sql stable security definer
set search_path = ''
as $$
    select encode(key_fingerprint, 'hex'),
           replace(replace(encode(public_key_spki, 'base64'), E'\n', ''), E'\r', '')
      from private.installations
     where handle = p_installation_handle and disabled_at is null and deletion_requested_at is null
$$;

create or replace function private.refresh_installation_curve(p_installation_id bigint, p_season_id bigint)
returns void
language plpgsql security definer
set search_path = ''
as $$
declare
    v_curve uuid;
    v_publish boolean;
begin
    select curve_id, publish_anonymous_curve into v_curve, v_publish
      from private.installation_seasons
     where installation_id = p_installation_id and season_id = p_season_id;
    if not found then return; end if;

    insert into public.published_seasons(season_id, slug, display_name, patch, starts_at, ends_at, active)
    select id, slug, display_name, patch, starts_at::date, ends_at::date, state = 'open'
      from private.seasons where id = p_season_id
    on conflict (season_id) do update set
        slug = excluded.slug, display_name = excluded.display_name, patch = excluded.patch,
        starts_at = excluded.starts_at, ends_at = excluded.ends_at, active = excluded.active;

    delete from public.published_curve_points
     where season_id = p_season_id and curve_id = v_curve;
    if not v_publish then return; end if;

    with ratings as (
        select date_bin(interval '15 minutes', m.played_at, timestamptz '2000-01-01 00:00:00+00') as bucket_at,
               m.player_faction as faction, m.faction_mmr_after as value, m.played_at, m.id
          from private.matches m
          join private.data_sources ds on ds.id = m.source_id
         where m.season_id = p_season_id and ds.installation_id = p_installation_id
           and ds.source_kind = 'live_game' and m.mmr_confirmed and m.faction_mmr_after is not null
           and m.player_faction is not null and m.played_at is not null
    ), latest as (
        select distinct on (faction, bucket_at) faction, bucket_at, value
          from ratings order by faction, bucket_at, played_at desc, id desc
    ), numbered as (
        select faction, bucket_at, value,
               row_number() over (partition by faction order by bucket_at)::integer as point_order
          from latest
    )
    insert into public.published_curve_points(season_id, curve_id, bucket_at, metric, faction, value, point_order)
    select p_season_id, v_curve, bucket_at, 'faction_mmr', faction, value, point_order from numbered;

    with ratings as (
        select date_bin(interval '15 minutes', m.played_at, timestamptz '2000-01-01 00:00:00+00') as bucket_at,
               m.player_faction as faction,
               greatest(m.faction_mmr_after, coalesce(m.faction_mmr_peak, m.faction_mmr_after)) as peak_value
          from private.matches m
          join private.data_sources ds on ds.id = m.source_id
         where m.season_id = p_season_id and ds.installation_id = p_installation_id
           and ds.source_kind = 'live_game' and m.mmr_confirmed and m.faction_mmr_after is not null
           and m.player_faction is not null and m.played_at is not null
    ), moments as (
        select distinct bucket_at from ratings
    ), totals as (
        select moments.bucket_at, top_four.value
          from moments
          cross join lateral (
              select coalesce(sum(peak), 0)::integer as value
                from (
                    select max(r.peak_value) as peak
                      from ratings r where r.bucket_at <= moments.bucket_at
                     group by r.faction order by max(r.peak_value) desc limit 4
                ) ranked
          ) top_four
    ), changed as (
        select bucket_at, value, lag(value) over (order by bucket_at) as previous
          from totals where value is not null
    ), numbered as (
        select bucket_at, value, row_number() over (order by bucket_at)::integer as point_order
          from changed where previous is null or value > previous
    )
    insert into public.published_curve_points(season_id, curve_id, bucket_at, metric, faction, value, point_order)
    select p_season_id, v_curve, bucket_at, 'total_mmr', '', value, point_order from numbered;
end;
$$;

create or replace function public.gw_accept_live_upload(
    p_installation_handle uuid,
    p_season_slug text,
    p_client_batch_id uuid,
    p_body_digest_hex text,
    p_producer_version text,
    p_consent_notice_version integer,
    p_publish_anonymous_curve boolean,
    p_code_lookup_hex text,
    p_matches jsonb)
returns table(receipt_id uuid, accepted_count integer, unchanged_count integer,
              curve_id uuid, code_created boolean)
language plpgsql security definer
set search_path = ''
as $$
declare
    v_installation_id bigint;
    v_season_id bigint;
    v_patch text;
    v_source_id bigint;
    v_existing private.upload_batches%rowtype;
    v_curve uuid;
    v_code_created boolean := false;
    v_changed integer := 0;
    v_unchanged integer := 0;
    v_row_count integer;
    item jsonb;
    v_played_at timestamptz;
    v_game_date date;
    v_payload bytea;
    v_receipt uuid;
    v_new_matches integer;
begin
    if jsonb_typeof(p_matches) <> 'array' or jsonb_array_length(p_matches) not between 1 and 250 then
        raise exception 'batch must contain 1 to 250 matches' using errcode = '22023';
    end if;
    if length(p_producer_version) not between 1 and 80 or p_consent_notice_version < 1 then
        raise exception 'invalid producer or consent version' using errcode = '22023';
    end if;

    select id into v_installation_id from private.installations
     where handle = p_installation_handle and disabled_at is null and deletion_requested_at is null
     for update;
    if not found then raise exception 'installation is unavailable' using errcode = 'P0001'; end if;
    select id, patch into v_season_id, v_patch from private.seasons
     where slug = p_season_slug and state = 'open' for update;
    if not found then raise exception 'season is not open' using errcode = 'P0001'; end if;
    if not exists (select 1 from private.dataset_contracts where dataset_namespace = 'gwent-vision-live'
        and metadata_schema_version = 1 and source_kind = 'live_game' and enabled) then
        raise exception 'live dataset contract is disabled' using errcode = 'P0001';
    end if;

    insert into private.data_sources(source_kind, dataset_namespace, metadata_schema_version, producer,
        producer_version, installation_id)
    values ('live_game', 'gwent-vision-live', 1, 'Gwent Vision', p_producer_version, v_installation_id)
    on conflict do nothing;
    select id into v_source_id from private.data_sources
     where source_kind = 'live_game' and dataset_namespace = 'gwent-vision-live'
       and metadata_schema_version = 1
       and installation_id = v_installation_id and source_locator_digest is null
     for update;
    update private.data_sources set producer_version = p_producer_version where id = v_source_id;

    select count(*) into v_new_matches
      from jsonb_array_elements(p_matches) candidate
     where not exists (select 1 from private.matches existing
                        where existing.source_id = v_source_id and existing.season_id = v_season_id
                          and existing.observation_key = candidate->>'observationKey');
    if (select count(*) from private.matches where source_id = v_source_id and season_id = v_season_id)
       + v_new_matches > 5000 then
        raise exception 'installation season match limit exceeded' using errcode = '22023';
    end if;

    select * into v_existing from private.upload_batches
     where source_id = v_source_id and client_batch_id = p_client_batch_id;
    if found then
        if v_existing.body_digest <> private.hex_bytes(p_body_digest_hex, 32) then
            raise exception 'batch id was reused with different content' using errcode = 'P0001';
        end if;
        select installation_season.curve_id into v_curve from private.installation_seasons installation_season
         where installation_season.installation_id = v_installation_id
           and installation_season.season_id = v_season_id;
        return query select v_existing.receipt_id, v_existing.accepted_count,
            v_existing.unchanged_count, v_curve, false;
        return;
    end if;

    insert into private.installation_seasons(installation_id, season_id, publish_anonymous_curve,
        season_code_lookup, consent_notice_version, consented_at)
    values (v_installation_id, v_season_id, p_publish_anonymous_curve,
        private.hex_bytes(p_code_lookup_hex, 32), p_consent_notice_version, now())
    on conflict (installation_id, season_id) do nothing
    returning private.installation_seasons.curve_id into v_curve;
    v_code_created := found;
    if not v_code_created then
        select private.installation_seasons.curve_id into v_curve from private.installation_seasons
         where installation_id = v_installation_id and season_id = v_season_id for update;
        update private.installation_seasons set publish_anonymous_curve = p_publish_anonymous_curve,
            consent_notice_version = p_consent_notice_version, consented_at = now()
         where installation_id = v_installation_id and season_id = v_season_id;
    end if;

    for item in select value from jsonb_array_elements(p_matches)
    loop
        v_game_date := (item->>'gameDateUtc')::date;
        v_played_at := nullif(item->>'playedAt', '')::timestamptz;
        v_payload := decode(item->>'payloadBase64', 'base64');
        if item->>'patch' <> v_patch or
           v_game_date < (select starts_at::date from private.seasons where id = v_season_id) or
           v_game_date >= (select ends_at::date from private.seasons where id = v_season_id) or
           (v_played_at is not null and (v_played_at < (select starts_at from private.seasons where id = v_season_id) or
                                        v_played_at >= (select ends_at from private.seasons where id = v_season_id))) or
           octet_length(v_payload) not between 1 and 4194304 then
            raise exception 'match does not satisfy the active season contract' using errcode = '22023';
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
            faction_mmr_after = excluded.faction_mmr_after, faction_mmr_change = excluded.faction_mmr_change,
            faction_mmr_peak = excluded.faction_mmr_peak, standard_rank = excluded.standard_rank,
            mmr_confirmed = excluded.mmr_confirmed, capture_complete = excluded.capture_complete,
            payload_format = excluded.payload_format, compact_payload = excluded.compact_payload,
            quality_flags = excluded.quality_flags, received_at = now()
        where excluded.revision > private.matches.revision;
        get diagnostics v_row_count = row_count;
        if v_row_count = 1 then v_changed := v_changed + 1; else v_unchanged := v_unchanged + 1; end if;
    end loop;

    insert into private.upload_batches(source_id, season_id, client_batch_id, body_digest,
        producer_version, accepted_count, unchanged_count, rejected_count, status)
    values (v_source_id, v_season_id, p_client_batch_id, private.hex_bytes(p_body_digest_hex, 32),
        p_producer_version, v_changed, v_unchanged, 0, 'accepted')
    returning private.upload_batches.receipt_id into v_receipt;
    perform private.refresh_installation_curve(v_installation_id, v_season_id);
    return query select v_receipt, v_changed, v_unchanged, v_curve, v_code_created;
end;
$$;

create or replace function public.gw_highlight_curve(p_season_slug text, p_code_lookup_hex text)
returns table(curve_id uuid)
language sql stable security definer
set search_path = ''
as $$
    select iseason.curve_id
      from private.installation_seasons iseason
      join private.seasons season on season.id = iseason.season_id
     where season.slug = p_season_slug and iseason.publish_anonymous_curve
       and iseason.season_code_lookup = private.hex_bytes(p_code_lookup_hex, 32)
$$;

create or replace function public.gw_rotate_season_code(
    p_installation_handle uuid, p_season_slug text, p_code_lookup_hex text)
returns table(curve_id uuid)
language plpgsql security definer
set search_path = ''
as $$
begin
    return query
    update private.installation_seasons iseason set
        season_code_lookup = private.hex_bytes(p_code_lookup_hex, 32), code_rotated_at = now()
      from private.installations installation, private.seasons season
     where installation.handle = p_installation_handle and installation.disabled_at is null
       and iseason.installation_id = installation.id and iseason.season_id = season.id
       and season.slug = p_season_slug
    returning iseason.curve_id;
end;
$$;

create or replace function public.gw_set_curve_visibility(
    p_installation_handle uuid, p_season_slug text, p_publish boolean)
returns table(curve_id uuid, published boolean)
language plpgsql security definer
set search_path = ''
as $$
declare
    v_installation_id bigint;
    v_season_id bigint;
    v_curve uuid;
begin
    select installation.id into v_installation_id from private.installations installation
     where installation.handle = p_installation_handle and installation.disabled_at is null
       and installation.deletion_requested_at is null;
    select season.id into v_season_id from private.seasons season where season.slug = p_season_slug;
    if v_installation_id is null or v_season_id is null then
        raise exception 'season contribution was not found' using errcode = 'P0001';
    end if;
    update private.installation_seasons installation_season
       set publish_anonymous_curve = p_publish
     where installation_season.installation_id = v_installation_id
       and installation_season.season_id = v_season_id
    returning installation_season.curve_id into v_curve;
    if not found then raise exception 'season contribution was not found' using errcode = 'P0001'; end if;
    perform private.refresh_installation_curve(v_installation_id, v_season_id);
    return query select v_curve, p_publish;
end;
$$;

create or replace function public.gw_delete_installation_data(
    p_installation_handle uuid, p_season_slug text default null)
returns table(deleted_seasons integer, deleted_matches integer)
language plpgsql security definer
set search_path = ''
as $$
declare
    v_installation_id bigint;
    v_season_id bigint;
    v_matches integer := 0;
    v_seasons integer := 0;
begin
    select id into v_installation_id from private.installations
     where handle = p_installation_handle and disabled_at is null for update;
    if not found then raise exception 'installation is unavailable' using errcode = 'P0001'; end if;
    if p_season_slug is not null then
        select id into v_season_id from private.seasons where slug = p_season_slug;
        if not found then raise exception 'season not found' using errcode = 'P0001'; end if;
    end if;
    delete from public.published_curve_points published using private.installation_seasons installation_season
     where published.season_id = installation_season.season_id
       and published.curve_id = installation_season.curve_id
       and installation_season.installation_id = v_installation_id
       and (v_season_id is null or installation_season.season_id = v_season_id);
    delete from private.matches m using private.data_sources ds
     where m.source_id = ds.id and ds.installation_id = v_installation_id
       and (v_season_id is null or m.season_id = v_season_id);
    get diagnostics v_matches = row_count;
    if v_season_id is null then
        select count(*) into v_seasons from private.installation_seasons where installation_id = v_installation_id;
        delete from private.installations where id = v_installation_id;
    else
        delete from private.installation_seasons where installation_id = v_installation_id and season_id = v_season_id;
        get diagnostics v_seasons = row_count;
    end if;
    return query select v_seasons, v_matches;
end;
$$;

create or replace function public.gw_is_analyst(p_user_id uuid, p_scope text)
returns boolean
language sql stable security definer
set search_path = ''
as $$
    select exists(select 1 from private.analysts where user_id = p_user_id
      and disabled_at is null and p_scope = any(scopes))
$$;

create or replace function public.gw_analyst_export(
    p_user_id uuid, p_season_slug text, p_after_id bigint default 0,
    p_limit integer default 200, p_source_kind text default null)
returns setof jsonb
language plpgsql volatile security definer
set search_path = ''
as $$
declare
    v_season_id bigint;
begin
    if not public.gw_is_analyst(p_user_id, 'read_matches') then
        raise exception 'analyst access denied' using errcode = '42501';
    end if;
    select season.id into v_season_id from private.seasons season where season.slug = p_season_slug;
    if not found then raise exception 'season not found' using errcode = 'P0001'; end if;
    insert into private.analyst_access_log(user_id, season_id, source_kind, after_id, requested_limit)
    values (p_user_id, v_season_id, p_source_kind, greatest(p_after_id, 0), greatest(1, least(p_limit, 500)));
    return query
    select jsonb_build_object(
        'id', m.id, 'sourceKind', ds.source_kind, 'datasetNamespace', ds.dataset_namespace,
        'producerVersion', ds.producer_version, 'gameDateUtc', m.game_date_utc,
        'playedAt', m.played_at, 'patch', m.patch, 'result', m.result,
        'playerFaction', m.player_faction, 'opponentFaction', m.opponent_faction,
        'factionMmrAfter', m.faction_mmr_after, 'factionMmrPeak', m.faction_mmr_peak,
        'standardRank', m.standard_rank, 'mmrConfirmed', m.mmr_confirmed,
        'captureComplete', m.capture_complete, 'payloadFormat', m.payload_format,
        'payloadBase64', encode(m.compact_payload, 'base64'), 'qualityFlags', m.quality_flags)
      from private.matches m
      join private.data_sources ds on ds.id = m.source_id
     where m.season_id = v_season_id and m.id > p_after_id
       and (p_source_kind is null or ds.source_kind = p_source_kind)
     order by m.id limit greatest(1, least(p_limit, 500));
end;
$$;

create or replace function public.gw_database_status()
returns table(database_bytes bigint, pressure_mode boolean, last_size_check_at timestamptz,
              last_retired_season_id bigint, last_error text)
language sql security definer
set search_path = ''
as $$
    select pg_database_size(current_database()), state.pressure_mode, state.last_size_check_at,
           state.last_retired_season_id, state.last_error
      from private.maintenance_state state where state.singleton
$$;

-- Public population summaries contain no curve or installation identifier. A
-- faction/day cell is suppressed until at least five installations contributed,
-- so early sparse data cannot expose a single person's result history.
create or replace function public.gw_public_faction_daily(p_season_slug text)
returns setof jsonb
language sql stable security definer
set search_path = ''
as $$
    select jsonb_build_object(
        'date', grouped.game_date_utc,
        'faction', grouped.player_faction,
        'matches', grouped.matches,
        'contributors', grouped.contributors,
        'wins', grouped.wins,
        'losses', grouped.losses,
        'draws', grouped.draws)
      from (
        select m.game_date_utc, m.player_faction,
               count(*)::integer as matches,
               count(distinct ds.installation_id)::integer as contributors,
               count(*) filter (where m.result = 'VICTORY')::integer as wins,
               count(*) filter (where m.result = 'DEFEAT')::integer as losses,
               count(*) filter (where m.result = 'DRAW')::integer as draws
          from private.matches m
          join private.data_sources ds on ds.id = m.source_id and ds.source_kind = 'live_game'
          join private.seasons season on season.id = m.season_id
          join private.installation_seasons installation_season
            on installation_season.installation_id = ds.installation_id
           and installation_season.season_id = m.season_id
           and installation_season.publish_anonymous_curve
         where season.slug = p_season_slug and m.player_faction is not null and m.capture_complete
         group by m.game_date_utc, m.player_faction
        having count(distinct ds.installation_id) >= 5
      ) grouped
     order by grouped.game_date_utc, grouped.player_faction
$$;

create or replace function private.maintenance_tick(p_force boolean default false)
returns table(checked boolean, database_bytes bigint, pressure_mode boolean,
              retired_season_id bigint, message text)
language plpgsql security definer
set search_path = ''
as $$
declare
    v_state private.maintenance_state%rowtype;
    v_size bigint;
    v_retire bigint;
    v_partition text;
    v_message text;
begin
    select * into v_state from private.maintenance_state where singleton for update;
    -- The job wakes weekly. Removing data after 23 days guarantees that public
    -- curve IDs and season-code lookups disappear no later than 30 days after a
    -- season closes, even when the size measurement itself is not due.
    delete from public.published_seasons published
     where exists (select 1 from private.seasons season where season.id = published.season_id
                    and season.state <> 'open' and season.ends_at < now() - interval '23 days');
    delete from private.installation_seasons installation_season
     where exists (select 1 from private.seasons season where season.id = installation_season.season_id
                    and season.state <> 'open' and season.ends_at < now() - interval '23 days');
    delete from private.analyst_access_log where requested_at < now() - interval '30 days';
    -- A weekly wake plus a 388-day threshold keeps verified archives within the
    -- 395-day raw-data ceiling even while the size check remains monthly.
    select id into v_retire from private.seasons
     where state = 'closed' and archive_verified_at is not null
       and ends_at < now() - interval '388 days'
     order by ends_at limit 1;
    if v_retire is not null then
        v_partition := format('private.matches_s%s', v_retire);
        if to_regclass(v_partition) is not null then execute format('drop table %s', v_partition); end if;
        delete from public.published_seasons where season_id = v_retire;
        update private.seasons set state = 'retired' where id = v_retire;
        v_state.last_retired_season_id := v_retire;
        v_message := 'retired oldest verified season at retention limit';
    end if;
    if exists (select 1 from private.seasons where state = 'closed' and archive_verified_at is null
               and ends_at < now() - interval '388 days') then
        v_state.last_error := 'retention deadline reached without a verified archive';
    else
        v_state.last_error := null;
    end if;
    if not p_force and v_state.last_size_check_at is not null and
       now() - v_state.last_size_check_at <
           (case when v_state.pressure_mode then interval '6 days' else interval '27 days' end) then
        update private.maintenance_state set last_retired_season_id = v_state.last_retired_season_id,
            last_error = v_state.last_error where singleton;
        return query select false, v_state.database_bytes, v_state.pressure_mode, v_retire,
            coalesce(v_message, 'not due');
        return;
    end if;
    v_size := pg_database_size(current_database());
    v_state.pressure_mode := v_size >= 400 * 1024 * 1024;
    if v_state.pressure_mode then
        if v_retire is null then
            select id into v_retire from private.seasons
             where state = 'closed' and archive_verified_at is not null and ends_at < now()
             order by ends_at limit 1;
            if v_retire is not null then
                v_partition := format('private.matches_s%s', v_retire);
                if to_regclass(v_partition) is not null then execute format('drop table %s', v_partition); end if;
                delete from public.published_seasons where season_id = v_retire;
                update private.seasons set state = 'retired' where id = v_retire;
                v_message := 'retired oldest verified archived season';
            else
                v_message := 'pressure mode: no closed season has a verified archive';
            end if;
        end if;
    else
        v_message := coalesce(v_message,
            case when v_size >= 350 * 1024 * 1024 then 'warning threshold reached' else 'within target' end);
    end if;
    update private.maintenance_state set last_size_check_at = now(), database_bytes = v_size,
        pressure_mode = v_state.pressure_mode, last_retired_season_id = coalesce(v_retire, last_retired_season_id),
        last_error = case when v_state.pressure_mode and v_retire is null then v_message else v_state.last_error end
     where singleton;
    return query select true, v_size, v_state.pressure_mode, v_retire, v_message;
end;
$$;

revoke all on function public.gw_create_registration_challenge(text) from public, anon, authenticated;
revoke all on function public.gw_consume_rate_limit(text, text) from public, anon, authenticated;
revoke all on function public.gw_complete_registration(uuid, text, text, text) from public, anon, authenticated;
revoke all on function public.gw_get_installation_key(uuid) from public, anon, authenticated;
revoke all on function public.gw_accept_live_upload(uuid, text, uuid, text, text, integer, boolean, text, jsonb) from public, anon, authenticated;
revoke all on function public.gw_highlight_curve(text, text) from public, anon, authenticated;
revoke all on function public.gw_rotate_season_code(uuid, text, text) from public, anon, authenticated;
revoke all on function public.gw_set_curve_visibility(uuid, text, boolean) from public, anon, authenticated;
revoke all on function public.gw_delete_installation_data(uuid, text) from public, anon, authenticated;
revoke all on function public.gw_is_analyst(uuid, text) from public, anon, authenticated;
revoke all on function public.gw_analyst_export(uuid, text, bigint, integer, text) from public, anon, authenticated;
revoke all on function public.gw_database_status() from public, anon, authenticated;
revoke all on function public.gw_public_faction_daily(text) from public, anon, authenticated;

grant execute on function public.gw_create_registration_challenge(text) to service_role;
grant execute on function public.gw_complete_registration(uuid, text, text, text) to service_role;
grant execute on function public.gw_get_installation_key(uuid) to service_role;
grant execute on function public.gw_accept_live_upload(uuid, text, uuid, text, text, integer, boolean, text, jsonb) to service_role;
grant execute on function public.gw_highlight_curve(text, text) to service_role;
grant execute on function public.gw_rotate_season_code(uuid, text, text) to service_role;
grant execute on function public.gw_set_curve_visibility(uuid, text, boolean) to service_role;
grant execute on function public.gw_delete_installation_data(uuid, text) to service_role;
grant execute on function public.gw_is_analyst(uuid, text) to service_role;
grant execute on function public.gw_analyst_export(uuid, text, bigint, integer, text) to service_role;
grant execute on function public.gw_database_status() to service_role;
grant execute on function public.gw_public_faction_daily(text) to service_role;
grant execute on function public.gw_consume_rate_limit(text, text) to service_role;

commit;
