# R-028 review: concurrent creation of the server-wide group roles

**Summary for the product owner.** The change fixes the reported failure where it happens during customer set-up:
if two customers are set up on a new server at the same moment, the second one now carries on instead of
failing. It is safe and correct for that case. But the same clash still happens in two older setup scripts that
the automated tests run, so the test runs on a fresh server (what CI uses) can still fail now and then for this
reason. There is an easy fix in the test setup that does not touch the frozen scripts. One of the new tests does
not reliably prove the fix. Real customer databases are not affected by the remaining problem.

## Findings, most severe first

### 1. HIGH: the frozen grants migrations still race, and the test fixtures still run them in parallel on one server
- `src/Platform/Erp.Kernel.Infrastructure/Migrations/V202611150200__kernel__grants.sql:9-14` and
  `src/Modules/Hrm/Erp.Hrm.Infrastructure/Migrations/V202611150101__hrm__grants.sql:9-14` still do
  "if not exists ... then create role" (check, then create). The S1.05 and S1.06 reviews (T6) named these as the
  second source of the race.
- They are run as the superuser, in parallel collections, against the shared `ERP_TEST_PG` server (the CI service
  container is fresh on every run): `tests/Erp.IntegrationTests/Infrastructure/MigratedDatabases.cs:44`,
  `tests/Erp.IntegrationTests/Api/GeneratedEndpointTests.cs:48`, `tests/Erp.IntegrationTests/DataDesign/DataDesignTests.cs:60`.
  Each grants script runs in its own transaction (`SqlScriptRunner.cs:27`), so a second session's insert waits on
  `pg_authid_rolname_index` and then fails with 23505. This can happen between two of these fixtures, or between
  one of them and a provisioning fixture when the migration is the one that loses. Only the provisioning side is
  protected now.
- Production is not affected: tenant migrations run as the owner login after `CreateDatabaseAsync` created the roles,
  so the `if not exists` branch is always skipped.
- **Fix (frozen files and generator unchanged):** in `tests/Erp.IntegrationTests/Infrastructure/PostgresFixture.cs`
  `InitializeAsync` (line 21 onwards), once `ConnectionString` is known, open an admin connection and call
  `ClusterAdministrator.EnsureGroupRolesAsync` (public, and race-safe now). Then every fixture finds the roles
  already there before any grants script runs. Say in the handoff that `DictionaryWriter` (the CodeGen `dictionary`
  command) also runs these scripts, but it runs on its own, one at a time.

### 2. MEDIUM: the race test does not reliably show the fix works
- `tests/Erp.IntegrationTests/Tenancy/GroupRoleRaceTests.cs:16-55` releases 16 sessions together and hopes they
  collide. `CREATE ROLE` takes microseconds, so with the old code this test often fails, but it can still pass.
  The exact path being fixed (waiting on the unique index, then 23505) is never forced. It also never checks that
  any session took the exception path.
