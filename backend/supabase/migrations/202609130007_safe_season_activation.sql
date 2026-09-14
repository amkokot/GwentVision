-- Supabase's protected-database hook requires an explicit WHERE clause for
-- intentional all-row updates. Season activation recalculates only sanitized
-- public season metadata.

begin;

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

revoke all on function private.ensure_patch_season(text) from public, anon, authenticated;

commit;
