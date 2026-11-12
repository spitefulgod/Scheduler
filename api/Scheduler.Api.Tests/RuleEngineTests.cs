using Scheduler.Solver;
using Scheduler.Solver.Rules;
using System.Text.Json;
using Xunit;

namespace Scheduler.Api.Tests
{
    // The rule engine on a small moulding shop built in code: jobs, presses, tools of different sizes, compounds and cells
    public class RuleEngineTests
    {
        private static readonly DateTime Start = new DateTime(2026, 11, 2, 6, 0, 0);

        private static Dictionary<string, object?> Row(params (string name, object? value)[] values)
        {
            Dictionary<string, object?> row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach ((string name, object? value) in values)
                row[name] = value;
            return row;
        }
        private static RuleModel Rule(string id, string type, string json, string strength = "Hard", int weight = 0)
        {
            return new RuleModel { Id = id, Name = id, RuleTypeId = type, Strength = strength, Weight = weight, Blocks = JsonDocument.Parse(json).RootElement.Clone() };
        }

        // P1 is small (space 2, cell A), P2 is big (space 6, cell B), tools T1 (size 1, 2 copies, 4 cavities) and T2 (size 4)
        public static ProfileModel Moulding()
        {
            ProfileModel model = new ProfileModel { ProfileId = "p", Name = "Moulding", TimeUnitMinutes = 15 };
            model.Entities.Add(new EntityModel
            {
                Id = "job", Name = "Job", Kind = "Item",
                Attributes =
                {
                    new AttributeModel { Name = "job_no", Role = "Id" },
                    new AttributeModel { Name = "compound", Role = "Category", ReferencesEntity = "Compound" },
                    new AttributeModel { Name = "tool", ReferencesEntity = "Tool" },
                    new AttributeModel { Name = "qty", Role = "Quantity", DataType = "INTEGER" },
                    new AttributeModel { Name = "cycle_secs", Role = "CycleTime", DataType = "INTEGER" },
                    new AttributeModel { Name = "due", Role = "DueDate", DataType = "DATETIME" },
                    new AttributeModel { Name = "cell" },
                    new AttributeModel { Name = "priority", Role = "Priority", DataType = "INTEGER" }
                },
                Rows =
                {
                    Row(("job_no", "J1"), ("compound", "NBR"), ("tool", "T1"), ("qty", 800), ("cycle_secs", 180), ("due", Start.AddDays(2)), ("cell", "A"), ("priority", 5)),
                    Row(("job_no", "J2"), ("compound", "SIL"), ("tool", "T1"), ("qty", 80), ("cycle_secs", 180), ("due", Start.AddDays(2)), ("cell", "A"), ("priority", 1)),
                    Row(("job_no", "J3"), ("compound", "NBR"), ("tool", "T2"), ("qty", 100), ("cycle_secs", 240), ("due", Start.AddDays(3)), ("cell", "B"), ("priority", 3)),
                    Row(("job_no", "J4"), ("compound", "FKM"), ("tool", "T3"), ("qty", 50), ("cycle_secs", 240), ("due", Start.AddDays(3)), ("cell", "B"), ("priority", 3))
                }
            });
            model.Entities.Add(new EntityModel
            {
                Id = "press", Name = "Press", Kind = "Resource",
                Attributes =
                {
                    new AttributeModel { Name = "code", Role = "Id" },
                    new AttributeModel { Name = "space", Role = "Capacity", DataType = "INTEGER" },
                    new AttributeModel { Name = "max_tool_size", Role = "Size", DataType = "INTEGER" },
                    new AttributeModel { Name = "cell" }
                },
                Rows =
                {
                    Row(("code", "P1"), ("space", 2), ("max_tool_size", 2), ("cell", "A")),
                    Row(("code", "P2"), ("space", 6), ("max_tool_size", 5), ("cell", "B"))
                }
            });
            model.Entities.Add(new EntityModel
            {
                Id = "tool", Name = "Tool", Kind = "Resource",
                Attributes =
                {
                    new AttributeModel { Name = "code", Role = "Id" },
                    new AttributeModel { Name = "size", Role = "Size", DataType = "INTEGER" },
                    new AttributeModel { Name = "copies", Role = "Copies", DataType = "INTEGER" },
                    new AttributeModel { Name = "cavities", DataType = "INTEGER" }
                },
                Rows =
                {
                    Row(("code", "T1"), ("size", 1), ("copies", 2), ("cavities", 4)),
                    Row(("code", "T2"), ("size", 4), ("copies", 1), ("cavities", 1)),
                    Row(("code", "T3"), ("size", 9), ("copies", 1), ("cavities", 1))
                }
            });
            model.Entities.Add(new EntityModel
            {
                Id = "compound", Name = "Compound", Kind = "Reference",
                Attributes = { new AttributeModel { Name = "code", Role = "Id" }, new AttributeModel { Name = "temp", DataType = "INTEGER" } },
                Rows = { Row(("code", "NBR"), ("temp", 170)), Row(("code", "SIL"), ("temp", 165)), Row(("code", "FKM"), ("temp", 185)) }
            });
            model.ValueScales.Add(new ValueScaleModel
            {
                Id = "BOOLEAN", Name = "Boolean",
                Values = { new ScaleValueModel { Code = "Y", Meaning = "Allowed" }, new ScaleValueModel { Code = "N", Label = "No", Meaning = "Forbidden" } }
            });
            model.ValueScales.Add(new ValueScaleModel
            {
                Id = "GRADED", Name = "Graded",
                Values =
                {
                    new ScaleValueModel { Code = "P", Label = "Preferred", Meaning = "Penalty", Weight = 0 },
                    new ScaleValueModel { Code = "V", Label = "Avoid", Meaning = "Penalty", Weight = 100 },
                    new ScaleValueModel { Code = "X", Label = "Not allowed", Meaning = "Forbidden" }
                }
            });
            model.Relationships.Add(new RelationshipModel
            {
                Id = "compPress", Name = "Compound on press", EntityA = "Compound", EntityB = "Press", ValueScaleId = "GRADED", DefaultValue = "P",
                Values = { new RelationshipValueModel { A = "SIL", B = "P2", Value = "X" }, new RelationshipValueModel { A = "NBR", B = "P1", Value = "V" } }
            });
            model.Relationships.Add(new RelationshipModel
            {
                Id = "compat", Name = "Compound compatibility", EntityA = "Compound", EntityB = "Compound", ValueScaleId = "BOOLEAN", DefaultValue = "Y", Symmetric = true,
                Values = { new RelationshipValueModel { A = "NBR", B = "SIL", Value = "N" } }
            });
            model.Rules.Add(Rule("runs", "RunsOn", "{\"params\":{\"resource\":\"Press\"}}"));
            model.Rules.Add(Rule("tooling", "UsesTooling", "{\"params\":{\"tooling\":\"Tool\",\"toolAttribute\":\"item.tool\",\"maxCount\":\"tooling.copies\",\"unitsPerCycle\":\"tooling.cavities\"}}"));
            model.Rules.Add(Rule("grade", "Allowed", "{\"params\":{\"relationshipId\":\"compPress\"}}"));
            model.Rules.Add(Rule("fits", "Condition", "{\"params\":{\"condition\":\"tooling.size <= resource.max_tool_size\"}}"));
            model.Rules.Add(Rule("space", "Capacity", "{\"params\":{\"capacity\":\"resource.space\",\"demand\":\"tooling.size\",\"per\":\"copy\"}}"));
            model.Rules.Add(Rule("compatible", "Compatible", "{\"params\":{\"relationshipId\":\"compat\",\"attribute\":\"item.compound\"}}"));
            model.Rules.Add(Rule("cell", "Condition", "{\"params\":{\"condition\":\"item.cell = resource.cell\"}}", "Soft", 40));
            model.Rules.Add(Rule("late", "Goal", "{\"params\":{\"goal\":\"Lateness\",\"weightAttribute\":\"priority\"}}", "Soft", 10));
            return model;
        }

