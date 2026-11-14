-- The parts of a Supabase project that supabase/schema.sql relies on, for running it against a plain Postgres in CI.
-- Mirrors how Supabase does it: the API roles, auth.users, and auth.uid() / auth.jwt() reading the request's JWT
-- claims from settings. Never run this on a real Supabase project; it already has all of this.

do $$
begin
    if not exists (select 1 from pg_roles where rolname = 'anon') then create role anon nologin; end if;
    if not exists (select 1 from pg_roles where rolname = 'authenticated') then create role authenticated nologin; end if;
    if not exists (select 1 from pg_roles where rolname = 'service_role') then create role service_role nologin bypassrls; end if;
end $$;

create schema if not exists auth;

create table if not exists auth.users (
    id           uuid primary key default gen_random_uuid(),
    email        text,
    banned_until timestamptz
);

create or replace function auth.uid()
returns uuid language sql stable as $$
    select nullif(coalesce(current_setting('request.jwt.claim.sub', true),
                           current_setting('request.jwt.claims', true)::jsonb ->> 'sub'), '')::uuid;
$$;

create or replace function auth.jwt()
returns jsonb language sql stable as $$
    select coalesce(nullif(current_setting('request.jwt.claims', true), ''), '{}')::jsonb;
$$;

grant usage on schema auth, public to anon, authenticated, service_role;
grant execute on function auth.uid(), auth.jwt() to anon, authenticated, service_role;
