-- MTG Companion: player profiles, playgroups, decks and play history.
-- Run this in your Supabase project's SQL Editor (Dashboard -> SQL Editor -> New query).
-- It is safe to run again: it only creates what is missing and replaces the functions and policies.
--
-- Upgrading from the version with email sign-in and per-playgroup profiles? Run supabase/wipe.sql first.
-- The data model changed, and wipe.sql removes the old profiles, playgroups, decks and play history.
--
-- How it fits together:
--   * Everyone signs in with an OAuth provider (Google, Apple, ...). Each signed-in user has exactly one
--     player profile (player_profiles.user_id).
--   * A profile doesn't need a user: friends without an account have unclaimed profiles, administered by
--     the signed-in user who made them. They claim it later with a single-use claim code.
--   * Profiles and playgroups are many-to-many (playgroup_profiles). Each playgroup has one admin profile.
--   * Decks belong to a profile and are visible in every playgroup that profile is in.
--   * Play history belongs to a playgroup.

-- ---------- Codes ----------

-- Random code from an alphabet without look-alikes (no I, O, 0, 1). Randomness comes from gen_random_uuid(),
-- which is built in and cryptographically random, so no extension is needed. Up to 16 characters.
create or replace function public.random_code(len int default 8)
returns text language sql volatile set search_path = public as $$
    select string_agg(substr('ABCDEFGHJKLMNPQRSTUVWXYZ23456789', (get_byte(b, i) % 32) + 1, 1), '' order by i)
    from (select decode(md5(gen_random_uuid()::text), 'hex') as b) s, generate_series(0, least(len, 16) - 1) i;
$$;

-- Codes are typed by people: ignore case, spaces and dashes.
create or replace function public.normalize_code(code text)
returns text language sql immutable set search_path = public as $$
    select upper(regexp_replace(coalesce(code, ''), '[\s-]', '', 'g'));
$$;

-- ---------- Tables ----------

create table if not exists public.player_profiles (
    id          uuid primary key default gen_random_uuid(),
    name        text not null check (char_length(name) between 1 and 60),
    -- The signed-in user this profile belongs to. Null until someone claims it.
    user_id     uuid unique references auth.users (id) on delete set null,
    -- Who made it. Administers the profile while it's unclaimed.
    created_by  uuid references auth.users (id) on delete set null default auth.uid(),
    -- Shareable: a playgroup admin types it to add this profile to their group.
    player_code text not null unique default public.random_code(8),
    -- Secret, single-use: lets someone signing in for the first time claim this profile. Only its admin can read it.
    claim_code  text unique,
    -- [{ "name", "quantity", "scryfall_id", "set_code", "collector_number" }]
    collection  jsonb not null default '[]'::jsonb check (jsonb_typeof(collection) = 'array'),
    updated_by  uuid references auth.users (id) on delete set null default auth.uid(),
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),
    check (user_id is null or claim_code is null)
);

create table if not exists public.playgroups (
    id               uuid primary key default gen_random_uuid(),
    name             text not null check (char_length(name) between 1 and 80),
    -- Anyone with it can add their own profile to the group.
    join_code        text not null unique default public.random_code(8),
    -- Always a claimed profile. Hand the group over (transfer_playgroup) before deleting that profile.
    admin_profile_id uuid not null references public.player_profiles (id) on delete restrict,
    created_at       timestamptz not null default now()
);

create table if not exists public.playgroup_profiles (
    playgroup_id uuid not null references public.playgroups (id) on delete cascade,
    profile_id   uuid not null references public.player_profiles (id) on delete cascade,
    added_by     uuid references auth.users (id) on delete set null default auth.uid(),
    added_at     timestamptz not null default now(),
    primary key (playgroup_id, profile_id)
);

create table if not exists public.decks (
    id               uuid primary key default gen_random_uuid(),
    owner_profile_id uuid not null references public.player_profiles (id) on delete cascade,
    name             text not null check (char_length(name) between 1 and 120),
    format           text not null default 'commander',
    notes            text,
    cover_image_url  text,
    -- [{ "name", "quantity", "section": "main"|"sideboard"|"commander", "scryfall_id", "set_code", "collector_number" }]
    cards            jsonb not null default '[]'::jsonb check (jsonb_typeof(cards) = 'array'),
    created_by       uuid references auth.users (id) on delete set null default auth.uid(),
    updated_by       uuid references auth.users (id) on delete set null default auth.uid(),
    created_at       timestamptz not null default now(),
    updated_at       timestamptz not null default now()
);