        private static RunContext Context()
        {
            return new RunContext { HorizonStart = Start, HorizonDays = 7 };
        }

        [Fact]
        public void Modes_FollowTheRules()
        {
            EngineResult result = RuleEngine.Build(Moulding(), Context());
            Assert.False(result.HasErrors, string.Join("; ", result.Diagnostics.Select(x => x.Message)));
            WorkItem j1 = result.Problem.Items.Single(x => x.Key == "J1");
            // T1 fits both presses, NBR on P1 is avoid (100) and J1's cell is A so P2 costs the 40 for leaving it
            WorkMode onP1 = j1.Modes.Single(x => x.ResourceKey == "P1");
            WorkMode onP2 = j1.Modes.Single(x => x.ResourceKey == "P2");
            Assert.Equal(100, onP1.Cost);
            Assert.Equal(40, onP2.Cost);
            Assert.Equal(4, onP1.UnitsPerCycle);
            // P1 only has room for 2 copies of a size 1 tool, and T1 only has 2 copies anyway
            Assert.Equal(2, onP1.MaxCount);
            Assert.Equal("T1", onP1.PoolKey);

            // silicone isn't allowed on P2
            WorkItem j2 = result.Problem.Items.Single(x => x.Key == "J2");
            Assert.Equal(new[] { "P1" }, j2.Modes.Select(x => x.ResourceKey));

            // T2 is size 4, too big for P1
            WorkItem j3 = result.Problem.Items.Single(x => x.Key == "J3");
            Assert.Equal(new[] { "P2" }, j3.Modes.Select(x => x.ResourceKey));

            // T3 fits nowhere, the reason says which rule took the presses out
            WorkItem j4 = result.Problem.Items.Single(x => x.Key == "J4");
            Assert.Empty(j4.Modes);
            Assert.Contains("fits took out 2", j4.NoModeReason);

            // the lateness weight comes from the priority
            Assert.Equal(5, j1.LatenessWeight);
            Assert.Equal(10, result.Problem.Weights.Lateness);
        }

