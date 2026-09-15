begin;

alter table private.request_windows
    drop constraint if exists request_windows_route_check;

alter table private.request_windows
    add constraint request_windows_route_check
    check (route in ('register', 'upload', 'highlight', 'device', 'public', 'analyst', 'collaborator'));

commit;
