-- Tests for the billing part of supabase/schema.sql. CI runs them (see .github/workflows/ci.yml) after
-- tests/supabase-stub.sql and schema.sql. Everything runs in one transaction that's rolled back at the end.
-- A failed assert stops psql with an error.

\set ON_ERROR_STOP on
begin;

-- Acting as a signed-in user: the API role plus the JWT's subject, as Supabase sets them for each request.
create function pg_temp.act_as(uid uuid) returns void language plpgsql as $$
begin
    perform set_config('role', 'authenticated', true);
    perform set_config('request.jwt.claim.sub', uid::text, true);
end $$;
create function pg_temp.act_as_admin() returns void language plpgsql as $$
begin
    perform set_config('role', 'postgres', true);
    perform set_config('request.jwt.claim.sub', '', true);
end $$;

insert into auth.users (id, email) values
    ('00000000-0000-0000-0000-00000000000a', 'paid@example.test'),
    ('00000000-0000-0000-0000-00000000000b', 'other@example.test'),
    ('00000000-0000-0000-0000-00000000000c', 'never@example.test'),
    ('00000000-0000-0000-0000-00000000000d', 'incomplete@example.test');

-- ---------- subscription_grants_access: the "is paid" rule ----------
do $$
declare
    t timestamptz := '2026-11-14 12:00Z';
begin
    assert public.subscription_grants_access('active', t + interval '10 days', t), 'active, in period';
    assert public.subscription_grants_access('trialing', t + interval '1 day', t), 'trialing, in period';
    assert public.subscription_grants_access('past_due', t + interval '1 day', t), 'past_due, in period';
    assert public.subscription_grants_access('active', t - interval '2 days', t), 'active, inside the grace period';
    assert not public.subscription_grants_access('active', t - interval '3 days', t), 'active, grace period over';
    assert not public.subscription_grants_access('active', t - interval '30 days', t), 'active, long expired';
    assert not public.subscription_grants_access('canceled', t + interval '10 days', t), 'canceled grants nothing';
    assert not public.subscription_grants_access('expired', t + interval '10 days', t), 'expired grants nothing';
    assert not public.subscription_grants_access('incomplete', t + interval '10 days', t), 'incomplete grants nothing';
    assert not public.subscription_grants_access('active', null, t), 'no period end grants nothing';
    assert not public.subscription_grants_access(null, t + interval '10 days', t), 'no status grants nothing';
    assert public.billing_grace_period() = interval '3 days', 'grace period is 3 days';
end $$;

-- ---------- Fixtures (written as the service role would) ----------
insert into public.billing_customers (user_id, provider, provider_customer_id) values
    ('00000000-0000-0000-0000-00000000000a', 'stripe', 'cus_paid'),
    ('00000000-0000-0000-0000-00000000000b', 'stripe', 'cus_other');

insert into public.subscriptions
    (user_id, provider, store, provider_subscription_id, plan, entitlement, status, trial_ends_at, current_period_ends_at, cancel_at_period_end)
values
    -- Paid user: an old expired web subscription, a past-due App Store one, and an active annual web one.
    ('00000000-0000-0000-0000-00000000000a', 'stripe', 'web', 'sub_old', 'premium_monthly', 'premium', 'expired',
        now() - interval '400 days', now() - interval '300 days', false),
    ('00000000-0000-0000-0000-00000000000a', 'revenuecat', 'app_store', 'rc_1', 'premium_monthly', 'premium', 'past_due',
        null, now() + interval '2 days', false),
    ('00000000-0000-0000-0000-00000000000a', 'stripe', 'web', 'sub_annual', 'premium_annual', 'premium', 'active',
        null, now() + interval '200 days', true),
    -- Other user: a cancelled subscription whose period hasn't ended (cancelled immediately, so no access).
    ('00000000-0000-0000-0000-00000000000b', 'stripe', 'web', 'sub_b', 'premium_monthly', 'premium', 'canceled',
        null, now() + interval '20 days', false),
    -- A checkout that never went through.
    ('00000000-0000-0000-0000-00000000000d', 'stripe', 'web', 'sub_d', 'premium_monthly', 'premium', 'incomplete',
        null, null, false);

