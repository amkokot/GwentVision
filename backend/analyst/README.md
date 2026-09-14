# Analyst export utility

`Export-GwentVisionData.ps1` signs an analyst into Supabase Auth with an
individual account and downloads only through the allowlisted Edge Function. It
never needs or accepts the database password or Supabase secret key.

Run it from a private working directory:

```powershell
./backend/analyst/Export-GwentVisionData.ps1 `
  -SupabaseUrl https://PROJECT_REF.supabase.co `
  -PublishableKey sb_publishable_REPLACE `
  -Season 14.9 `
  -Email analyst@example.com `
  -Source live_game `
  -OutputPath ./private-analysis/14.9-live.ndjson
```

The password is requested through `Get-Credential`; it does not appear in the
command or output. By default the utility expands `json-gzip-v1` into a nested
`match` object and removes its compressed duplicate. Pass `-KeepCompressedPayload`
only when the research pipeline will decode it separately.

The output is pseudonymous private research data. Store it outside the public
repository, restrict access to the approved group, encrypt backups, and delete
working copies when they are no longer needed.
