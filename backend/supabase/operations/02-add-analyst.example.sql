-- Create/invite the person under Authentication > Users first, then replace the email.
do $$
declare
    v_email constant text := 'REPLACE_person@example.com';
    v_user_id uuid;
begin
    if v_email like 'REPLACE_%' then raise exception 'Replace the analyst email first.'; end if;
    select id into strict v_user_id from auth.users where lower(email) = lower(v_email);
    insert into private.analysts(user_id, scopes, disabled_at)
    values (v_user_id, array['read_matches']::text[], null)
    on conflict (user_id) do update set scopes = excluded.scopes, disabled_at = null;
end;
$$;