-- Finished games. The id is the game's own id, so recording a game twice just replaces it.
create table if not exists public.game_records (
    id           uuid primary key,
    playgroup_id uuid not null references public.playgroups (id) on delete cascade,
    format       text not null,
    started_at   timestamptz not null,
    ended_at     timestamptz not null,
    turns        int not null default 1,
    -- [{ "profile_id", "name", "deck_id", "deck_name", "won", "eliminated", "final_life" }]
    players      jsonb not null default '[]'::jsonb check (jsonb_typeof(players) = 'array'),
    created_by   uuid references auth.users (id) on delete set null default auth.uid(),
    created_at   timestamptz not null default now()
);

-- Wrong claim codes per user, for rate limiting (see claim_profile). Only the functions below touch it.
create table if not exists public.claim_attempts (
    user_id       uuid primary key references auth.users (id) on delete cascade,
    failed_total  int not null default 0,
    failed_streak int not null default 0,
    locked_until  timestamptz,
    blocked_at    timestamptz,
    updated_at    timestamptz not null default now()
);

create index if not exists player_profiles_created_by_idx on public.player_profiles (created_by);
create index if not exists playgroup_profiles_profile_id_idx on public.playgroup_profiles (profile_id);
create index if not exists playgroups_admin_profile_id_idx on public.playgroups (admin_profile_id);
create index if not exists decks_owner_profile_id_idx on public.decks (owner_profile_id);
create index if not exists game_records_playgroup_ended_idx on public.game_records (playgroup_id, ended_at desc);

-- Keep updated_at / updated_by current on every edit, and stamp who made a row.
create or replace function public.touch_row()
returns trigger language plpgsql set search_path = public as $$
begin
    if tg_op = 'INSERT' then
        new.created_by := coalesce(auth.uid(), new.created_by);
    else
        new.created_by := old.created_by;
        new.created_at := old.created_at;
    end if;
    new.updated_at := now();
    new.updated_by := auth.uid();
    return new;
end $$;

drop trigger if exists decks_touch on public.decks;
create trigger decks_touch before insert or update on public.decks
    for each row execute function public.touch_row();

drop trigger if exists player_profiles_touch on public.player_profiles;
create trigger player_profiles_touch before update on public.player_profiles
    for each row execute function public.touch_row();

-- ---------- Who's who ----------
-- SECURITY DEFINER so policies can call these without tripping row-level security on the tables they read.

-- The signed-in user's own profile, or null if they haven't made or claimed one yet.
create or replace function public.my_profile_id()
returns uuid language sql stable security definer set search_path = public as $$
    select id from public.player_profiles where user_id = auth.uid();
$$;

-- Whether the signed-in user's profile is in the playgroup.
create or replace function public.is_playgroup_member(group_id uuid)
returns boolean language sql stable security definer set search_path = public as $$
    select exists (
        select 1 from public.playgroup_profiles gp
        join public.player_profiles p on p.id = gp.profile_id
        where gp.playgroup_id = group_id and p.user_id = auth.uid()
    );
$$;

-- Whether the signed-in user is the playgroup's admin.
create or replace function public.is_playgroup_admin(group_id uuid)
returns boolean language sql stable security definer set search_path = public as $$
    select exists (
        select 1 from public.playgroups g
        join public.player_profiles p on p.id = g.admin_profile_id
        where g.id = group_id and p.user_id = auth.uid()
    );
$$;

-- Whether the signed-in user may rename, delete, or edit the collection and decks of a profile:
-- their own profile, or an unclaimed one they made. An unclaimed profile whose maker's account is gone
-- falls to the admins of its playgroups.
create or replace function public.can_manage_profile(profile uuid)
returns boolean language sql stable security definer set search_path = public as $$
    select exists (
        select 1 from public.player_profiles p
        where p.id = profile
          and auth.uid() is not null
          and (
              p.user_id = auth.uid()
              or (p.user_id is null and p.created_by = auth.uid())
              or (p.user_id is null and p.created_by is null and exists (
                  select 1 from public.playgroup_profiles gp
                  where gp.profile_id = p.id and public.is_playgroup_admin(gp.playgroup_id)))
          )
    );
