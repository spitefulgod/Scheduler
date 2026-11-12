# Design notes

How the scheduler turns a profile into a plan, how each of the press shop's needs is met by configuration alone, how the
design holds up against harder production setups, and what it doesn't do yet.

## The pipeline

```
SQL Server sources ──read only──▶ ProfileLoader ──▶ ProfileModel ─┐
SchedulerMeta (entities, matrices,                                ├──▶ RuleEngine ──▶ SchedulingProblem ──▶ CpSatScheduler ──▶ ScheduleResult
calendars, rules, plan, locks)  ──▶ PlanBuilder ──▶ RunContext ───┘                                                    │
                                                                                                                      ▼
                                          Gantt, changes against the plan ◀── SaveSolveResult ◀── PlanBuilder.ToRows / Compare
```

- **`ProfileLoader`** (API) reads the profile's set up with `GetProfileModel`, then each entity's rows from its source through
  its column mapping, and each relationship's values live from its source with the values typed into the matrix on top.
  Nothing is copied out of the sources.
- **`PlanBuilder.BuildContext`** works out what a run may change: work already started or in a locked window stays where
  the published plan has it, item locks hold their items, what-if pins and emergency downs are applied, and the published
  plan becomes the baseline the stability goal keeps close to.
- **`RuleEngine`** (solver library) knows nothing about presses. It takes entities, relationships, calendars and rules and
  produces a generic `SchedulingProblem`: work items, each with its possible **modes** (a resource, an optional tooling pool,
  how many copies, units per cycle, cycle time, set up, cost and how much of each resource dimension it takes), resources
  with capacity dimensions and down time, pools of copies, pairs that can't overlap, precedences, shared limits, fixed
  bookings and objective weights.
- **`CpSatScheduler`** builds the CP-SAT model from the problem. It doesn't know about rules either, so a new rule type
  is a change to the rule engine only.

This replaced the `IBlockCompiler` per block design in CLAUDE.md: blocks compile to the generic problem instead of
straight to CP-SAT, which keeps the CP-SAT model small, lets several rules shape the same mode (a tool fit, a compound
grade and a cell preference all prune or cost the same press choice) and lets the rule engine be tested without a solver.

## The CP-SAT model

| Problem part | CP-SAT |
| --- | --- |
| Item | `scheduled` literal (fixed to 1 unless leaving items out is allowed), up to N segments |
| Segment | start, end, duration, cycles, copy count, units made, inspections, one optional interval per mode, exactly one mode |
| Duration | `duration × bucket ≥ cycles × cycle seconds + inspections + set up + first off`, with a bucket of slack at most |
| Copies | count between the mode's min and max, `units = cycles × count × cavities` via `AddMultiplicationEquality` |
| Resource with dimensions | one `AddCumulative` per dimension (slots, platen space, size class slots), demand `count × per copy + fixed` |
| Resource without dimensions | `AddNoOverlap` |
| Down time | fixed intervals: full capacity in each cumulative, or in the no overlap |
| Tooling pool | `AddCumulative`, capacity copies, demand the segment's count |
| Pairs that can't share | for each shared resource a presence literal per item and an order literal, a gap when one is asked for |
| Precedence | `end + gap ≤ start` between segments, an item can't be planned without its predecessor |
| Shared limit | `AddCumulative` over the items it covers |
| Configurations | each dimension's capacity is the most any combination allows, per window a literal per combination (exactly one), an optional interval per combination takes up what it doesn't allow in each dimension, a change between windows is an optional interval at the window's start that stops the resource and can cost |
| Goals | weighted lateness, makespan, compactness, preference costs, segment penalties, moving off the published plan, leaving items out |
| Infeasible | the model is built again with an assumption literal per hard rule and `SufficientAssumptionsForInfeasibility` names the rules that clash |

**First plan.** Before solving, a quick greedy plan is given to CP-SAT as a hint: pinned items first, then the published
plan's order, then earliest due, each item whole on the mode that ends soonest while keeping capacities, copies, down
times, shared limits and the pairs that can't share. On the demo this took a 30 second solve from 43 late jobs (6,384
hours late) to 5 late jobs (236 hours). Without it CP-SAT spent most of its time finding a first solution.

## The press shop, by configuration only

Everything below is rows in `09_Seed_PressScheduling.sql`, nothing in code is press specific.

