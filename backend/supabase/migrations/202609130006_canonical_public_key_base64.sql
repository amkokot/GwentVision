-- PostgreSQL wraps base64 output at 76 characters. Device public keys cross
-- the strict Edge boundary as canonical, unwrapped RFC 4648 base64.

begin;

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

revoke all on function public.gw_get_installation_key(uuid) from public, anon, authenticated;
grant execute on function public.gw_get_installation_key(uuid) to service_role;

commit;