$$;

-- Whether the signed-in user can see a profile (and its decks): it's theirs, they manage it,
-- or it shares a playgroup with their profile.
create or replace function public.can_see_profile(profile uuid)
returns boolean language sql stable security definer set search_path = public as $$
    select public.can_manage_profile(profile) or exists (
        select 1 from public.playgroup_profiles theirs
        join public.playgroup_profiles mine on mine.playgroup_id = theirs.playgroup_id
        where theirs.profile_id = profile and mine.profile_id = public.my_profile_id()
    );
$$;

-- ---------- Row-level security ----------

alter table public.player_profiles enable row level security;
alter table public.playgroups enable row level security;
alter table public.playgroup_profiles enable row level security;
alter table public.decks enable row level security;
alter table public.game_records enable row level security;
alter table public.claim_attempts enable row level security; -- no policies: functions only

-- Profiles: see the ones you share a group with. Only whoever manages a profile can rename it, edit its
-- collection, or delete it. New profiles are made with create_my_profile / create_profile / claim_profile.
drop policy if exists "read visible profiles" on public.player_profiles;
create policy "read visible profiles" on public.player_profiles
    for select to authenticated using (public.can_see_profile(id));

drop policy if exists "manager edits profile" on public.player_profiles;
create policy "manager edits profile" on public.player_profiles
    for update to authenticated using (public.can_manage_profile(id)) with check (public.can_manage_profile(id));

drop policy if exists "manager deletes profile" on public.player_profiles;
create policy "manager deletes profile" on public.player_profiles
    for delete to authenticated using (public.can_manage_profile(id));

-- Playgroups: members can see them; only the admin renames or deletes. Created with create_playgroup.
drop policy if exists "members read playgroups" on public.playgroups;
create policy "members read playgroups" on public.playgroups
    for select to authenticated using (public.is_playgroup_member(id));

drop policy if exists "admin renames playgroup" on public.playgroups;
create policy "admin renames playgroup" on public.playgroups
    for update to authenticated using (public.is_playgroup_admin(id)) with check (public.is_playgroup_admin(id));

drop policy if exists "admin deletes playgroup" on public.playgroups;
create policy "admin deletes playgroup" on public.playgroups
    for delete to authenticated using (public.is_playgroup_admin(id));

-- Membership: members see who's in the group. The admin can remove anyone but themself; members can take
-- their own profile out (leave). Adding goes through join_playgroup / add_profile_to_playgroup / create_profile.
drop policy if exists "members read membership" on public.playgroup_profiles;
create policy "members read membership" on public.playgroup_profiles
    for select to authenticated using (public.is_playgroup_member(playgroup_id));

drop policy if exists "admin removes or member leaves" on public.playgroup_profiles;
create policy "admin removes or member leaves" on public.playgroup_profiles
    for delete to authenticated using (
        (public.is_playgroup_admin(playgroup_id) or profile_id = public.my_profile_id())
        and profile_id <> (select g.admin_profile_id from public.playgroups g where g.id = playgroup_id)
    );

-- Decks: visible to everyone who can see the owner profile; only whoever manages that profile edits them.
drop policy if exists "read visible decks" on public.decks;
create policy "read visible decks" on public.decks
    for select to authenticated using (public.can_see_profile(owner_profile_id));

drop policy if exists "manager adds decks" on public.decks;
create policy "manager adds decks" on public.decks
    for insert to authenticated with check (public.can_manage_profile(owner_profile_id));

drop policy if exists "manager edits decks" on public.decks;
create policy "manager edits decks" on public.decks
    for update to authenticated
    using (public.can_manage_profile(owner_profile_id))
    with check (public.can_manage_profile(owner_profile_id));

drop policy if exists "manager deletes decks" on public.decks;
create policy "manager deletes decks" on public.decks
    for delete to authenticated using (public.can_manage_profile(owner_profile_id));

-- Play history: anyone in the group can read, add, edit, and delete.
drop policy if exists "members read games" on public.game_records;
create policy "members read games" on public.game_records
    for select to authenticated using (public.is_playgroup_member(playgroup_id));

drop policy if exists "members add games" on public.game_records;
create policy "members add games" on public.game_records
    for insert to authenticated with check (public.is_playgroup_member(playgroup_id));

