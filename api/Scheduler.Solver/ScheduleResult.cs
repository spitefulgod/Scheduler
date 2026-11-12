namespace Scheduler.Solver
{
    // What came back from a solve, in buckets, the API turns them into times
    public class ScheduleResult
    {
        // Optimal, Feasible, Infeasible, Stopped, Invalid or Failed
        public string Status { get; set; } = "Failed";
        public double? Objective { get; set; }
        public double? BestBound { get; set; }
        public string Message { get; set; } = "";
        public double WallSeconds { get; set; }
        public List<SegmentAssignment> Assignments { get; set; } = new List<SegmentAssignment>();
        public List<UnscheduledItem> Unscheduled { get; set; } = new List<UnscheduledItem>();
        // When nothing could be planned, the hard rules that clash, id and name
        public List<ConflictingRule> Conflicts { get; set; } = new List<ConflictingRule>();
        public Dictionary<string, int> Stats { get; set; } = new Dictionary<string, int>();
        // The combination each configured resource is in, window by window, windows in a row with the same combination are joined
        public List<ConfigurationChoice> Configurations { get; set; } = new List<ConfigurationChoice>();
    }

    public class ConfigurationChoice
    {
        public string ResourceKey { get; set; } = "";
        public string RuleId { get; set; } = "";
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public int StartBucket { get; set; }
        public int EndBucket { get; set; }
    }

    public class SegmentAssignment
    {
        public string ItemKey { get; set; } = "";
        public int Segment { get; set; }
        public string ResourceKey { get; set; } = "";
        public string? PoolKey { get; set; }
        public int Count { get; set; }
        public long Cycles { get; set; }
        public long Quantity { get; set; }
        public int StartBucket { get; set; }
        public int EndBucket { get; set; }
        public int SetupMinutes { get; set; }
        public int LateBuckets { get; set; }
        public int Cost { get; set; }
        public List<string> CostReasons { get; set; } = new List<string>();
        public List<InspectionPoint> Inspections { get; set; } = new List<InspectionPoint>();
        // Came from a locked booking rather than this solve
        public bool Fixed { get; set; }
    }

    // An inspection inside a segment, minutes from its start
    public class InspectionPoint
    {
        public string Kind { get; set; } = "";
        public double OffsetMinutes { get; set; }
        public int Minutes { get; set; }
    }

    public class UnscheduledItem
    {
        public string ItemKey { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    public class ConflictingRule
    {
        public string RuleId { get; set; } = "";
        public string Name { get; set; } = "";
    }
}
