# Gwent Vision data API

`gw-api` is the only write boundary for public installations. It keeps the
Supabase secret key inside the Edge Function and calls service-role-only database
routines. The raw `private` schema is never granted to `anon` or ordinary
`authenticated` users.

The deployed base URL is:

```text
https://PROJECT_REF.supabase.co/functions/v1/gw-api
```

Required Edge Function secrets are `GV_SERVER_KEY` (a modern Supabase secret
key), `GV_CODE_PEPPER` (base64 for at least 32 random bytes), and
`GV_ALLOWED_ORIGINS` (a comma-separated exact-origin allowlist). `SUPABASE_URL`
is supplied by Supabase. A populated environment file must never be committed.

## Routes

| Route | Caller and result |
| --- | --- |
| `POST /register/challenge` | Returns a ten-minute nonce for first contribution. |
| `POST /register` | Verifies proof of the installation's ECDSA P-256 private key and returns its opaque server handle. |
| `POST /upload` | Verifies a signed bounded batch, performs revision-aware idempotent upserts, and returns a receipt plus a newly created season code. |
| `POST /code/rotate` | Replaces a season code after a signed device request. |
| `POST /curve/visibility` | Applies the anonymous-curve toggle immediately for one season. |
| `POST /data/delete` | Deletes one season or all data for the signing installation. |
| `GET /public/seasons` | Returns only published season labels, patch, dates, and active state. |
| `GET /public/curves` | Returns 5,000 sanitized, 15-minute curve points at a time; omit `faction` with `metric=faction_mmr` to retrieve all factions, and follow `nextOffset` until null. |
| `GET /public/faction-daily` | Returns daily faction match counts and results only for cells with at least five contributing installations. |
| `POST /public/highlight` | Turns a season code into the corresponding public curve ID without exposing an installation. |
| `GET /analyst/export` | Requires a valid Supabase Auth bearer token whose user UUID is allowlisted in `private.analysts`. |

## Signed request contract

Registration signs:

```text
GWENTVISION
1
register
{challenge UUID}
{base64url nonce}
{lowercase SHA-256 of SPKI public key}
```

Device operations sign UTF-8 bytes for this canonical value, with the final
timestamp normalized to UTC ISO-8601:

```text
GWENTVISION
1
{upload|code-rotate|curve-visibility|data-delete}
{installation handle UUID}
{batchId for upload, otherwise requestId}
{season slug or empty string}
{lowercase SHA-256 of decoded payloadBase64}
{timestamp as yyyy-MM-ddTHH:mm:ss.fffZ}
```

The HTTP body has exactly `payloadBase64`, `signature`, and `timestamp` fields.
Signatures are ASN.1 DER ECDSA P-256/SHA-256, matching .NET's current installation
identity implementation.

Upload match envelopes currently use `json-v1`: lower-camel-case JSON decoded
from the supported local match model. The function parses every allowed field,
rejects unknown fields, enforces evidence categories and bounds, and derives all
database index columns itself. It removes the redundant local installation UUID
and unused play-order array, then stores canonical gzip JSON as `json-gzip-v1`.
This prevents a client from smuggling names or arbitrary metadata into the raw
dataset while keeping storage compact. The local UUID is validated inside the
transport record but is never retained in that payload or projected publicly.

Network addresses are HMACed in the Edge Function. PostgreSQL receives only the
keyed digest, retains one-minute counters for at most ten minutes, and exposes
neither the digest nor rate tables to analysts.

Daily faction aggregates include only installations with anonymous public
publication enabled. They contain no curve identifier and are calculated on
demand, so changing visibility or deleting installation data affects later
responses after the five-minute public cache expires.
