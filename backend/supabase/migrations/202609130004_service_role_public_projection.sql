-- New Supabase projects may disable automatic table exposure. The Edge Function
-- still needs explicit read access to the sanitized public projection through
-- its server-only service role; private tables remain inaccessible directly.

begin;

grant usage on schema public to service_role;
grant select on public.published_seasons, public.published_curve_points to service_role;

commit;
