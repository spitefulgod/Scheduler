using Scheduler.Solver;
using Xunit;

namespace Scheduler.Api.Tests
{
    // Small hand checked problems for each thing the CP-SAT model does, 1 minute buckets so the sums are easy
    public class CpSatSchedulerTests
    {
        private static SchedulingProblem NewProblem(int horizon = 600)
        {
            return new SchedulingProblem
            {
                HorizonStart = new DateTime(2026, 11, 2, 6, 0, 0),
                BucketMinutes = 1,
                HorizonBuckets = horizon,
                AllowUnscheduled = false,
                Weights = new ObjectiveWeights { Lateness = 10, Compact = 1 }
            };
        }
        private static WorkItem NewItem(string key, long quantity, params WorkMode[] modes)
        {
            return new WorkItem { Key = key, Label = key, Quantity = quantity, Modes = modes.ToList() };
        }
        private static WorkMode NewMode(string resource, double cycleSeconds = 60, string? pool = null, int units = 1, int minCount = 1, int maxCount = 1)
        {
            return new WorkMode { ResourceKey = resource, PoolKey = pool, CycleSeconds = cycleSeconds, UnitsPerCycle = units, MinCount = minCount, MaxCount = maxCount };
        }
        private static ScheduleResult Solve(SchedulingProblem problem)
        {
            return new CpSatScheduler().Solve(problem, new SolveOptions { TimeLimitSeconds = 10, Workers = 4 });
        }
        private static List<SegmentAssignment> For(ScheduleResult result, string itemKey)
        {
            return result.Assignments.Where(x => x.ItemKey == itemKey && !x.Fixed).OrderBy(x => x.StartBucket).ToList();
        }

        [Fact]
        public void OneItem_TakesQuantityTimesCycle()
        {
            SchedulingProblem problem = NewProblem();
            problem.Resources.Add(new WorkResource { Key = "P1" });
            problem.Items.Add(NewItem("J1", 30, NewMode("P1", 120)));
            ScheduleResult result = Solve(problem);
            Assert.Equal("Optimal", result.Status);
            SegmentAssignment a = Assert.Single(For(result, "J1"));
            Assert.Equal(0, a.StartBucket);
            Assert.Equal(60, a.EndBucket);
            Assert.Equal(30, a.Cycles);
        }

        [Fact]
        public void MultiCavity_And_ToolCopies_CutTheTime()
        {
            // 4 cavities and 2 copies at once makes 8 a cycle, 80 units is 10 cycles
            SchedulingProblem problem = NewProblem();
            problem.Resources.Add(new WorkResource { Key = "P1" });
            problem.Pools.Add(new WorkPool { Key = "T1", Copies = 2 });
            problem.Items.Add(NewItem("J1", 80, NewMode("P1", 60, "T1", units : 4, minCount : 1, maxCount : 2)));
            ScheduleResult result = Solve(problem);
            SegmentAssignment a = Assert.Single(For(result, "J1"));
            Assert.Equal(2, a.Count);
            Assert.Equal(10, a.Cycles);
            Assert.Equal(10, a.EndBucket - a.StartBucket);
        }

        [Fact]
        public void ToolCopies_LimitWhatRunsAtOnce()
        {
            // two presses but one copy of the tool, so the jobs can't overlap
            SchedulingProblem problem = NewProblem();
            problem.Resources.Add(new WorkResource { Key = "P1" });
            problem.Resources.Add(new WorkResource { Key = "P2" });
            problem.Pools.Add(new WorkPool { Key = "T1", Copies = 1 });
            problem.Items.Add(NewItem("J1", 10, NewMode("P1", 60, "T1"), NewMode("P2", 60, "T1")));
            problem.Items.Add(NewItem("J2", 10, NewMode("P1", 60, "T1"), NewMode("P2", 60, "T1")));
            ScheduleResult result = Solve(problem);
            SegmentAssignment a = Assert.Single(For(result, "J1"));
            SegmentAssignment b = Assert.Single(For(result, "J2"));
            Assert.True(a.EndBucket <= b.StartBucket || b.EndBucket <= a.StartBucket);
        }

