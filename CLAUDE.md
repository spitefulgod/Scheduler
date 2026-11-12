# Generic Rule-Based Scheduler — Proof of Concept

## Goal
A configurable scheduling platform where a "schedule profile" defines its own entities,
setup screens and rules, so the same engine can schedule factory presses (jobs, tools,
compounds) or a completely different process (e.g. lengths through a routing) without
custom code. The solver is Google OR-Tools CP-SAT.

First target profile: **press scheduling** (see "Reference profile" below).

## Stack
- **Frontend:** Angular 21 (current LTS), standalone components, signals, SCSS. Angular CDK for
  overlays and drag-drop. No heavy UI kit unless needed; the ribbon is a custom component.
- **Backend:** ASP.NET Core Web API (.NET 10 LTS), C#. SQL Server for SchedulerMeta is hosted on **PlexServer**.
- **Metadata store:** SQL Server database `SchedulerMeta`, built by the scripts in `/Database`
  (`Database.cmd`, `RunAll.sql`, numbered `Updates`). No EF: the API calls stored procedures
  through `SQLConnector`, each returning one `results` column of JSON.
- **Source data access:** ADO.NET (Microsoft.Data.SqlClient) through `SqlServerSource`, read-only,
  against user-configured connections. Every statement is parsed with ScriptDom first.
- **Solver:** `Google.OrTools` NuGet package (CP-SAT) inside the API, in a separate
  `Scheduler.Solver` class library.
- **Coding style:** follow the owner's style from their other projects (see project memory):
  block namespaces and explicit types in C#, `BaseController.RunSP`, PluralPascal tables with
  camelCase columns, `name : type` and `if(` in TypeScript, `fetch` through `callApi`.
- **Solution layout:**
  - `/Database` (table, function, procedure and seed scripts, `Updates`, `Database.cmd`)
  - `/api/Scheduler.Api` (`Controllers/`, `Services/`, `SQLConnector.cs`)
  - `/api/Scheduler.Solver` (`Rules/RuleEngine` turns a profile into a generic `SchedulingProblem`, `CpSatScheduler` builds and solves the CP-SAT model)
  - `/api/Scheduler.Api.Tests` (xUnit, builds a `Scheduler_Test` database from the scripts)
  - `/web` (Angular app)

## Hard safety rules (non-negotiable)
1. **Never write to source databases.** Source connections are used only for `SELECT`.
   Enforce in code: the source query runner rejects anything that is not a single `SELECT`
   statement against a configured table or view, and wraps calls in a read-only
   transaction (`SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED` is fine for preview).
   Recommend users connect with a login that has `db_datareader` only.
2. All writes go to `SchedulerMeta` only (profiles, setup data, rules, results).
3. Connection passwords are encrypted at rest with AES-GCM (`Sources:Key` in config). Never return
   passwords to the frontend; return a masked flag instead.
4. Do not run migrations or create objects on any database other than `SchedulerMeta`.
5. Ask before touching any database you discover that is not in `appsettings.Development.json`.

## Core concepts (metadata model)
- **Profile:** one scheduler configuration (e.g. "Press Scheduling").
- **Connection:** provider (SQL Server first; design for others), server, database, auth
  type, encrypted credentials, test status.
- **DataSource:** connection + table/view (or saved SELECT) + column list cached from
  `INFORMATION_SCHEMA`.
- **EntityType:** e.g. Job, Tool, Press, Compound. Has `Kind`: Item (schedulable),
  Resource, Reference.
- **Attribute:** field on an EntityType with data type and **Role**: Id, Name, Duration,
  Quantity, CycleTime, Capacity, Copies, Size, Category, ReleaseDate, DueDate, Priority,
  ParentItem, Sequence, Custom.
- **Mapping:** EntityType ← DataSource, attribute ← column. Supports a duration formula
  (e.g. `qty * cycle_secs / 60`), evaluated server-side with a safe expression parser
  (NCalc or similar), never raw SQL.
- **ValueScale:** named scale with values, each carrying solver meaning:
  `Forbidden` (hard), `Allowed`, `Penalty(weight)`, or `Numeric`.
  Seed: Boolean (Yes/No), Graded (Preferred 0 / Acceptable 10 / Avoid 100 / Not allowed).
- **Relationship:** EntityType A × EntityType B, ValueScale, default value, symmetric flag,
  preferred editor (`Matrix` or `List`), `Drives` (rule block it feeds). Values stored as
  rows `(RelationshipId, AKey, BKey, Value)`; values can be entered manually or mapped
  from a DataSource.
- **Calendar:** shift patterns, exceptions (date ranges, down/available), inheritance
  (Site → Resource).
- **RuleType (block catalogue)** and **RuleInstance** stored as a JSON block tree:
  Selector (who) + optional Condition (when) + Relation (what) + Strength (hard/soft weight).
- **SolveRun / ScheduleResult:** run settings, status, objective, assignments
  (item, resource(s), start, end), plus infeasibility explanation.

## Rule blocks (compile to CP-SAT)

Blocks compile through the rule engine into a generic `SchedulingProblem` (items with modes, resources with capacity
dimensions, pools, separations, precedences, limits), which `CpSatScheduler` turns into CP-SAT. Adding a block is a case
in `RuleEngine` plus a `RuleTypes` row with its parameter metadata, the rule builder draws its editor from that. See
`docs/Design.md`.