        [Fact]
        public void Pairs_ComeFromTheCompatibilityMatrix()
        {
            EngineResult result = RuleEngine.Build(Moulding(), Context());
            // J1 and J3 are NBR, J2 is SIL, NBR can't run with SIL
            List<(string, string)> pairs = result.Problem.Separations.Select(x => string.CompareOrdinal(x.ItemA, x.ItemB) < 0 ? (x.ItemA, x.ItemB) : (x.ItemB, x.ItemA)).OrderBy(x => x).ToList();
            Assert.Equal(new[] { ("J1", "J2"), ("J2", "J3") }, pairs);
        }

        [Fact]
        public void MustMatch_And_Changeover()
        {
            ProfileModel model = Moulding();
            model.Rules.Add(Rule("sameCycle", "MustMatch", "{\"params\":{\"attribute\":\"item.cycle_secs\"}}"));
            model.Rules.Add(Rule("clean", "Changeover", "{\"params\":{\"attribute\":\"item.compound.temp\",\"minutes\":\"30\"}}"));
            EngineResult result = RuleEngine.Build(model, Context());
            Separation j1j3 = result.Problem.Separations.Single(x => (x.ItemA + x.ItemB).Contains("J1") && (x.ItemA + x.ItemB).Contains("J3"));
            // different cycle times, same compound temperature, so they can't overlap but need no gap
            Assert.Equal(0, j1j3.GapBuckets);
            Separation j1j4 = result.Problem.Separations.Single(x => (x.ItemA + x.ItemB).Contains("J1") && (x.ItemA + x.ItemB).Contains("J4"));
            Assert.Equal(2, j1j4.GapBuckets);
        }