- **Fix:** add a test that controls the timing. Connection A runs `begin; create role <r> nologin;` and stays open.
  Connection B starts `EnsureNoLoginRolesAsync(B, [r])`. Wait until B shows as blocked
  (`pg_stat_activity.wait_event_type = 'Lock'` for B's pid). Then commit A and assert that B finishes without an
  error. Add a second case where A rolls back and B creates the role. Keep the 16-session test as a smoke test.

### 3. MEDIUM: an existing role of the same name is accepted whatever it is
- `ClusterAdministrator.cs:89`: duplicate_object is ignored without looking at the role that is already there. The
  old code did the same (`if not exists`), so this is not a regression. But this product is also sold on-premises,
  on servers it may share, and `backup` is a common role name. If a role called `app_rw`, `app_ro`, `app_audit` or
  `backup` already exists with LOGIN, SUPERUSER, CREATEROLE, BYPASSRLS or REPLICATION, or is a member of
  something like `pg_read_all_data`, every customer login is then granted it (`EnsureLoginAsync`, line 117, with
  `inherit`) and quietly gets those powers.
- **Fix:** after the loop, run one parameterized check:
  `select rolname from pg_roles where rolname = any($1) and (rolcanlogin or rolsuper or rolcreaterole or rolcreatedb or rolreplication or rolbypassrls or oid in (select member from pg_auth_members))`.
  Throw a clear error if it returns any rows, naming the role. If the product owner rules this out of scope for R-028,
  record it as a new risk instead (D-39).

### 4. LOW: the identifier is quoted, but it sits inside a dollar-quoted DO body
- `ClusterAdministrator.cs:89`: `Sql.Identifier` escapes `"` but not `$do$`. A name containing `$do$` would end
  the DO body early and inject SQL. Today the names are constants (`TenantLogins.GroupRoles`) and test names, so
  nothing can exploit it now, but `EnsureNoLoginRolesAsync` accepts any `IEnumerable<string>`.
- **Fix:** at the top of the loop, check each name against `^[a-z_][a-z0-9_]{0,62}$` and throw if it does not match.
  Or drop the DO block, run plain `create role "<name>" nologin` and catch `PostgresException` with SqlState
  `42710` or `23505` in C# (the connection has no transaction, so the failed statement aborts nothing else).

### 5. LOW: provisioning the same customer twice at once still races (an older problem, outside R-028)
- `TenantProvisioner.RegisterAsync` resumes when a customer's status is `provisioning`, so two runs for the same
  code both continue. In `ClusterAdministrator.cs:94-119`, both runs can read "no secret" and each store a different
  password, which can leave the secret store and the server login out of step. `EnsureLoginAsync` (check, then
  create, lines 110-114) can fail with 23505, and `CREATE DATABASE` (lines 52-59) with 42P04. Different customers
  never collide: login and database names include the customer code, `template0` allows concurrent use, and member
  grants are separate rows.
- **Fix (separate change):** take `pg_advisory_xact_lock(hashtext('provision:' || code))` in the control-plane
  database for the whole run. One control-plane database serves every provisioning, so an advisory lock works
  there. Otherwise record it as a risk. Also reword the comment at `ClusterAdministrator.cs:79-80`: an advisory lock
  in the control-plane database would serve provisioning, but it would not cover the migration scripts or tools
  connected to other databases.

### 6. LOW: housekeeping
- The risk R-028 is not in `docs/registers/risks-and-changes.md` (the last risk is R-026). Add it and close it,
  pointing to this fix and to S1.05/S1.06 review T6.
- `GroupRoleRaceTests.cs:10` joins `TenancyGroup`, so it waits for the full two-customer fixture, although it only
  needs `AdminConnectionString`. `PostgresTestGroup` (`PostgresFixture`) would be lighter and would run in parallel.
- `GroupRoleRaceTests.cs:57`: the test name says "existing roles are left alone", but it only runs the call twice
  on roles it made itself. Either rename it ("a second run succeeds and roles have no login"), or create a role
  first with a comment or setting and assert that the role is unchanged afterwards.

## Checked and found correct
- **PostgreSQL 16 behaviour.** `CREATE ROLE` (user.c `CreateRole`) raises 42710 `duplicate_object` when a
  committed role with that name is visible. When another session's insert is still in progress, it waits on
  `pg_authid_rolname_index`, then raises 23505 `unique_violation` if that session commits, or carries on and
  creates the role if it rolls back. As superuser, no other conflict is possible: no shared catalogue row is
  updated, so there is no "tuple concurrently updated". A non-superuser CREATEROLE login also inserts its implicit
  admin grant in PG16, inside the same subtransaction. Both codes are caught. Other errors, such as 42501 for a
  missing privilege, still propagate.
- **Exception block in this context.** The DO block runs in an implicit transaction in autocommit mode
  (`Sql.ExecuteAsync(connection, null, ...)`, `Pooling=false`, no `TransactionScope` anywhere in `src`). The
  `exception` clause uses a subtransaction, and `CREATE ROLE` is allowed inside transaction blocks and functions
  (unlike `CREATE DATABASE`). Only the one statement is covered.
- **Quoting.** The old string literal `rolname = '{role}'` is gone. A quoted lower-case name gives the same role as
  before.
- **Frozen files.** No migration file and no generator file is touched, so there is no generator drift.
- **Different customers at once.** Logins, databases and the grants and revokes on each database never collide.
- **Test rules.** The tests use a real PostgreSQL and cite FR-KRN-TEN-02 (release 1.0). They use unique role names
  and drop the roles in `finally`. Connections are disposed. 16 connections fit within CI's `max_connections` of 300.
- **`InternalsVisibleTo Erp.IntegrationTests`.** Acceptable. The test needs names that no server has yet, which the
  public `EnsureGroupRolesAsync` cannot give. It follows the existing `Erp.UnitTests` pattern.
- **Not done.** I did not run psql experiments that create roles, because plan mode allowed only read-only actions.
  A read-only check showed PostgreSQL 16.15, with the four group roles present as NOLOGIN, non-superuser.

## Outcome (2026-11-19)

| # | Finding | Outcome |
| --- | --- | --- |
| 1 | HIGH: frozen grants migrations still race in parallel test fixtures | Fixed: `PostgresFixture.InitializeAsync` creates the group roles with the race-safe method before any fixture migrates; the frozen migrations then find them. No migration or generator change |
| 2 | MEDIUM: race test could pass with the old code | Fixed: new timing-controlled test (another open transaction holds the role; ours must wait, then carry on after commit or create after rollback). Against the old check-then-create it failed 3 of 3 runs (commit case); the 16-session test also failed 5 of 5 |
| 3 | MEDIUM: a same-named existing role with powers is silently reused | Fixed: after creation, a role with login, superuser, create-role, create-database, replication, bypass-RLS or any membership is refused with an operator message; three tests |
| 4 | LOW: name inside `$do$` | Fixed: names must match `^[a-z_][a-z0-9_]{0,62}$`; four tests |
| 5 | LOW: same customer provisioned twice at once | Recorded as risk R-031 (outside R-028) |
| 6 | LOW: housekeeping | R-028 closed in the register; tests moved to the `PostgresTestGroup` collection; second test renamed; the code comment on advisory locks reworded |
