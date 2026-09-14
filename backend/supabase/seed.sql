-- Local development only. `supabase db push` does not apply this file.
do $$
declare
    v_season_id bigint;
begin
    insert into private.seasons(slug, display_name, patch, starts_at, ends_at, state)
    values ('14.9', 'Patch 14.9', '14.9', '2026-08-31 22:00:00+00',
            '2026-09-30 22:00:00+00', 'open')
    on conflict (slug) do update set display_name = excluded.display_name
    returning id into v_season_id;
    execute format('create table if not exists private.matches_s%s partition of private.matches for values in (%s)',
                   v_season_id, v_season_id);
end;
$$;
