using Scheduler.Solver;
using Scheduler.Solver.Rules;
using System.Text.Json;
using Xunit;

namespace Scheduler.Api.Tests
{
    // The Configurations block: a press holds tools of sizes A to D in one of a list of combinations, e.g.
    //   Combo 1  7 A
    //   Combo 2  5 A, 1 B
    //   Combo 3  5 A, 1 C
    //   Combo 4  4 A, 2 B
    // it can change combination from one day to the next, which stops the press for an hour
    public class ConfigurationTests
    {
        private static readonly DateTime Start = new DateTime(2026, 11, 2, 6, 0, 0);

        private static Dictionary<string, object?> Row(params (string name, object? value)[] values)
        {
            Dictionary<string, object?> row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach ((string name, object? value) in values)
                row[name] = value;
            return row;
        }
        private static RuleModel Rule(string id, string type, string json)
        {
            return new RuleModel { Id = id, Name = id, RuleTypeId = type, Strength = "Hard", Blocks = JsonDocument.Parse(json).RootElement.Clone() };
        }

        // Jobs on one press P1, each runs every copy of its tool, 1 part a cycle of a minute
        private static ProfileModel Press(string configurationParams, params (string job, string tool, int qty)[] jobs)
        {
            ProfileModel model = new ProfileModel { ProfileId = "p", Name = "Configurations", TimeUnitMinutes = 15 };
            EntityModel job = new EntityModel
            {
                Id = "job", Name = "Job", Kind = "Item",
                Attributes =
                {
                    new AttributeModel { Name = "job_no", Role = "Id" },
                    new AttributeModel { Name = "tool", ReferencesEntity = "Tool" },
                    new AttributeModel { Name = "qty", Role = "Quantity", DataType = "INTEGER" },
                    new AttributeModel { Name = "cycle_secs", Role = "CycleTime", DataType = "INTEGER" },
                    new AttributeModel { Name = "due", Role = "DueDate", DataType = "DATETIME" }
                }
            };
            foreach ((string key, string tool, int qty) in jobs)
                job.Rows.Add(Row(("job_no", key), ("tool", tool), ("qty", qty), ("cycle_secs", 60), ("due", Start.AddHours(4))));
            model.Entities.Add(job);
            model.Entities.Add(new EntityModel
            {
                Id = "press", Name = "Press", Kind = "Resource",
                Attributes = { new AttributeModel { Name = "code", Role = "Id" } },
                Rows = { Row(("code", "P1")) }
            });
            model.Entities.Add(new EntityModel
            {
                Id = "tool", Name = "Tool", Kind = "Resource",
                Attributes =
                {
                    new AttributeModel { Name = "code", Role = "Id" },
                    new AttributeModel { Name = "size_class" },
                    new AttributeModel { Name = "copies", Role = "Copies", DataType = "INTEGER" }
                },
                Rows =
                {
                    Row(("code", "A6"), ("size_class", "A"), ("copies", 6)),
                    Row(("code", "A4"), ("size_class", "A"), ("copies", 4)),
                    Row(("code", "B1"), ("size_class", "B"), ("copies", 1)),
                    Row(("code", "B2"), ("size_class", "B"), ("copies", 2)),
                    Row(("code", "C1"), ("size_class", "C"), ("copies", 1))
                }
            });
            model.Entities.Add(new EntityModel
            {
                Id = "combo", Name = "Combination", Kind = "Reference",
                Attributes = { new AttributeModel { Name = "code", Role = "Id" }, new AttributeModel { Name = "name", Role = "Name" } },
                Rows =
                {
                    Row(("code", "1"), ("name", "Combo 1")),
                    Row(("code", "2"), ("name", "Combo 2")),
                    Row(("code", "3"), ("name", "Combo 3")),
                    Row(("code", "4"), ("name", "Combo 4"))
                }
            });
            model.ValueScales.Add(new ValueScaleModel
            {
                Id = "BOOLEAN", Name = "Boolean",
                Values = { new ScaleValueModel { Code = "Y", Meaning = "Allowed" }, new ScaleValueModel { Code = "N", Label = "No", Meaning = "Forbidden" } }
            });
            RelationshipModel sizes = new RelationshipModel { Id = "comboSizes", Name = "Combination sizes", EntityA = "Combination", EntityB = "Size", ValueScaleId = "NUMBER" };
            foreach ((string combo, string size, int count) in new[] { ("1", "A", 7), ("2", "A", 5), ("2", "B", 1), ("3", "A", 5), ("3", "C", 1), ("4", "A", 4), ("4", "B", 2) })
                sizes.Values.Add(new RelationshipValueModel { A = combo, B = size, Value = count.ToString() });
            model.Relationships.Add(sizes);
            // P1 may only use combinations 1 and 2
            model.Relationships.Add(new RelationshipModel
            {
                Id = "pressCombos", Name = "Press combinations", EntityA = "Press", EntityB = "Combination", ValueScaleId = "BOOLEAN",
                Values = { new RelationshipValueModel { A = "P1", B = "1", Value = "Y" }, new RelationshipValueModel { A = "P1", B = "2", Value = "Y" } }
            });
            model.Rules.Add(Rule("runs", "RunsOn", "{\"params\":{\"resource\":\"Press\"}}"));
            model.Rules.Add(Rule("tooling", "UsesTooling", "{\"params\":{\"tooling\":\"Tool\",\"toolAttribute\":\"item.tool\",\"minCount\":\"tooling.copies\",\"maxCount\":\"tooling.copies\"}}"));
            model.Rules.Add(Rule("combos", "Configurations", "{\"params\":{\"relationshipId\":\"comboSizes\",\"key\":\"tooling.size_class\",\"per\":\"copy\"" + configurationParams + "}}"));
            model.Rules.Add(Rule("late", "Goal", "{\"params\":{\"goal\":\"Lateness\"}}"));
            return model;
        }