| Need | How it's set up |
| --- | --- |
| Jobs with a start date, quantity and cycle time | Job entity, attributes with roles ReleaseDate, Quantity, CycleTime |
| Tools with copies, sizes, several alternatives | Tool entity (Copies, Size), *Uses tooling* rule with the Job alternative tools relationship, `maxCount = tooling.copies` |
| Multi cavity tools | `unitsPerCycle = tooling.cavities` |
| Tools fit presses up to a size, sizes can mix | *Allowed by matrix* on Tool × Press, *Capacity* rule `platen_space` with `demand = tooling.size per copy` |
| Configurations hold so many tools of each size | *Configurations*: a Configuration × Size class matrix of combinations (e.g. 1 medium and 3 small) and a Press × Configuration list, one combination per press per day, 90 minutes to change. *Capacity by size* (a limit per size, independent of each other) is still there and switched off in the demo |
| Changing configuration takes time | *Setup time* `tooling.change_minutes` |
| Some presses can't run some jobs | *Allowed by matrix* Compound × Press on the Graded scale, X is forbidden, Avoid costs 100 |
| Some materials can't be mixed | *Can share at once* on the Compound compatibility matrix |
| Cycle time and parameters must match when shared | *Must match at once* `item.cycle_secs` within 60 seconds, the same block works for temperature |
| A job on several presses at once | *Split* with `allowParallel = item.allow_parallel` |
| 4 tools for a while, then 2 while another job takes 2 | *Split* segments each choose their own copy count from the pool |
| Cells, a job can move cell at a cost | *Condition* `item.cell = resource.cell`, soft, weight 200 |
| Inspections after the first cycle, every N cycles, on change | *Inspection* first off 15, every `item.inspect_every` cycles 5 minutes, on set up 10 |
| At most two silicone jobs at once | *Shared limit* with `when item.compound = 'SIL-50'` |
| Calendars, site shutdown inherited by presses | Site calendar, a calendar per press inheriting it, *Calendars* rule |
| Lateness by priority, preferred presses, a steady plan | *Goal* rules: Lateness weighted by priority, Preferences, Stability |

## Locking, what-if and emergencies

- **Locks.** Work already running is always locked. A window lock (next hour, day, week, month, or any time) keeps
  everything starting before it where the published plan has it, optionally for one resource. An item lock holds one item.
  `lockHours` in the solve settings locks a rolling window on every run. Locked work goes into the model as fixed bookings
  that use capacity, pools and separations like any other work.
- **What-if.** Drag bars on the Gantt to another resource or time, then run the what-if. The moved items are pinned,
  everything else is planned again with the published plan as the baseline, and the run shows what else changed
  (moved, earlier, later, added, dropped, lateness before and after). **Proceed** publishes it, **Abort** discards it.
- **Emergency.** *Resource down* takes a resource, a time and the alternatives its work may go to. Locked or started work on
  it in that time is released (the part already made is kept), down time is added for the run, and the released items are
  restricted to the ticked alternatives. Publishing writes the down time into the resource's calendar so later runs keep it.

## Stress test against harder production setups

| Setup | What makes it nasty | How it maps | Gaps |
| --- | --- | --- | --- |
| Semiconductor fab | re-entrant routes, batch furnaces, queue time limits | operations as items with *Precedence* by group and sequence, furnaces as capacity dimensions | max time between steps (queue time) isn't a block yet, batching by recipe is approximated with *Must match* |
| Pharma campaigns | cleaning between products, campaign length, equipment trains | *Changeover* with a product matrix, *Shared limit* for shared rooms, *Split* minimum cycles for campaign size | sequence dependent cleaning is a minimum gap between pairs, not a full sequence (circuit) model |
| Food allergens | allergen order (light to dark), full washdowns | *Changeover* matrix with large gaps for the wrong order | the order is pairwise, not a global ordering constraint |
| Steel and aluminium | width and temperature windows, coffin shaped campaigns | *Must match* with a tolerance, *Changeover* gaps | gradual width steps across a campaign need a sequence model |
| Printing and packaging | plate and ink changes, jobs ganged on one sheet | *Uses tooling* for plates, *Capacity* for sheet space, *Can share* for compatible inks | |
| Aerospace machining | long routes, fixtures, tools with life, operators with skills | *Precedence*, fixtures as pools, operators as a second pool or resource | tool life (cycles before a change) isn't modelled, one tooling pool per mode |
| Tyre curing | presses with two cavities, green tyres with a shelf life | *Capacity* slots, *Must match* temperature, *Precedence* from building | shelf life is a queue time limit, as for fabs |
| Job shop (classic) | many machines, routes, makespan | proved in `RuleEngineTests` with configuration only, optimum 21 buckets found | |
| Flow line | identical routes, balance | items with *Precedence*, *Goal* makespan and compact | |

## Decisions made without asking

- The rule engine compiles to a generic problem rather than each block straight to CP-SAT (see above).
- Relationship values are read live from their source every run, values typed into the matrix win, **Import from source**
  copies them in when a matrix should stop depending on the source.
- Times are in buckets of the profile's time unit (15 minutes by default), the horizon defaults to 21 days for the demo.
- A run is one at a time per profile, in the background, Stop keeps the best plan found so far.
- A run left unfinished when the API stops is marked failed when it starts again, so it can't block new runs.
- Demo job dates are relative to the day the demo is built, so the demo always has work in its horizon.

## Limits and next steps

- **Sequence dependent changeovers** are a minimum gap between pairs on the same resource, not a full sequence model with
  `AddCircuit`. Fine for families, weak for long chains where only the next item matters.
- **Combinations change on window boundaries** (a day by default), not at any moment, and the first window of a plan takes
  whichever combination fits without a change time, as the combination the press is in now isn't read yet.
- **Work doesn't pause across down time**: a segment fits between down times. Splitting gives the solver a way round it.
- **Shared limits and precedences** aren't in the infeasibility explanation pass yet.
- **Pairwise separations grow with the square of items** on shared resources: the demo has 8,487 pairs and CP-SAT presolve
  takes about 6 seconds. Much larger profiles will want a time limit over a minute or a narrower horizon.
- **Queue time limits, tool life and operator skills** need new blocks.
- **Milestone 7**, a second profile set up only through the screens, is shown by the job shop test but not yet seeded.
- Undo, redo and duplicate profile aren't built.