        [Fact]
        public void Calendars_Downs_Locks_And_Pins()
        {
            ProfileModel model = Moulding();
            model.Calendars.Add(new CalendarModel
            {
                Id = "site", Name = "Site",
                Shifts = Enumerable.Range(1, 5).Select(d => new CalendarShiftModel { DayOfWeek = d, StartTime = "06:00", EndTime = "22:00" }).ToList()
            });
            model.Rules.Add(Rule("cal", "Calendar", "{\"params\":{}}"));
            RunContext context = Context();
            context.Downs.Add(new ResourceDown { ResourceKey = "P2", From = Start.AddHours(2), To = Start.AddHours(4), Reason = "Hydraulic leak" });
            context.Locked.Add(new LockedBooking { ItemKey = "J1", ResourceKey = "P2", PoolKey = "T1", Count = 2, Start = Start, End = Start.AddHours(1), Quantity = 160 });
            context.Pins.Add(new ItemPin { ItemKey = "J3", ResourceKey = "P2", Start = Start.AddHours(5) });
            EngineResult result = RuleEngine.Build(model, context);

            WorkResource p2 = result.Problem.Resources.Single(x => x.Key == "P2");
            // 22:00 to 06:00 the first night is buckets 64 to 96, and the breakdown is buckets 8 to 16
            Assert.Contains(p2.Unavailable, x => x.Start == 64 && x.End == 96);
            Assert.Contains(p2.Unavailable, x => x.Start == 8 && x.End == 16 && x.Reason == "Hydraulic leak");

            Assert.Equal(640, result.Problem.Items.Single(x => x.Key == "J1").Quantity);
            FixedBooking locked = Assert.Single(result.Problem.Fixed);
            Assert.Equal(("J1", 0, 4), (locked.ItemKey, locked.StartBucket, locked.EndBucket));
            Assert.Contains(locked.Demands, x => x.PerCount == 1);

            WorkItem j3 = result.Problem.Items.Single(x => x.Key == "J3");
            Assert.Equal((20, true), (j3.PinStartBucket ?? -1, j3.Required));
        }

        [Fact]
        public void Emergency_RestrictsWhereWorkCanGo()
        {
            RunContext context = Context();
            context.RestrictTo["J1"] = new List<string> { "P2" };
            EngineResult result = RuleEngine.Build(Moulding(), context);
            Assert.Equal(new[] { "P2" }, result.Problem.Items.Single(x => x.Key == "J1").Modes.Select(x => x.ResourceKey));
        }

        [Fact]
        public void Selectors_LimitRulesToSomeItems()
        {
            ProfileModel model = Moulding();
            model.Rules.Add(Rule("siliconeInspect", "Inspection", "{\"who\":{\"entity\":\"Job\",\"when\":\"item.compound = 'SIL'\"},\"params\":{\"firstOffMinutes\":\"20\",\"everyCycles\":\"50\",\"everyMinutes\":\"5\"}}"));
            model.Rules.Add(Rule("split", "Split", "{\"who\":{\"when\":\"qty >= 500\"},\"params\":{\"maxSegments\":\"3\",\"allowParallel\":\"true\"}}"));
            EngineResult result = RuleEngine.Build(model, Context());
            WorkItem j1 = result.Problem.Items.Single(x => x.Key == "J1");
            WorkItem j2 = result.Problem.Items.Single(x => x.Key == "J2");
            Assert.Equal((0, 3, true), (j1.FirstOffInspectionMinutes, j1.MaxSegments, j1.AllowParallel));
            Assert.Equal((20, 50, 5, 1), (j2.FirstOffInspectionMinutes, j2.EveryCycles, j2.EveryCyclesInspectionMinutes, j2.MaxSegments));
        }

        [Fact]
        public void BadExpressions_AreReported_NotThrown()
        {
            ProfileModel model = Moulding();
            model.Rules.Add(Rule("broken", "Condition", "{\"params\":{\"condition\":\"tooling.size <=\"}}"));
            model.Rules.Add(Rule("typo", "Condition", "{\"params\":{\"condition\":\"resource.nope = 1\"}}", "Soft", 1));
            EngineResult result = RuleEngine.Build(model, Context());
            Assert.Contains(result.Diagnostics, x => x.Level == "error" && x.RuleId == "broken");
            Assert.Contains(result.Diagnostics, x => x.RuleId == "typo" && x.Message.Contains("no attribute nope"));
        }

