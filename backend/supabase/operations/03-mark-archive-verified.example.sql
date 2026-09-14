-- Run only after an encrypted data-only dump has been restored into a disposable
-- database and its SHA-256 has been recorded. This authorizes automatic retirement.
do $$
declare
    v_slug constant text := 'REPLACE_2026_09';
    v_sha256 constant text := 'REPLACE_64_LOWERCASE_HEX_DIGEST';
begin
    if v_slug like 'REPLACE_%' or v_sha256 !~ '^[0-9a-f]{64}$' then
        raise exception 'Replace the season and verified archive digest first.';
    end if;
    update private.seasons set archive_verified_at = now(), archive_digest = decode(v_sha256, 'hex')
     where slug = v_slug and state = 'closed' and ends_at < now();
    if not found then raise exception 'No eligible closed season found.'; end if;
end;
$$;