        [Fact]
        public void SizeCapacity_MixesToolsThatFit()
        {
            // the press has 4 units of space: a size 3 and a size 1 tool fit together, a second size 3 waits
            SchedulingProblem problem = NewProblem();
            problem.Resources.Add(new WorkResource { Key = "P1", Capacities = { ["space"] = 4 } });
            WorkMode Sized(int size) => new WorkMode { ResourceKey = "P1", CycleSeconds = 60, Demands = { new DimensionDemand { Dimension = "space", PerCount = size } } };
            problem.Items.Add(NewItem("Big1", 10, Sized(3)));
            problem.Items.Add(NewItem("Small", 10, Sized(1)));
            problem.Items.Add(NewItem("Big2", 10, Sized(3)));
            ScheduleResult result = Solve(problem);
            Assert.Equal("Optimal", result.Status);
            SegmentAssignment big1 = Assert.Single(For(result, "Big1"));
            SegmentAssignment big2 = Assert.Single(For(result, "Big2"));
            SegmentAssignment small = Assert.Single(For(result, "Small"));
            Assert.True(big1.EndBucket <= big2.StartBucket || big2.EndBucket <= big1.StartBucket);
            Assert.Equal(0, small.StartBucket);
        }

        [Fact]
        public void Separation_KeepsIncompatibleItemsApart_WithAGap()
        {
            SchedulingProblem problem = NewProblem();
            problem.Resources.Add(new WorkResource { Key = "P1", Capacities = { ["slots"] = 3 } });
            WorkMode Slot() => new WorkMode { ResourceKey = "P1", CycleSeconds = 60, Demands = { new DimensionDemand { Dimension = "slots", Fixed = 1 } } };
            problem.Items.Add(NewItem("A", 10, Slot()));
            problem.Items.Add(NewItem("B", 10, Slot()));
            problem.Separations.Add(new Separation { ItemA = "A", ItemB = "B", GapBuckets = 5, RuleId = "r1" });
            ScheduleResult result = Solve(problem);
            SegmentAssignment a = Assert.Single(For(result, "A"));
            SegmentAssignment b = Assert.Single(For(result, "B"));
            Assert.True(a.EndBucket + 5 <= b.StartBucket || b.EndBucket + 5 <= a.StartBucket);
        }

        [Fact]
        public void Inspections_AddTime()
        {
            // 100 cycles of a minute, 5 minutes setup, 10 first off, 2 minutes every 25 cycles (4 of them)
            SchedulingProblem problem = NewProblem();
            problem.Resources.Add(new WorkResource { Key = "P1" });
            WorkItem item = NewItem("J1", 100, new WorkMode { ResourceKey = "P1", CycleSeconds = 60, SetupMinutes = 5 });
            item.FirstOffInspectionMinutes = 10;
            item.EveryCycles = 25;
            item.EveryCyclesInspectionMinutes = 2;
            problem.Items.Add(item);
            ScheduleResult result = Solve(problem);
            SegmentAssignment a = Assert.Single(For(result, "J1"));
            Assert.Equal(100 + 5 + 10 + 8, a.EndBucket - a.StartBucket);
            Assert.Equal(new[] { "FirstOff", "Periodic", "Periodic", "Periodic", "Periodic" }, a.Inspections.Select(x => x.Kind));
        }

