-- Someone Else's Table: one-off wipe for the move to Google/Apple sign-in and account-linked player profiles.
--
-- Run this ONCE in the SQL Editor after the new version is deployed, then run supabase/schema.sql.
-- It permanently deletes every player profile, playgroup, stored deck and recorded game. It can't be undone.
-- Sign-in accounts (auth.users) are left alone; email sign-in is simply no longer offered.

begin;

drop table if exists
    public.game_records,
    public.decks,
    public.playgroup_profiles,
    public.playgroup_members,
    public.playgroups,
    public.player_profiles,
    public.claim_attempts
    cascade;

-- Functions from the old schema whose signatures changed or that are gone.
drop function if exists public.create_playgroup(text, text);
drop function if exists public.join_playgroup(text, text);
drop function if exists public.reset_invite_code(uuid);
drop function if exists public.touch_deck() cascade;

commit;