-- ---------- Constraints ----------
do $$
begin
    begin
        insert into public.subscriptions (user_id, provider, store, provider_subscription_id, plan, entitlement, status)
            values ('00000000-0000-0000-0000-00000000000c', 'stripe', 'web', 'sub_annual', 'premium_monthly', 'premium', 'active');
        assert false, 'a provider subscription id is unique per provider';
    exception when unique_violation then null;
    end;
    begin
        insert into public.subscriptions (user_id, provider, store, provider_subscription_id, plan, entitlement, status)
            values ('00000000-0000-0000-0000-00000000000c', 'stripe', 'web', 'sub_x', 'premium_monthly', 'premium', 'unpaid');
        assert false, 'statuses are normalized';
    exception when check_violation then null;
    end;
    begin
        insert into public.subscriptions (user_id, provider, store, provider_subscription_id, plan, entitlement, status)
            values ('00000000-0000-0000-0000-00000000000c', 'stripe', 'desktop', 'sub_y', 'premium_monthly', 'premium', 'active');
        assert false, 'store is web, app_store or play_store';
    exception when check_violation then null;
    end;
    begin
        insert into public.billing_customers (user_id, provider, provider_customer_id)
            values ('00000000-0000-0000-0000-00000000000c', 'Stripe', 'cus_c');
        assert false, 'provider keys are lower_snake_case';
    exception when check_violation then null;
    end;
    begin
        insert into public.billing_customers (user_id, provider, provider_customer_id)
            values ('00000000-0000-0000-0000-00000000000c', 'stripe', 'cus_paid');
        assert false, 'a provider customer belongs to one user';
    exception when unique_violation then null;
    end;
    insert into public.billing_events (provider, event_id, type) values ('stripe', 'evt_1', 'invoice.paid');
    begin
        insert into public.billing_events (provider, event_id, type) values ('stripe', 'evt_1', 'invoice.paid');
        assert false, 'a redelivered event is caught by the primary key';
    exception when unique_violation then null;
    end;
end $$;

-- updated_at moves on update; created_at doesn't.
do $$
declare
    before_row public.subscriptions;
    after_row  public.subscriptions;
begin
    update public.subscriptions set created_at = '2000-01-01', updated_at = '2000-01-01' where provider_subscription_id = 'sub_b';
    select * into before_row from public.subscriptions where provider_subscription_id = 'sub_b';
    update public.subscriptions set cancel_at_period_end = true, created_at = now() where provider_subscription_id = 'sub_b';
    select * into after_row from public.subscriptions where provider_subscription_id = 'sub_b';
    assert after_row.created_at = before_row.created_at, 'created_at is kept';
    assert after_row.updated_at > '2000-01-02', 'updated_at is touched';
end $$;

-- ---------- As the paid user ----------
select pg_temp.act_as('00000000-0000-0000-0000-00000000000a');
do $$
declare
    e record;
    n int;
begin
    assert public.has_entitlement('premium'), 'paid user has premium';
    assert not public.has_entitlement('something_else'), 'only the entitlements they bought';
    assert public.has_had_trial(), 'paid user has had a subscription';

    select count(*) into n from public.my_entitlements();
    assert n = 1, 'one row per entitlement, got ' || n;
    select * into e from public.my_entitlements();
    assert e.entitlement = 'premium' and e.plan = 'premium_annual' and e.status = 'active' and e.store = 'web'
        and e.cancel_at_period_end, 'the active subscription wins over the past-due one: ' || row_to_json(e)::text;

    select count(*) into n from public.subscriptions;
    assert n = 3, 'reads all and only their own subscriptions, got ' || n;
    select count(*) into n from public.billing_customers;
    assert n = 1, 'reads only their own billing customer, got ' || n;
end $$;

