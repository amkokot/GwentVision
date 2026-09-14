-- Output fields in a PL/pgSQL RETURNS TABLE function are variables. Qualify the
-- cleanup columns so PostgreSQL never confuses them with the expires_at output.

begin;

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

revoke all on function public.gw_create_registration_challenge(text) from public, anon, authenticated;
grant execute on function public.gw_create_registration_challenge(text) to service_role;

commit;