        [Fact]
        public void Split_RunsOnFourTools_ThenGivesTwoBack()
        {
            // J1 has 4 copies of T1 and a lot to make, J2 needs 2 copies of T1 early, so J1 splits:
            // the best plan has J1 using all 4 copies, then 2 while J2 has the other 2, rather than waiting
            SchedulingProblem problem = NewProblem(300);
            problem.Resources.Add(new WorkResource { Key = "P1", Capacities = { ["slots"] = 4 } });
            problem.Resources.Add(new WorkResource { Key = "P2", Capacities = { ["slots"] = 4 } });
            problem.Pools.Add(new WorkPool { Key = "T1", Copies = 4 });
            WorkMode Mode(string press, int min, int max) => new WorkMode { ResourceKey = press, PoolKey = "T1", CycleSeconds = 60, MinCount = min, MaxCount = max,
                Demands = { new DimensionDemand { Dimension = "slots", PerCount = 1 } } };
            WorkItem big = NewItem("J1", 400, Mode("P1", 1, 4));
            big.MaxSegments = 2;
            big.SegmentPenalty = 1;
            problem.Items.Add(big);
            WorkItem urgent = NewItem("J2", 40, Mode("P2", 2, 2));
            urgent.ReleaseBucket = 30;
            urgent.DueBucket = 50;
            problem.Items.Add(urgent);
            ScheduleResult result = Solve(problem);
            Assert.Contains(result.Status, new[] { "Optimal", "Feasible" });
            SegmentAssignment j2 = Assert.Single(For(result, "J2"));
            Assert.True(j2.EndBucket <= 50, $"J2 ended at {j2.EndBucket}");
            List<SegmentAssignment> j1 = For(result, "J1");
            Assert.Equal(400, j1.Sum(x => x.Quantity) >= 400 ? 400 : j1.Sum(x => x.Quantity));
            Assert.Contains(j1, x => x.Count == 4);
            // whenever both run, they never use more than the 4 copies between them
            foreach (SegmentAssignment segment in j1)
                if (segment.StartBucket < j2.EndBucket && j2.StartBucket < segment.EndBucket)
                    Assert.True(segment.Count + j2.Count <= 4);
        }

        [Fact]
        public void Downtime_IsAvoided()
        {
            SchedulingProblem problem = NewProblem();
            problem.Resources.Add(new WorkResource { Key = "P1", Unavailable = { new BucketRange { Start = 10, End = 100 } } });
            problem.Items.Add(NewItem("J1", 30, NewMode("P1")));
            ScheduleResult result = Solve(problem);
            SegmentAssignment a = Assert.Single(For(result, "J1"));
            Assert.Equal(100, a.StartBucket);
        }

        [Fact]
        public void Downtime_IsAvoided_OnCapacityResources()
        {
            SchedulingProblem problem = NewProblem();
            problem.Resources.Add(new WorkResource { Key = "P1", Capacities = { ["slots"] = 2 }, Unavailable = { new BucketRange { Start = 10, End = 100 } } });
            problem.Items.Add(NewItem("J1", 30, new WorkMode { ResourceKey = "P1", CycleSeconds = 60, Demands = { new DimensionDemand { Dimension = "slots", Fixed = 1 } } }));
            ScheduleResult result = Solve(problem);
            Assert.Equal(100, Assert.Single(For(result, "J1")).StartBucket);
        }

        [Fact]
        public void Locked_Work_IsKept_AndTakesRoom()
        {
            SchedulingProblem problem = NewProblem();
            problem.Resources.Add(new WorkResource { Key = "P1" });
            problem.Items.Add(NewItem("J2", 10, NewMode("P1")));
            problem.Fixed.Add(new FixedBooking { ItemKey = "J1", ResourceKey = "P1", StartBucket = 0, EndBucket = 50, Quantity = 50 });
            ScheduleResult result = Solve(problem);
            Assert.Equal(50, Assert.Single(For(result, "J2")).StartBucket);
            Assert.Contains(result.Assignments, x => x.ItemKey == "J1" && x.Fixed && x.StartBucket == 0);
        }

