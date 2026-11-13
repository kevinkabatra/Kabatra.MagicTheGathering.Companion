-- MTG Companion: shared decks, player profiles and play history for playgroups.
-- Run this once in your Supabase project's SQL Editor (Dashboard -> SQL Editor -> New query).
-- It is safe to run again: it only creates what is missing and replaces the functions and policies.

-- ---------- Tables ----------

create table if not exists public.playgroups (
    id          uuid primary key default gen_random_uuid(),
    name        text not null check (char_length(name) between 1 and 80),
    invite_code text not null unique default upper(substr(md5(gen_random_uuid()::text), 1, 8)),
    created_by  uuid references auth.users (id) on delete set null default auth.uid(),
    created_at  timestamptz not null default now()
);

create table if not exists public.playgroup_members (
    playgroup_id uuid not null references public.playgroups (id) on delete cascade,
    user_id      uuid not null references auth.users (id) on delete cascade default auth.uid(),
    display_name text check (char_length(display_name) <= 60),
    joined_at    timestamptz not null default now(),
    primary key (playgroup_id, user_id)
);

create table if not exists public.decks (
    id              uuid primary key default gen_random_uuid(),
    playgroup_id    uuid not null references public.playgroups (id) on delete cascade,
    name            text not null check (char_length(name) between 1 and 120),
    owner_name      text,
    format          text not null default 'commander',
    notes           text,
    cover_image_url text,
    -- [{ "name", "quantity", "section": "main"|"sideboard"|"commander", "scryfall_id", "set_code", "collector_number" }]
    cards           jsonb not null default '[]'::jsonb check (jsonb_typeof(cards) = 'array'),
    created_by      uuid references auth.users (id) on delete set null default auth.uid(),
    updated_by      uuid references auth.users (id) on delete set null default auth.uid(),
    created_at      timestamptz not null default now(),
    updated_at      timestamptz not null default now()
);

-- Player profiles: who's in the playgroup, and the cards they own. Not tied to sign-in accounts,
-- so friends without one can still have a profile.
create table if not exists public.player_profiles (
    id           uuid primary key default gen_random_uuid(),
    playgroup_id uuid not null references public.playgroups (id) on delete cascade,
    name         text not null check (char_length(name) between 1 and 60),
    -- Same shape as decks.cards; "section" is ignored.
    collection   jsonb not null default '[]'::jsonb check (jsonb_typeof(collection) = 'array'),
    created_by   uuid references auth.users (id) on delete set null default auth.uid(),
    updated_by   uuid references auth.users (id) on delete set null default auth.uid(),
    created_at   timestamptz not null default now(),
    updated_at   timestamptz not null default now()
);

-- Which profile owns a deck. Anyone can still play it. Added separately so existing projects pick it up.
alter table public.decks add column if not exists owner_profile_id uuid references public.player_profiles (id) on delete set null;

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

create index if not exists decks_playgroup_id_idx on public.decks (playgroup_id);
create index if not exists player_profiles_playgroup_id_idx on public.player_profiles (playgroup_id);
create index if not exists game_records_playgroup_ended_idx on public.game_records (playgroup_id, ended_at desc);
create index if not exists playgroup_members_user_id_idx on public.playgroup_members (user_id);

-- Keep updated_at / updated_by current on every edit.
create or replace function public.touch_deck()
returns trigger language plpgsql as $$
begin
    new.updated_at := now();
    new.updated_by := auth.uid();
    return new;
end $$;

drop trigger if exists decks_touch on public.decks;
create trigger decks_touch before update on public.decks
    for each row execute function public.touch_deck();

drop trigger if exists player_profiles_touch on public.player_profiles;
create trigger player_profiles_touch before update on public.player_profiles
    for each row execute function public.touch_deck();

-- ---------- Membership check ----------
-- SECURITY DEFINER so policies can call it without tripping row-level security on playgroup_members itself.

create or replace function public.is_playgroup_member(group_id uuid)
returns boolean language sql stable security definer set search_path = public as $$
    select exists (
        select 1 from public.playgroup_members
        where playgroup_id = group_id and user_id = auth.uid()
    );
$$;

-- ---------- Row-level security ----------

alter table public.playgroups enable row level security;
alter table public.playgroup_members enable row level security;
alter table public.decks enable row level security;
alter table public.player_profiles enable row level security;
alter table public.game_records enable row level security;

-- Playgroups: members can see their groups; only the creator can rename or delete one.
-- New groups are made with create_playgroup() so the creator is added as a member at the same time.
drop policy if exists "members read playgroups" on public.playgroups;
create policy "members read playgroups" on public.playgroups
    for select to authenticated using (public.is_playgroup_member(id));

drop policy if exists "creator updates playgroup" on public.playgroups;
create policy "creator updates playgroup" on public.playgroups
    for update to authenticated using (created_by = auth.uid()) with check (created_by = auth.uid());

drop policy if exists "creator deletes playgroup" on public.playgroups;
create policy "creator deletes playgroup" on public.playgroups
    for delete to authenticated using (created_by = auth.uid());