drop policy if exists "members edit games" on public.game_records;
create policy "members edit games" on public.game_records
    for update to authenticated
    using (public.is_playgroup_member(playgroup_id))
    with check (public.is_playgroup_member(playgroup_id));

drop policy if exists "members delete games" on public.game_records;
create policy "members delete games" on public.game_records
    for delete to authenticated using (public.is_playgroup_member(playgroup_id));

-- ---------- First sign-in ----------

-- What the app needs on sign-in: the user's profile if they have one, whether there are unclaimed profiles
-- they could claim instead of starting fresh, and where they stand with wrong claim codes.
create or replace function public.onboarding_state()
returns jsonb language plpgsql stable security definer set search_path = public as $$
declare
    a public.claim_attempts;
begin
    if auth.uid() is null then
        raise exception 'Sign in first.' using errcode = '28000';
    end if;
    select * into a from public.claim_attempts where user_id = auth.uid();
    return jsonb_build_object(
        'profile_id', public.my_profile_id(),
        'can_claim', exists (select 1 from public.player_profiles where user_id is null),
        'blocked', a.blocked_at is not null,
        'locked_until', case when a.locked_until > now() then a.locked_until end,
        'attempts_left', public.claim_attempts_left(a));
end $$;

-- How many wrong codes the user can enter before the next lockout or block.
create or replace function public.claim_attempts_left(a public.claim_attempts)
returns int language sql immutable set search_path = public as $$
    select greatest(0, least(3 - coalesce(a.failed_streak, 0), 10 - coalesce(a.failed_total, 0)));
$$;

-- Default name for a new profile: the name the sign-in provider gave us, else the start of the email.
create or replace function public.provider_display_name()
returns text language sql stable set search_path = public as $$
    select left(coalesce(
        nullif(trim(auth.jwt() -> 'user_metadata' ->> 'full_name'), ''),
        nullif(trim(auth.jwt() -> 'user_metadata' ->> 'name'), ''),
        nullif(split_part(auth.jwt() ->> 'email', '@', 1), ''),
        'Player'), 60);
$$;

-- Makes the signed-in user's own profile.
create or replace function public.create_my_profile(profile_name text default null)
returns public.player_profiles language plpgsql security definer set search_path = public as $$
declare
    p public.player_profiles;
begin
    if auth.uid() is null then
        raise exception 'Sign in first.' using errcode = '28000';
    end if;
    if exists (select 1 from public.claim_attempts where user_id = auth.uid() and blocked_at is not null) then
        raise exception 'This account is blocked.' using errcode = '42501';
    end if;
    if public.my_profile_id() is not null then
        raise exception 'You already have a player profile.' using errcode = '23505';
    end if;
    insert into public.player_profiles (name, user_id, created_by)
        values (coalesce(nullif(left(trim(profile_name), 60), ''), public.provider_display_name()), auth.uid(), auth.uid())
        returning * into p;
    return p;
end $$;

-- Links an unclaimed profile to the signed-in user, using the profile's claim code.
-- Wrong codes are rate limited: 3 in a row lock the account out for 15 minutes; 10 in all block it, which
-- also bans the sign-in account so it can't come back with the same Google/Apple login.
-- Returns { status: claimed | wrong_code | locked | blocked, profile_id, attempts_left, locked_until }.
-- Wrong codes return a status rather than raising, so the attempt is still counted (raising would roll it back).
create or replace function public.claim_profile(code text)
returns jsonb language plpgsql security definer set search_path = public as $$
declare
    uid uuid := auth.uid();
    a   public.claim_attempts;
    p   public.player_profiles;
