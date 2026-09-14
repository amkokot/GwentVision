select pg_size_pretty(database_bytes) as last_measured_size,
       pressure_mode, last_size_check_at, last_retired_season_id, last_error
  from private.maintenance_state where singleton;

select * from private.maintenance_tick(true);