        private static ScheduleResult Solve(ProfileModel model)
        {
            EngineResult built = RuleEngine.Build(model, new RunContext { HorizonStart = Start, HorizonDays = 3 });
            Assert.False(built.HasErrors);
            ScheduleResult result = new CpSatScheduler().Solve(built.Problem, new SolveOptions { TimeLimitSeconds = 10, Workers = 4 });
            Assert.True(result.Status == "Optimal" || result.Status == "Feasible", $"{result.Status}: {result.Message}");
            return result;
        }

        [Fact]
        public void Capacities_AreTheMostAnyCombinationAllows()
        {
            EngineResult built = RuleEngine.Build(Press(",\"windowHours\":\"24\""), new RunContext { HorizonStart = Start, HorizonDays = 3 });
            WorkResource press = built.Problem.Resources.Single();
            ConfigurationSet set = press.Configurations.Single();
            Assert.Equal(4, set.Options.Count);
            Assert.Equal(new[] { "Combo 1", "Combo 2", "Combo 3", "Combo 4" }, set.Options.Select(x => x.Label).ToArray());
            Assert.Equal((7, 2, 1), (press.Capacities["cfg:combos:A"], press.Capacities["cfg:combos:B"], press.Capacities["cfg:combos:C"]));
            // a window to midnight, then a day at a time
            Assert.Equal((0, 72), (set.Windows[0].Start, set.Windows[0].End));
            Assert.Equal((72, 168), (set.Windows[1].Start, set.Windows[1].End));
        }

        [Fact]
        public void AllowedList_LimitsTheCombinations()
        {
            ProfileModel model = Press(",\"allowedRelationshipId\":\"pressCombos\"", ("J1", "B2", 100), ("J2", "C1", 100));
            EngineResult built = RuleEngine.Build(model, new RunContext { HorizonStart = Start, HorizonDays = 3 });
            WorkResource press = built.Problem.Resources.Single();
            Assert.Equal(2, press.Configurations.Single().Options.Count);
            // only combo 2 has a B, and just 1, nothing has a C
            Assert.Empty(built.Problem.Items.Single(x => x.Key == "J1").Modes);
            Assert.Empty(built.Problem.Items.Single(x => x.Key == "J2").Modes);
            Assert.Contains("no combination takes size C", built.Problem.Items.Single(x => x.Key == "J2").NoModeReason);
        }

        // 4 A and 2 B is combination 4, so the jobs share the press
        [Fact]
        public void SizesShare_WhenACombinationHoldsThem()
        {
            ScheduleResult result = Solve(Press(",\"windowHours\":\"24\",\"changeMinutes\":\"60\"", ("J1", "A4", 960), ("J2", "B2", 480)));
            SegmentAssignment j1 = result.Assignments.Single(x => x.ItemKey == "J1");
            SegmentAssignment j2 = result.Assignments.Single(x => x.ItemKey == "J2");
            Assert.Equal((0, 0), (j1.StartBucket, j2.StartBucket));
            Assert.Equal("4", result.Configurations.First(x => x.StartBucket == 0).Key);
        }

        // 6 A and 1 B fit the most of each size (7 A, 2 B) but no one combination, so the B job waits for the next day,
        // after an hour's change of combination
        [Fact]
        public void Combinations_AreEitherOr_AndChangingTakesTime()
        {
            ScheduleResult result = Solve(Press(",\"windowHours\":\"24\",\"changeMinutes\":\"60\"", ("J1", "A6", 1440), ("J2", "B1", 240)));
            SegmentAssignment j1 = result.Assignments.Single(x => x.ItemKey == "J1");
            SegmentAssignment j2 = result.Assignments.Single(x => x.ItemKey == "J2");
            Assert.True(j1.EndBucket <= j2.StartBucket || j2.EndBucket <= j1.StartBucket);
            SegmentAssignment later = j1.StartBucket < j2.StartBucket ? j2 : j1;
            // the next day starts at bucket 72, the change takes 4 buckets
            Assert.True(later.StartBucket >= 76, $"starts at {later.StartBucket}");
            Assert.True(result.Configurations.Select(x => x.Key).Distinct().Count() >= 2);
        }

        // when the combination can't change the B job can't run with the A job's combination at all
        [Fact]
        public void FixedCombination_LeavesOutWhatDoesntFit()
        {
            ScheduleResult result = Solve(Press(",\"windowHours\":\"0\"", ("J1", "A6", 1440), ("J2", "B1", 240)));
            Assert.Single(result.Unscheduled);
            Assert.Single(result.Configurations);
        }
    }
}