begin
    if uid is null then
        raise exception 'Sign in first.' using errcode = '28000';
    end if;
    if public.my_profile_id() is not null then
        raise exception 'You already have a player profile.' using errcode = '23505';
    end if;

    insert into public.claim_attempts (user_id) values (uid) on conflict (user_id) do nothing;
    select * into a from public.claim_attempts where user_id = uid for update;

    if a.blocked_at is not null then
        return jsonb_build_object('status', 'blocked', 'attempts_left', 0);
    end if;
    if a.locked_until > now() then
        return jsonb_build_object('status', 'locked', 'locked_until', a.locked_until, 'attempts_left', 0);
    end if;

    select * into p from public.player_profiles
        where claim_code = public.normalize_code(code) and user_id is null
        for update;

    if found then
        update public.player_profiles set user_id = uid, claim_code = null where id = p.id;
        update public.claim_attempts set failed_streak = 0, locked_until = null, updated_at = now() where user_id = uid;
        return jsonb_build_object('status', 'claimed', 'profile_id', p.id);
    end if;

    a.failed_total := a.failed_total + 1;
    a.failed_streak := a.failed_streak + 1;
    a.locked_until := null;

    if a.failed_total >= 10 then
        update public.claim_attempts
            set failed_total = a.failed_total, failed_streak = a.failed_streak, blocked_at = now(), updated_at = now()
            where user_id = uid;
        perform public.ban_user(uid);
        return jsonb_build_object('status', 'blocked', 'attempts_left', 0);
    end if;

    if a.failed_streak >= 3 then
        a.locked_until := now() + interval '15 minutes';
        a.failed_streak := 0;
    end if;
    update public.claim_attempts
        set failed_total = a.failed_total, failed_streak = a.failed_streak, locked_until = a.locked_until, updated_at = now()
        where user_id = uid;

    if a.locked_until is not null then
        return jsonb_build_object('status', 'locked', 'locked_until', a.locked_until, 'attempts_left', 0);
    end if;
    return jsonb_build_object('status', 'wrong_code', 'attempts_left', public.claim_attempts_left(a));
end $$;

-- Bans the sign-in account in Supabase Auth, so the same Google/Apple login can't sign in again.
-- If the project doesn't allow this, the account is still blocked here: claim_attempts.blocked_at stops
-- it from ever making or claiming a profile, and without a profile it can't see or change anything.
create or replace function public.ban_user(uid uuid)
returns void language plpgsql security definer set search_path = public as $$
begin
    update auth.users set banned_until = now() + interval '100 years' where id = uid;
exception when others then
    raise warning 'Could not ban user %: %', uid, sqlerrm;
end $$;

-- The claim code for an unclaimed profile you manage, made on first ask. Pass reset to replace it.
create or replace function public.profile_claim_code(profile uuid, reset boolean default false)
returns text language plpgsql security definer set search_path = public as $$
declare
    p public.player_profiles;
begin
    select * into p from public.player_profiles where id = profile for update;
    if not found or not public.can_manage_profile(profile) then
        raise exception 'Only the person who made this profile can see its invite code.' using errcode = '42501';
    end if;
    if p.user_id is not null then
        raise exception 'This profile has already been claimed.' using errcode = '22023';
    end if;
    if p.claim_code is null or reset then
        update public.player_profiles set claim_code = public.random_code(8) where id = profile returning * into p;
    end if;
    return p.claim_code;
end $$;

-- ---------- Profiles and playgroups ----------

-- Makes an unclaimed profile, optionally straight into a playgroup (admin only).
create or replace function public.create_profile(profile_name text, group_id uuid default null)
returns public.player_profiles language plpgsql security definer set search_path = public as $$
declare
    p public.player_profiles;
begin
    if public.my_profile_id() is null then
        raise exception 'Set up your own profile first.' using errcode = '28000';
    end if;
    if group_id is not null and not public.is_playgroup_admin(group_id) then
        raise exception 'Only the playgroup''s admin can add players to it.' using errcode = '42501';
    end if;
    insert into public.player_profiles (name, created_by) values (trim(profile_name), auth.uid()) returning * into p;
    if group_id is not null then
        insert into public.playgroup_profiles (playgroup_id, profile_id) values (group_id, p.id);
    end if;
    return p;
end $$;

create or replace function public.create_playgroup(group_name text)
returns public.playgroups language plpgsql security definer set search_path = public as $$
declare
    me uuid := public.my_profile_id();
    g  public.playgroups;
begin
    if me is null then
        raise exception 'Set up your own profile first.' using errcode = '28000';
    end if;
    insert into public.playgroups (name, admin_profile_id) values (trim(group_name), me) returning * into g;
    insert into public.playgroup_profiles (playgroup_id, profile_id) values (g.id, me);
    return g;
end $$;

-- Adds the signed-in user's profile to the playgroup with that join code.
create or replace function public.join_playgroup(code text)
returns public.playgroups language plpgsql security definer set search_path = public as $$
declare
    me uuid := public.my_profile_id();
    g  public.playgroups;
