-- Run once in the Supabase SQL Editor. The job wakes weekly, but the function
-- reads database size only every 27 days until 400 MiB pressure mode is reached.
create extension if not exists pg_cron with schema extensions;

do $$
declare
    v_job bigint;
begin
    select jobid into v_job from cron.job where jobname = 'gwent-vision-storage-maintenance';
    if v_job is not null then perform cron.unschedule(v_job); end if;
    perform cron.schedule('gwent-vision-storage-maintenance', '17 4 * * 0',
        'select private.maintenance_tick(false);');
end;
$$;