        [Fact]
        public void Precedence_And_Pin()
        {
            SchedulingProblem problem = NewProblem();
            problem.Resources.Add(new WorkResource { Key = "P1" });
            problem.Resources.Add(new WorkResource { Key = "P2" });
            problem.Items.Add(NewItem("Op10", 10, NewMode("P1"), NewMode("P2")));
            WorkItem op20 = NewItem("Op20", 10, NewMode("P2"));
            problem.Items.Add(op20);
            problem.Precedences.Add(new Precedence { Before = "Op10", After = "Op20", GapBuckets = 3, RuleId = "seq" });
            WorkItem pinned = NewItem("Other", 5, NewMode("P1"), NewMode("P2"));
            pinned.PinResourceKey = "P1";
            pinned.PinStartBucket = 200;
            pinned.Modes = pinned.Modes.Where(x => x.ResourceKey == "P1").ToList();
            problem.Items.Add(pinned);
            ScheduleResult result = Solve(problem);
            SegmentAssignment op10 = Assert.Single(For(result, "Op10"));
            Assert.True(op10.EndBucket + 3 <= Assert.Single(For(result, "Op20")).StartBucket);
            SegmentAssignment other = Assert.Single(For(result, "Other"));
            Assert.Equal(("P1", 200), (other.ResourceKey, other.StartBucket));
        }

        [Fact]
        public void SharedLimit_CapsWorkEverywhere()
        {
            SchedulingProblem problem = NewProblem();
            foreach (string press in new[] { "P1", "P2", "P3" })
                problem.Resources.Add(new WorkResource { Key = press });
            foreach (string job in new[] { "A", "B", "C" })
                problem.Items.Add(NewItem(job, 10, NewMode("P1"), NewMode("P2"), NewMode("P3")));
            problem.SharedLimits.Add(new SharedLimit { RuleId = "ops", Capacity = 2, ItemKeys = { "A", "B", "C" } });
            ScheduleResult result = Solve(problem);
            List<SegmentAssignment> all = result.Assignments;
            for (int t = 0; t < 40; t++)
                Assert.True(all.Count(x => x.StartBucket <= t && t < x.EndBucket) <= 2);
        }

        [Fact]
        public void Unscheduled_WhenItCantFit()
        {
            SchedulingProblem problem = NewProblem(60);
            problem.AllowUnscheduled = true;
            problem.Resources.Add(new WorkResource { Key = "P1" });
            problem.Items.Add(NewItem("Fits", 30, NewMode("P1")));
            problem.Items.Add(NewItem("TooBig", 100, NewMode("P1")));
            problem.Items.Add(new WorkItem { Key = "Nowhere", Quantity = 5, NoModeReason = "No press is allowed" });
            ScheduleResult result = Solve(problem);
            Assert.Single(For(result, "Fits"));
            Assert.Contains(result.Unscheduled, x => x.ItemKey == "TooBig");
            Assert.Contains(result.Unscheduled, x => x.ItemKey == "Nowhere" && x.Reason == "No press is allowed");
        }

        [Fact]
        public void Infeasible_NamesTheClashingRules()
        {
            // two items pinned to the same time on one press, with a rule that they can't overlap
            SchedulingProblem problem = NewProblem();
            problem.Resources.Add(new WorkResource { Key = "P1", Capacities = { ["slots"] = 2 } });
            WorkMode Slot() => new WorkMode { ResourceKey = "P1", CycleSeconds = 60, Demands = { new DimensionDemand { Dimension = "slots", Fixed = 1 } } };
            WorkItem a = NewItem("A", 10, Slot());
            a.PinStartBucket = 0;
            WorkItem b = NewItem("B", 10, Slot());
            b.PinStartBucket = 5;
            problem.Items.Add(a);
            problem.Items.Add(b);
            problem.Separations.Add(new Separation { ItemA = "A", ItemB = "B", RuleId = "compat" });
            problem.RuleNames["compat"] = "Only compatible compounds share a press";
            ScheduleResult result = Solve(problem);
            Assert.Equal("Infeasible", result.Status);
            Assert.Contains(result.Conflicts, x => x.Name == "Only compatible compounds share a press");
        }
    }
}