-- Writes are refused: users can't grant themselves Premium.
do $$
begin
    begin
        insert into public.subscriptions (user_id, provider, store, provider_subscription_id, plan, entitlement, status, current_period_ends_at)
            values (auth.uid(), 'stripe', 'web', 'sub_free', 'premium_annual', 'premium', 'active', now() + interval '1 year');
        assert false, 'users cannot insert subscriptions';
    exception when insufficient_privilege then null;
    end;
    begin
        update public.subscriptions set current_period_ends_at = now() + interval '10 years';
        assert false, 'users cannot update subscriptions';
    exception when insufficient_privilege then null;
    end;
    begin
        delete from public.subscriptions;
        assert false, 'users cannot delete subscriptions';
    exception when insufficient_privilege then null;
    end;
    begin
        insert into public.billing_customers (user_id, provider, provider_customer_id) values (auth.uid(), 'paddle', 'ctm_1');
        assert false, 'users cannot insert billing customers';
    exception when insufficient_privilege then null;
    end;
    begin
        perform 1 from public.billing_events;
        assert false, 'users cannot read billing events';
    exception when insufficient_privilege then null;
    end;
end $$;

-- ---------- As the user whose subscription was cancelled ----------
select pg_temp.act_as('00000000-0000-0000-0000-00000000000b');
do $$
begin
    assert not public.has_entitlement('premium'), 'cancelled means no premium, even inside the period';
    assert not exists (select 1 from public.my_entitlements()), 'no entitlements listed';
    assert public.has_had_trial(), 'a cancelled subscription still used up the trial';
    assert (select count(*) from public.subscriptions) = 1, 'sees their own cancelled subscription';
end $$;

-- ---------- As a user who never subscribed ----------
select pg_temp.act_as('00000000-0000-0000-0000-00000000000c');
do $$
begin
    assert not public.has_entitlement('premium'), 'never subscribed: no premium';
    assert not public.has_had_trial(), 'never subscribed: trial available';
    assert (select count(*) from public.subscriptions) = 0, 'sees no one else''s subscriptions';
    assert (select count(*) from public.billing_customers) = 0, 'sees no one else''s billing customers';
end $$;

-- ---------- As a user whose checkout never went through ----------
select pg_temp.act_as('00000000-0000-0000-0000-00000000000d');
do $$
begin
    assert not public.has_entitlement('premium'), 'incomplete: no premium';
    assert not public.has_had_trial(), 'incomplete: trial still available';
end $$;

-- ---------- Signed out ----------
select pg_temp.act_as_admin();
select set_config('role', 'anon', true);
do $$
begin
    begin
        perform public.has_entitlement('premium');
        assert false, 'anon cannot call has_entitlement';
    exception when insufficient_privilege then null;
    end;
    begin
        perform 1 from public.subscriptions;
        assert false, 'anon cannot read subscriptions';
    exception when insufficient_privilege then null;
    end;
end $$;

-- ---------- The service role writes ----------
select pg_temp.act_as_admin();
select set_config('role', 'service_role', true);
do $$
begin
    insert into public.subscriptions (user_id, provider, store, provider_subscription_id, plan, entitlement, status, trial_ends_at, current_period_ends_at)
        values ('00000000-0000-0000-0000-00000000000c', 'stripe', 'web', 'sub_c', 'premium_monthly', 'premium', 'trialing',
                now() + interval '7 days', now() + interval '7 days')
        on conflict (provider, provider_subscription_id) do update set status = excluded.status;
    update public.subscriptions set status = 'active' where provider_subscription_id = 'sub_c';
    insert into public.billing_events (provider, event_id, type) values ('stripe', 'evt_2', 'customer.subscription.created');
end $$;

select pg_temp.act_as('00000000-0000-0000-0000-00000000000c');
do $$
begin
    assert public.has_entitlement('premium'), 'premium once the webhook writes the subscription';
    assert public.has_had_trial(), 'and the trial is used';
end $$;

-- Deleting the account removes its billing rows.
select pg_temp.act_as_admin();
delete from auth.users where id = '00000000-0000-0000-0000-00000000000a';
do $$
begin
    assert not exists (select 1 from public.subscriptions where user_id = '00000000-0000-0000-0000-00000000000a'), 'subscriptions cascade';
    assert not exists (select 1 from public.billing_customers where user_id = '00000000-0000-0000-0000-00000000000a'), 'customers cascade';
end $$;

rollback;
\echo 'billing tests passed'
