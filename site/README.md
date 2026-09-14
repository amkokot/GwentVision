# Public season MMR site

This dependency-free static site is deployed to GitHub Pages by
`.github/workflows/pages.yml`. The workflow validates the repository variable
`PUBLIC_DATA_API` and writes it to `runtime-config.js` during deployment. The
value is the public Supabase Edge Function URL ending in `/functions/v1/gw-api`;
it contains no API key or upload authority.

The browser calls only the sanitized public routes documented in
`backend/supabase/functions/gw-api/README.md`. Season codes are submitted directly
to the highlight route and are never saved in browser storage or included in the
curve feed.

For local layout work, serve this directory over HTTP and replace the empty
`runtime-config.js` in the temporary served copy with:

```js
window.GWENT_VISION_CONFIG = { apiBase: "https://PROJECT_REF.supabase.co/functions/v1/gw-api" };
```

Do not commit an analyst publishable key, service-role key, database password, or
Supabase access token to this directory.