begin
    if me is null then
        raise exception 'Set up your own profile first.' using errcode = '28000';
    end if;
    select * into g from public.playgroups where join_code = public.normalize_code(code);
    if not found then
        raise exception 'No playgroup has that join code.' using errcode = 'P0002';
    end if;
    insert into public.playgroup_profiles (playgroup_id, profile_id) values (g.id, me) on conflict do nothing;
    return g;
end $$;

-- Admin: adds someone's profile to the playgroup using their player code.
create or replace function public.add_profile_to_playgroup(group_id uuid, code text)
returns public.player_profiles language plpgsql security definer set search_path = public as $$
declare
    p public.player_profiles;
begin
    if not public.is_playgroup_admin(group_id) then
        raise exception 'Only the playgroup''s admin can add players to it.' using errcode = '42501';
    end if;
    select * into p from public.player_profiles where player_code = public.normalize_code(code);
    if not found then
        raise exception 'No player has that player code.' using errcode = 'P0002';
    end if;
    insert into public.playgroup_profiles (playgroup_id, profile_id) values (group_id, p.id) on conflict do nothing;
    p.claim_code := null; -- never hand the claim code to someone who doesn't manage the profile
    return p;
end $$;

-- Admin: hands the playgroup to another signed-in member.
create or replace function public.transfer_playgroup(group_id uuid, new_admin uuid)
returns public.playgroups language plpgsql security definer set search_path = public as $$
declare
    g public.playgroups;
begin
    if not public.is_playgroup_admin(group_id) then
        raise exception 'Only the playgroup''s admin can hand it over.' using errcode = '42501';
    end if;
    if not exists (
        select 1 from public.playgroup_profiles gp
        join public.player_profiles p on p.id = gp.profile_id
        where gp.playgroup_id = group_id and gp.profile_id = new_admin and p.user_id is not null
    ) then
        raise exception 'The new admin has to be in the playgroup and signed in.' using errcode = '22023';
    end if;
    update public.playgroups set admin_profile_id = new_admin where id = group_id returning * into g;
    return g;
end $$;

-- Admin: issues a new join code if the old one got out.
create or replace function public.reset_join_code(group_id uuid)
returns text language plpgsql security definer set search_path = public as $$
declare
    new_code text := public.random_code(8);
begin
    if not public.is_playgroup_admin(group_id) then
        raise exception 'Only the playgroup''s admin can reset its join code.' using errcode = '42501';
    end if;
    update public.playgroups set join_code = new_code where id = group_id;
    return new_code;
end $$;

-- ---------- Grants ----------
-- The anon role gets nothing: every table requires a signed-in user.

revoke all on function public.random_code(int) from public, anon, authenticated;
revoke all on function public.ban_user(uuid) from public, anon, authenticated;
revoke all on function public.claim_attempts_left(public.claim_attempts) from public, anon;
revoke all on function public.provider_display_name() from public, anon;

do $$
declare
    f text;
begin
    foreach f in array array[
        'public.my_profile_id()', 'public.is_playgroup_member(uuid)', 'public.is_playgroup_admin(uuid)',
        'public.can_manage_profile(uuid)', 'public.can_see_profile(uuid)', 'public.onboarding_state()',
        'public.create_my_profile(text)', 'public.claim_profile(text)', 'public.profile_claim_code(uuid, boolean)',
        'public.create_profile(text, uuid)', 'public.create_playgroup(text)', 'public.join_playgroup(text)',
        'public.add_profile_to_playgroup(uuid, text)', 'public.transfer_playgroup(uuid, uuid)',
        'public.reset_join_code(uuid)']
    loop
        execute format('revoke all on function %s from public, anon', f);
        execute format('grant execute on function %s to authenticated', f);
    end loop;
end $$;

revoke all on public.player_profiles, public.playgroups, public.playgroup_profiles, public.decks,
    public.game_records, public.claim_attempts from anon, authenticated;

-- Profiles: claim_code is left out of the readable columns on purpose; its admin reads it with profile_claim_code().
grant select (id, name, user_id, created_by, player_code, collection, created_at, updated_at) on public.player_profiles to authenticated;
grant update (name, collection) on public.player_profiles to authenticated;
grant delete on public.player_profiles to authenticated;

grant select, delete on public.playgroups to authenticated;
grant update (name) on public.playgroups to authenticated;

grant select, delete on public.playgroup_profiles to authenticated;

grant select, insert, update, delete on public.decks, public.game_records to authenticated;
