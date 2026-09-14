-- Patch labels are the season identity. The first valid upload for a monthly
-- patch creates its bounded partition and public season row automatically.

begin;

-- Normalize a staging season created with the earlier calendar slug before the
-- automatic rule was introduced. IDs stay stable, so contribution references
-- and curve IDs are preserved.
update private.seasons set
       slug = patch,
       display_name = 'Patch ' || patch,
       starts_at = make_date(split_part(patch, '.', 1)::integer + 2012,
                    split_part(patch, '.', 2)::integer, 1)::timestamp at time zone 'Europe/Warsaw',
       ends_at = (make_date(split_part(patch, '.', 1)::integer + 2012,
                  split_part(patch, '.', 2)::integer, 1) + interval '1 month')::timestamp
                  at time zone 'Europe/Warsaw'
 where patch ~ '^[0-9]{1,2}\.([1-9]|1[0-2])$';
update public.published_seasons published set slug = season.slug,
       display_name = season.display_name, patch = season.patch,
       starts_at = make_date(split_part(season.patch, '.', 1)::integer + 2012,
                   split_part(season.patch, '.', 2)::integer, 1),
       ends_at = (make_date(split_part(season.patch, '.', 1)::integer + 2012,
                 split_part(season.patch, '.', 2)::integer, 1) + interval '1 month')::date
  from private.seasons season where season.id = published.season_id;

create unique index if not exists seasons_patch_unique on private.seasons (patch);

create or replace function private.ensure_patch_season(p_patch text)
returns bigint
language plpgsql volatile security definer
set search_path = ''
as $$
declare
    v_major integer;
    v_month integer;
    v_start_date date;
    v_end_date date;
    v_start timestamptz;
    v_end timestamptz;
    v_id bigint;
    v_active_id bigint;
    v_created boolean := false;
begin
    if p_patch !~ '^[0-9]{1,2}\.([1-9]|1[0-2])$' then
        raise exception 'patch is not a monthly Gwent patch' using errcode = '22023';
    end if;
    v_major := split_part(p_patch, '.', 1)::integer;
    v_month := split_part(p_patch, '.', 2)::integer;
    if v_major < 12 then
        raise exception 'patch predates automatic monthly seasons' using errcode = '22023';
    end if;
    v_start_date := make_date(v_major + 2012, v_month, 1);
    v_end_date := (v_start_date + interval '1 month')::date;
    v_start := v_start_date::timestamp at time zone 'Europe/Warsaw';
    v_end := v_end_date::timestamp at time zone 'Europe/Warsaw';
    if v_start_date < (date_trunc('month', now() at time zone 'Europe/Warsaw') - interval '13 months')::date or
       v_start_date > date_trunc('month', now() at time zone 'Europe/Warsaw')::date then
        raise exception 'patch is outside the accepted upload window' using errcode = '22023';
    end if;

    select id into v_id from private.seasons where patch = p_patch;
    if not found then
        -- Different first-seen patches are serialized only while their rows and
        -- partitions are created. Normal uploads do not take this global lock.
        perform pg_advisory_xact_lock(hashtextextended('gwent-vision-season-creation', 0));
        select id into v_id from private.seasons where patch = p_patch;
        if not found then
            insert into private.seasons(slug, display_name, patch, starts_at, ends_at, state)
            values (p_patch, 'Patch ' || p_patch, p_patch, v_start, v_end, 'closed')
            returning id into v_id;
            v_created := true;
        end if;
    end if;
    if (select state from private.seasons where id = v_id) = 'retired' then
        raise exception 'patch has passed its retention window' using errcode = 'P0001';
    end if;

    if v_created or to_regclass('private.matches_s' || v_id) is null then
        execute format('create table if not exists private.%I partition of private.matches for values in (%s)',
                       'matches_s' || v_id, v_id);
    end if;
    if v_created then
        insert into public.published_seasons(season_id, slug, display_name, patch, starts_at, ends_at, active)
        values (v_id, p_patch, 'Patch ' || p_patch, p_patch, v_start_date, v_end_date, false);
        select id into v_active_id from private.seasons
         where state <> 'retired' order by starts_at desc, id desc limit 1;
        update private.seasons set state = case when id = v_active_id then 'open' else 'closed' end
         where state <> 'retired';
        update public.published_seasons set active = (season_id = v_active_id) where true;
    end if;
    return v_id;
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
    v_patch_max text;
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
    select min(value->>'patch'), max(value->>'patch') into v_patch, v_patch_max
      from jsonb_array_elements(p_matches);
    if v_patch is null or v_patch <> v_patch_max or p_season_slug <> v_patch then
        raise exception 'one batch must use one patch as its season' using errcode = '22023';
    end if;

    select id into v_installation_id from private.installations
     where handle = p_installation_handle and disabled_at is null and deletion_requested_at is null
     for update;
    if not found then raise exception 'installation is unavailable' using errcode = 'P0001'; end if;
    v_season_id := private.ensure_patch_season(v_patch);
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
           v_game_date > (select ends_at::date from private.seasons where id = v_season_id) or
           v_game_date > (now() at time zone 'UTC')::date or
           (v_played_at is not null and (v_played_at < (select starts_at from private.seasons where id = v_season_id) or
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
    if v_changed > 0 then
        update private.seasons set archive_verified_at = null, archive_digest = null where id = v_season_id;
    end if;
    perform private.refresh_installation_curve(v_installation_id, v_season_id);
    return query select v_receipt, v_changed, v_unchanged, v_curve, v_code_created;
end;
$$;

revoke all on function private.ensure_patch_season(text) from public, anon, authenticated;
revoke all on function public.gw_accept_live_upload(uuid, text, uuid, text, text, integer, boolean, text, jsonb)
    from public, anon, authenticated;
grant execute on function public.gw_accept_live_upload(uuid, text, uuid, text, text, integer, boolean, text, jsonb)
    to service_role;

comment on function private.ensure_patch_season(text) is
    'Creates monthly patch seasons and partitions on first valid upload; no calendar administration is required.';

commit;
