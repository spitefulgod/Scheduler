# Scheduler

A configurable, rule based scheduling platform. A **profile** sets up its own entities, setup data and rules, so the same
engine can schedule factory presses or any other process. The solver is Google OR-Tools CP-SAT. See
[CLAUDE.md](CLAUDE.md) for the full design and [docs/Design.md](docs/Design.md) for how rules become a plan, how the
press shop is set up, a stress test against harder production setups, and the current limits.

| Part | Tech | Folder |
| --- | --- | --- |
| Database | SQL Server scripts, stored procedures returning JSON | `Database/` |
| API | ASP.NET Core, .NET 10, `SQLConnector` and stored procedures | `api/Scheduler.Api` |
| Solver | OR-Tools CP-SAT | `api/Scheduler.Solver` |
| Tests | xUnit, against a `Scheduler_Test` database built from the scripts | `api/Scheduler.Api.Tests` |
| Web | Angular 21, standalone components, signals, SCSS | `web/` |

## Milestone status

| # | Milestone | Status |
| --- | --- | --- |
| 1 | Solution scaffold, `SchedulerMeta` database, ribbon shell with navigator and properties | Done |
| 2 | Connections, source browser and preview (read only, with tests) | Done |
| 3 | Entities, attributes, roles, column mapping, duration formula | Done |
| 4 | Value scales, matrix and list editors, calendars | Done |
| 5 | Rule block catalogue, rule builder, validation | Done, 16 blocks |
| 6 | Solver, Gantt, infeasibility explanation, locks, what-if, emergency, publish | Done |
| 7 | Second profile through configuration only | A job shop set up only through rules is in the tests, no seed yet |

## Database

`Database\Database.cmd` builds the `SchedulerMeta` database on a server, or brings an existing one up to date. It needs
`sqlcmd`. Every id is a `VARCHAR(50)` filled with `CAST(NEWID() AS VARCHAR(50))`, apart from the small lookup tables.

```
Database.cmd <server> [database] [/user <login>] [/demodata]

Database.cmd PLEXSERVER SchedulerMeta /demodata      development, with the demo press data and the Press Scheduling profile
Database.cmd sql01 SchedulerMeta /user scheduler     another server with a SQL Server login, Windows sign in without /user
```

- **A new database** is built with `RunAll.sql` (tables, functions, procedures and the built in lookup data), and every
  script in `Updates` is recorded as run because the build scripts are always kept current.
- **An existing database** gets any `Updates` scripts it hasn't had, in number order, each recorded in `SchemaVersions`.
  The functions, procedures and lookup data are then put in again, they're all `CREATE OR ALTER` or `MERGE`.
- **`/demodata`** runs `RunDemo.sql`: `08_DemoSource.sql` makes the `src` schema with made up press shop data (10 presses,
  30 tools, 8 compounds, 150 jobs) and `09_Seed_PressScheduling.sql` sets up the Press Scheduling profile on it, only
  when there isn't one already.

Every procedure returns a single `results` column of JSON (`FOR JSON`), with errors as `{ "errorMessage", "errorCode" }`
and a return value of 0.

### Changing the database

Every change to a table or its data is made in two places:

1. **The build script**, e.g. a new column in its `CREATE TABLE` in `01_Tables.sql`, so a new database is built as it is now.
2. **A new update script** in `Updates`, numbered one higher than the last, e.g. `0002_AddCalendarColour.sql`, safe to
   run twice. Never change an update once it has been run anywhere, add another one instead.

| Script | |
| --- | --- |
| `01_Tables.sql` | Profiles, Connections, Sources, EntityTypes, Attributes, ValueScales, Relationships, Calendars, RuleTypes, Rules, SolveRuns |
| `02_Functions.sql` | `GetProfileJson`, `GetConnectionJson` (never the password), `GetSourceJson` |
| `03` to `06` | Procedures for profiles and the navigator, connections, sources, the catalogue and status |
| `07_Seed_Catalogue.sql` | Connection types, the rule block catalogue with each block's parameters, the Boolean, Graded and Number scales |
| `10_Procedures_Model.sql` | Entities and attributes, relationships and their values, value scales, calendars, rules, settings, `GetProfileModel` |
| `11_Procedures_Schedule.sql` | Solve runs, results, the published plan, publish and discard, plan locks |
| `Updates/0002_PlansAndLocks.sql` | Profile settings, richer runs and assignments, `PlanLocks` |