| Block | Compiles to |
|---|---|
| AssignTo (from relationship or selector) | optional intervals + `AddExactlyOne` |
| Consumes (secondary resource, qty) | `AddCumulative`, capacity = copies |
| Capacity (multiple items on one resource) | `AddCumulative` on slots/space |
| Configurations (either/or combinations, e.g. 7 A or 4 A + 2 B) | literal per combination per window, optional intervals take up what it doesn't allow, change time blocks the resource |
| Within / Outside calendar | domain bounds / fixed blocked intervals in `NoOverlap` |
| Before / After (+gap) | linear precedence |
| SequenceGap (changeover) | conditional gap; `AddCircuit` later |
| ConcurrentCompatibility | if two items overlap on same resource, pair must be allowed |
| CountLimit | sum of booleans per resource per period |
| Same / Different | equality on assignment literals |
| Goals | weighted lateness, changeovers, load balance, preference penalties |

Every hard constraint gets an assumption literal so infeasibility can be explained with
`SufficientAssumptionsForInfeasibility`, mapped back to RuleInstance / Relationship names.
Time unit is configurable per profile (default 15-minute buckets). Solve time limit is a
run setting; always return the best solution found.

## UI design — Office-style ribbon, clean and calm
- **Ribbon** across the top with tabs: **Home**, **Data**, **Model**, **Setup**, **Rules**,
  **Schedule**, **View**. Each tab has labelled **groups** with large icon buttons
  (primary actions) and stacked small buttons (secondary). Collapsible ribbon; a quick
  access toolbar on the left of the title bar (Save, Undo, Redo, Solve).
  - Home: New Profile, Open, Save, Duplicate | Undo/Redo | Validate
  - Data: New Connection, Test, Refresh Schema | Add Source, Preview, Map Columns
  - Model: New Entity, Attributes, Roles | Duration Formula
  - Setup: New Matrix, New List, Value Scales | Import from Source | Calendars
  - Rules: New Rule, Duplicate, Enable/Disable | Check Conflicts
  - Schedule: Solve, Stop, Settings | Gantt, Table | Export to Meta
  - View: Navigator, Properties, Density (comfortable/compact), Collapse sidebar, Collapse properties
  - Home also has Users & Roles (Security)
- **Layout:** the Recommit upload site's dark **navigation sidebar** on the far left (brand mark collapses it, profiles,
  Schedule, Solve settings, Users & roles, the signed in user at the bottom), then the left **Navigator** tree (Connections, Sources, Entities, Relationships,
  Calendars, Rules, Runs), central **document area** with tabs, right **Properties**
  panel for the selected object (collapses to a strip, remembered per user), bottom **status bar** (connection state, validation
  count, last solve).
- **Look:** neutral surfaces, one accent colour, 4/8px spacing grid, Segoe UI / system
  font stack, line icons (Material Symbols Outlined or Lucide), subtle borders rather
  than heavy shadows, one light look (no theme switch) with the Recommit upload site's tokens in `styles.scss`.
- **Security:** Windows domain sign in, roles Site owner, Project owner, Project reader, Project solver (see README).
  Every API action needs a `[ProfileAccess]`; a solver only reaches the schedule.
- **Key screens:**
  - Connection wizard: provider → server/db/auth → Test → save. Show green/red status.
  - Source browser: schema tree of tables/views with search, data preview grid (top 200).
  - Column mapper: two panes, drag column onto attribute; role badges.
  - **Matrix editor:** pivot grid, frozen headers, cell dropdown from the ValueScale with
    colour coding, row/column filters, bulk fill (select range → set value),
    symmetric mirroring, switch to **List editor** for large sparse data.
  - Calendar editor: week pattern grid + exception date ranges.
  - **Rule builder:** chained block cards (WHO → WHEN → MUST/SHOULD → RELATION →
    parameters), drag-drop from a block palette, live "affects N items / M resources"
    preview.
  - Schedule view: Gantt by resource with zoom, plus a results table; conflicts panel
    listing clashing rules after an infeasible solve.

## Reference profile: Press Scheduling (seed this)
- Entities: Job (Item), Press (Resource), Tool (Resource, has Copies, Size),
  Compound (Reference).
- Job attributes: compound, assigned tool, quantity, cycle time, release, due, priority.
- Relationships: Job→AlternativeTool (List, Boolean); Tool×Press (Matrix, Boolean, drives
  AssignTo via size fit); Compound×Press (Matrix, Graded, drives AssignTo);
  Compound×Compound (Matrix, Boolean, symmetric, drives ConcurrentCompatibility).
- Press capacity: slots/platen space so presses can run multiple jobs at once.
- Calendars: per-press availability dates; site shutdown calendar inherited by all presses.
- Provide a seed script with ~10 presses, ~30 tools, ~8 compounds, ~150 jobs of
  synthetic data in `SchedulerMeta` (as a fake "source" schema), so the PoC works
  before real connections are added.

## Milestones (deliver and demo each before moving on)
1. Solution scaffold, `SchedulerMeta` database scripts, ribbon shell with navigator/properties.
2. Connections + source browser + preview (read-only enforcement with tests).
3. Entities, attributes, roles, column mapping, duration formula.
4. Value scales, matrix and list editors, calendars.
5. Rule block catalogue, rule builder UI, JSON storage, validation.
6. Solver compiler for AssignTo, Consumes, Capacity, Within/Outside, Before/After,
   ConcurrentCompatibility, lateness goal; Gantt output; infeasibility explanation.
7. Second profile (lengths through a routing, parent/child operations) built purely
   through configuration to prove genericity. Note any code changes it needed.

## Working conventions
- Run `dotnet build`, `dotnet test`, and `ng build` after each change set; fix before
  continuing.
- Unit tests for each rule block compiler with small hand-checkable cases.
- Keep the block catalogue data-driven: adding a block = one case in `RuleEngine` + one
  `RuleTypes` row with its parameters (the rule builder's editor comes from them).
- Keep `README.md` updated with run instructions and current milestone status.