-- Members: you can see who else is in your groups, change your own display name, and leave.
-- Joining goes through join_playgroup(), which checks the invite code.
drop policy if exists "members read members" on public.playgroup_members;
create policy "members read members" on public.playgroup_members
    for select to authenticated using (public.is_playgroup_member(playgroup_id));

drop policy if exists "member updates self" on public.playgroup_members;
create policy "member updates self" on public.playgroup_members
    for update to authenticated using (user_id = auth.uid()) with check (user_id = auth.uid());

drop policy if exists "member leaves" on public.playgroup_members;
create policy "member leaves" on public.playgroup_members
    for delete to authenticated using (user_id = auth.uid());

-- Decks: anyone in the group can read, add, edit, and delete the group's decks.
drop policy if exists "members read decks" on public.decks;
create policy "members read decks" on public.decks
    for select to authenticated using (public.is_playgroup_member(playgroup_id));

drop policy if exists "members add decks" on public.decks;
create policy "members add decks" on public.decks
    for insert to authenticated with check (public.is_playgroup_member(playgroup_id));

drop policy if exists "members edit decks" on public.decks;
create policy "members edit decks" on public.decks
    for update to authenticated
    using (public.is_playgroup_member(playgroup_id))
    with check (public.is_playgroup_member(playgroup_id));

drop policy if exists "members delete decks" on public.decks;
create policy "members delete decks" on public.decks
    for delete to authenticated using (public.is_playgroup_member(playgroup_id));

-- Profiles and play history: same as decks, anyone in the group can read, add, edit, and delete.
drop policy if exists "members read profiles" on public.player_profiles;
create policy "members read profiles" on public.player_profiles
    for select to authenticated using (public.is_playgroup_member(playgroup_id));

drop policy if exists "members add profiles" on public.player_profiles;
create policy "members add profiles" on public.player_profiles
    for insert to authenticated with check (public.is_playgroup_member(playgroup_id));

drop policy if exists "members edit profiles" on public.player_profiles;
create policy "members edit profiles" on public.player_profiles
    for update to authenticated
    using (public.is_playgroup_member(playgroup_id))
    with check (public.is_playgroup_member(playgroup_id));

drop policy if exists "members delete profiles" on public.player_profiles;
create policy "members delete profiles" on public.player_profiles
    for delete to authenticated using (public.is_playgroup_member(playgroup_id));

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

-- ---------- Functions the app calls ----------

create or replace function public.create_playgroup(group_name text, display_name text)
returns public.playgroups language plpgsql security definer set search_path = public as $$
declare
    g public.playgroups;
begin
    if auth.uid() is null then
        raise exception 'Sign in first.' using errcode = '28000';
    end if;
    insert into public.playgroups (name, created_by) values (trim(group_name), auth.uid()) returning * into g;
    insert into public.playgroup_members (playgroup_id, user_id, display_name)
        values (g.id, auth.uid(), nullif(trim(display_name), ''));
    return g;
end $$;

create or replace function public.join_playgroup(code text, display_name text)
returns public.playgroups language plpgsql security definer set search_path = public as $$
declare
    g public.playgroups;
begin
    if auth.uid() is null then
        raise exception 'Sign in first.' using errcode = '28000';
    end if;
    select * into g from public.playgroups where invite_code = upper(trim(code));
    if not found then
        raise exception 'No playgroup has that invite code.' using errcode = 'P0002';
    end if;
    insert into public.playgroup_members (playgroup_id, user_id, display_name)
        values (g.id, auth.uid(), nullif(trim(display_name), ''))
        on conflict (playgroup_id, user_id) do update set display_name = coalesce(excluded.display_name, playgroup_members.display_name);
    return g;
end $$;

-- Lets the group's creator issue a new invite code if the old one got out.
create or replace function public.reset_invite_code(group_id uuid)
returns text language plpgsql security definer set search_path = public as $$
declare
    new_code text := upper(substr(md5(gen_random_uuid()::text), 1, 8));
begin
    update public.playgroups set invite_code = new_code
        where id = group_id and created_by = auth.uid();
    if not found then
        raise exception 'Only the person who created the playgroup can reset its invite code.' using errcode = '42501';
    end if;
    return new_code;
end $$;

revoke all on function public.create_playgroup(text, text) from public, anon;
revoke all on function public.join_playgroup(text, text) from public, anon;
revoke all on function public.reset_invite_code(uuid) from public, anon;
revoke all on function public.is_playgroup_member(uuid) from public, anon;
grant execute on function public.create_playgroup(text, text) to authenticated;
grant execute on function public.join_playgroup(text, text) to authenticated;
grant execute on function public.reset_invite_code(uuid) to authenticated;
grant execute on function public.is_playgroup_member(uuid) to authenticated;

-- The anon role gets nothing: every table requires a signed-in member.
revoke all on public.playgroups, public.playgroup_members, public.decks, public.player_profiles, public.game_records from anon;
grant select, insert, update, delete on public.playgroups, public.playgroup_members, public.decks, public.player_profiles, public.game_records to authenticated;