## API

`appsettings.json` has the database and the key the connection passwords are encrypted with:

```
"SchedulerDBConn": "Data Source=PLEXSERVER;Initial Catalog=SchedulerMeta;Integrated Security=True;TrustServerCertificate=True;"
"Sources": { "Key": "<base64 of 32 random bytes>" }
```

Keep the key out of the repo, each server has its own and keeps it, e.g.
`dotnet user-secrets set "Sources:Key" "<key>"`. A password saved with one key can't be read with another.

| | |
| --- | --- |
| `GET/POST/PUT /api/profiles`, `GET /api/profiles/{id}/navigator` | Profiles and the navigator tree |
| `GET/POST/PUT/DELETE /api/connections` | Connections, the password is never sent back, only `hasPassword` |
| `POST /api/connections/test`, `POST /api/connections/{id}/test` | Test what's in the wizard, or a saved connection |
| `GET /api/connections/{id}/objects`, `.../objects/columns`, `.../objects/preview` | Tables and views, columns, first 200 rows |
| `GET/POST/PUT/DELETE /api/profiles/{id}/sources`, `.../refresh`, `.../preview` | A profile's sources |
| `GET /api/rule-types`, `/api/value-scales`, `/api/status` | Catalogue and status bar |
| `.../entities`, `PUT .../entities/{id}/attributes`, `GET .../entities/{id}/rows` | Entities, their mapped attributes, rows as the rules see them |
| `.../relationships`, `GET .../{id}/grid`, `PUT .../{id}/values`, `POST .../{id}/import` | Matrices and lists, live from the source with typed in values on top |
| `.../calendars`, `.../rules`, `POST .../rules/check`, `.../value-scales` | Calendars, rules, a check of the rules on the data without solving |
| `POST .../schedule/solve` | Starts a Solve, WhatIf (with `pins`) or Emergency (with `downs` and `alternatives`) run in the background |
| `GET .../schedule/runs`, `.../runs/{id}`, `.../plan` | Runs, one with its assignments and changes against the plan, the published plan |
| `POST .../runs/{id}/stop`, `.../publish`, `.../discard` | Stop with the best so far, make it the plan, throw it away |
| `GET/POST/DELETE .../schedule/locks`, `PUT .../schedule/settings` | Window and item locks, the profile's solve settings |

`SCHEDULER_SOLVER_LOG=1` in the API's environment writes CP-SAT's search log to the console, for tuning.

## Run it

Prerequisites: .NET 10 SDK, Node 22+, `sqlcmd`, and SQL Server on PLEXSERVER.

```
Database\Database.cmd PLEXSERVER SchedulerMeta /demodata

cd api
dotnet run --project Scheduler.Api        # http://localhost:5080

cd web
npm install
npm start                                 # http://localhost:4200, calls the API through CORS
```

## Build and test

```
cd api && dotnet build && dotnet test     # builds a Scheduler_Test database, runs the tests, then drops it
cd web && npx ng build && npx ng test --watch=false
```

The tests sign in to `localhost` with Windows. Another server or a SQL Server login is set with `SCHEDULER_TEST_SERVER`,
`SCHEDULER_TEST_USER` and `SCHEDULER_TEST_PASSWORD`.

## Safety rules (enforced in code)

- **Sources are only ever read.** `ReadOnlySqlGuard` parses every statement with ScriptDom and turns down anything but
  one `SELECT` over the tables and views the connection has: DML, DDL, `EXEC`, `SELECT INTO`, more than one statement,
  `OPENROWSET`, `OPENQUERY`, `OPENDATASOURCE`, linked servers, other databases, table valued functions and
  `NEXT VALUE FOR`. What's allowed runs in a `READ UNCOMMITTED` transaction that's always rolled back, with
  `ApplicationIntent=ReadOnly`. Use a `db_datareader` login for sources.
- **Writes only go to SchedulerMeta**, through its stored procedures.
- **Passwords are encrypted** with AES-GCM using `Sources:Key` and never sent back, the API gives `hasPassword` instead.
- **The demo connection only sees the `src` schema**, never the scheduler's own tables.