        [Fact]
        public void EndToEnd_Solves()
        {
            EngineResult built = RuleEngine.Build(Moulding(), Context());
            ScheduleResult result = new CpSatScheduler().Solve(built.Problem, new SolveOptions { TimeLimitSeconds = 10, Workers = 4 });
            Assert.Contains(result.Status, new[] { "Optimal", "Feasible" });
            Assert.Contains(result.Unscheduled, x => x.ItemKey == "J4");
            SegmentAssignment j1 = result.Assignments.Single(x => x.ItemKey == "J1");
            SegmentAssignment j2 = result.Assignments.Single(x => x.ItemKey == "J2");
            Assert.Equal("P2", j1.ResourceKey);
            Assert.Equal("P1", j2.ResourceKey);
            // 800 parts at 4 cavities x 2 copies is 100 cycles of 3 minutes
            Assert.Equal((2, 100L), (j1.Count, j1.Cycles));
        }

        // A job shop: orders of operations on machines in sequence, with no tooling at all, the same engine
        [Fact]
        public void JobShop_RoutingThroughConfigurationOnly()
        {
            ProfileModel model = new ProfileModel { Name = "Job shop", TimeUnitMinutes = 5 };
            model.Entities.Add(new EntityModel
            {
                Name = "Operation", Kind = "Item",
                Attributes =
                {
                    new AttributeModel { Name = "op", Role = "Id" },
                    new AttributeModel { Name = "order_no" },
                    new AttributeModel { Name = "seq", Role = "Sequence", DataType = "INTEGER" },
                    new AttributeModel { Name = "machine_group" },
                    new AttributeModel { Name = "minutes", Role = "Duration", DataType = "DECIMAL" }
                },
                Rows =
                {
                    Row(("op", "A-10"), ("order_no", "A"), ("seq", 10), ("machine_group", "Lathe"), ("minutes", 30)),
                    Row(("op", "A-20"), ("order_no", "A"), ("seq", 20), ("machine_group", "Mill"), ("minutes", 60)),
                    Row(("op", "B-10"), ("order_no", "B"), ("seq", 10), ("machine_group", "Mill"), ("minutes", 45)),
                    Row(("op", "B-20"), ("order_no", "B"), ("seq", 20), ("machine_group", "Lathe"), ("minutes", 20))
                }
            });
            model.Entities.Add(new EntityModel
            {
                Name = "Machine", Kind = "Resource",
                Attributes = { new AttributeModel { Name = "code", Role = "Id" }, new AttributeModel { Name = "group" } },
                Rows = { Row(("code", "L1"), ("group", "Lathe")), Row(("code", "M1"), ("group", "Mill")) }
            });
            model.Rules.Add(Rule("on", "RunsOn", "{\"params\":{\"resource\":\"Machine\",\"when\":\"item.machine_group = resource.group\"}}"));
            model.Rules.Add(Rule("route", "Precedence", "{\"params\":{\"group\":\"item.order_no\",\"sequence\":\"item.seq\"}}"));
            model.Rules.Add(Rule("fast", "Goal", "{\"params\":{\"goal\":\"Makespan\"}}", "Soft", 10));
            EngineResult built = RuleEngine.Build(model, new RunContext { HorizonStart = Start, HorizonDays = 1, AllowUnscheduled = false });
            Assert.False(built.HasErrors);
            ScheduleResult result = new CpSatScheduler().Solve(built.Problem, new SolveOptions { TimeLimitSeconds = 10 });
            Assert.Equal("Optimal", result.Status);
            Dictionary<string, SegmentAssignment> ops = result.Assignments.ToDictionary(x => x.ItemKey);
            Assert.True(ops["A-10"].EndBucket <= ops["A-20"].StartBucket);
            Assert.True(ops["B-10"].EndBucket <= ops["B-20"].StartBucket);
            Assert.Equal("M1", ops["A-20"].ResourceKey);
            // the best is 105 minutes: B-10 on the mill while A-10 is on the lathe, then A-20, with B-20 on the lathe
            Assert.Equal(21, ops.Values.Max(x => x.EndBucket));
        }
    }
}
