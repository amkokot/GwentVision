-- Create/invite the person under Authentication > Users first, then replace the
-- email and producer label. The account can upload only to this contract.
do $$
declare
    v_email constant text := 'REPLACE_person@example.com';
    v_producer constant text := 'REPLACE organization or pipeline';
    v_user_id uuid;
begin
    if v_email like 'REPLACE_%' or v_producer like 'REPLACE %' then
        raise exception 'Replace the collaborator email and producer label first.';
    end if;
    select id into strict v_user_id from auth.users where lower(email) = lower(v_email);
    insert into private.collaborator_producers(
        user_id, dataset_namespace, metadata_schema_version, producer,
        max_matches_per_season, disabled_at)
    values (v_user_id, 'gwent-vision-collaborator-match', 1, v_producer, 25000, null)
    on conflict (user_id, dataset_namespace, metadata_schema_version) do update set
        producer = excluded.producer,
        max_matches_per_season = excluded.max_matches_per_season,
        disabled_at = null;
end;
$$;
