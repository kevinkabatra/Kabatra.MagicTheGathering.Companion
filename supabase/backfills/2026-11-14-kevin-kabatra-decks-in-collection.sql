-- One-off backfill: mark every deck owned by Kevin Kabatra as already added to his collection.
-- His decks' cards were added with "+ Add its cards to the collection" before decks remembered that, so without
-- this the button would still show and the cards could be added a second time.
--
-- Run supabase/schema.sql first (it adds decks.cards_in_collection), then run this in the Supabase SQL Editor.
-- Safe to run again: it only changes decks that aren't marked yet. The decks' updated_at moves to now and
-- updated_by is cleared (the SQL Editor isn't a signed-in user); nothing else about them changes.

-- 1. Check which profile(s) match. It should be exactly one; if not, narrow the match below before step 2.
select p.id, p.name, p.user_id, count(d.id) as decks
from public.player_profiles p
left join public.decks d on d.owner_profile_id = p.id
where p.name = 'Kevin Kabatra'
group by p.id, p.name, p.user_id;

-- 2. Mark the decks, and list the ones that changed.
update public.decks d
set cards_in_collection = true
from public.player_profiles p
where d.owner_profile_id = p.id
  and p.name = 'Kevin Kabatra'
  and not d.cards_in_collection
returning d.id, d.name;
