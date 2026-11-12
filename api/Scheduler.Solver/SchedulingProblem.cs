namespace Scheduler.Solver
{
    // Everything CP-SAT is asked, made by the rule engine from a profile's data and rules
    // it knows nothing about presses or tools, only items, the resources they run on, the pools they draw from and the rules between them
    // every time is a bucket number counted from HorizonStart, a bucket is BucketMinutes long
    public class SchedulingProblem
    {
        public DateTime HorizonStart { get; set; }
        public int BucketMinutes { get; set; } = 15;
        public int HorizonBuckets { get; set; }

        public List<WorkItem> Items { get; set; } = new List<WorkItem>();
        public List<WorkResource> Resources { get; set; } = new List<WorkResource>();
        public List<WorkPool> Pools { get; set; } = new List<WorkPool>();
        public List<Separation> Separations { get; set; } = new List<Separation>();
        public List<Precedence> Precedences { get; set; } = new List<Precedence>();
        public List<SharedLimit> SharedLimits { get; set; } = new List<SharedLimit>();
        // Locked work, already decided, it takes up room but is never moved
        public List<FixedBooking> Fixed { get; set; } = new List<FixedBooking>();
        public ObjectiveWeights Weights { get; set; } = new ObjectiveWeights();
        // When an item may be left out, at a cost, rather than make the whole plan fail
        public bool AllowUnscheduled { get; set; } = true;
        // Where each item was in the plan this one replaces, for the stability goal
        public Dictionary<string, BaselinePlacement> Baseline { get; set; } = new Dictionary<string, BaselinePlacement>(StringComparer.OrdinalIgnoreCase);
        // The rules a hard constraint came from, so a clash can be named, id to name
        public Dictionary<string, string> RuleNames { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int ToBucket(DateTime time)
        {
            return (int)Math.Floor((time - HorizonStart).TotalMinutes / BucketMinutes);
        }
        public int ToBucketCeiling(DateTime time)
        {
            return (int)Math.Ceiling((time - HorizonStart).TotalMinutes / BucketMinutes - 1e-9);
        }
        public DateTime ToTime(int bucket)
        {
            return HorizonStart.AddMinutes((double)bucket * BucketMinutes);
        }
        public int MinutesToBuckets(double minutes)
        {
            return (int)Math.Ceiling(minutes / BucketMinutes - 1e-9);
        }
    }

    // Something to schedule, e.g. a job, made in one or more segments, each on one mode
    public class WorkItem
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        // How many units are needed, a cycle makes UnitsPerCycle units for each copy of the pool used
        public long Quantity { get; set; } = 1;
        public int ReleaseBucket { get; set; }
        public int? DueBucket { get; set; }
        // How much lateness on this item costs, per bucket, e.g. from its priority
        public int LatenessWeight { get; set; } = 1;
        // Up to this many segments, each on its own mode with its own tool count, e.g. 4 tools for a few cycles then 2
        public int MaxSegments { get; set; } = 1;
        public int MinSegmentCycles { get; set; } = 1;
        // Segments may run at the same time on different resources
        public bool AllowParallel { get; set; }
        public int SegmentPenalty { get; set; } = 50;
        // Inspections, minutes each: once after the first cycle of a segment, every N cycles, and at each setup
        public int FirstOffInspectionMinutes { get; set; }
        public int EveryCycles { get; set; }
        public int EveryCyclesInspectionMinutes { get; set; }
        public int SetupInspectionMinutes { get; set; }
        // What-if: must run on this resource and, when given, start in this bucket
        public string? PinResourceKey { get; set; }
        public int? PinStartBucket { get; set; }
        // Must be planned even when unscheduled items are allowed
        public bool Required { get; set; }
        // The ways it can run, after the eligibility rules have taken out the ones that aren't allowed
        public List<WorkMode> Modes { get; set; } = new List<WorkMode>();
        // Why there are no modes, e.g. which rules took each resource out
        public string? NoModeReason { get; set; }
        // For the Gantt, e.g. the compound, so bars can be coloured by it
        public Dictionary<string, string?> Attributes { get; set; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    }

    // One way to run an item: on a resource, optionally with copies drawn from a pool (e.g. a tool), at a rate
    public class WorkMode
    {
        public string ResourceKey { get; set; } = "";
        public string? PoolKey { get; set; }
        // How many copies of the pool run at once, the solver picks within this range
        public int MinCount { get; set; } = 1;
        public int MaxCount { get; set; } = 1;
        public int UnitsPerCycle { get; set; } = 1;
        public double CycleSeconds { get; set; } = 60;
        public int SetupMinutes { get; set; }
        // Soft rule costs for choosing this mode, e.g. an Avoid grade or moving the job to another cell
        public int Cost { get; set; }
        public List<string> CostReasons { get; set; } = new List<string>();
        // What one copy (PerCount) and the mode itself (Fixed) take of each of the resource's capacity dimensions
        public List<DimensionDemand> Demands { get; set; } = new List<DimensionDemand>();
    }

    public class DimensionDemand
    {
        public string Dimension { get; set; } = "";
        public int PerCount { get; set; }
        public int Fixed { get; set; }
    }

    // Where items run, e.g. a press, with one or more capacity dimensions, e.g. slots, space or 7 small tools
    // with no dimensions it runs one item at a time
    public class WorkResource
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public Dictionary<string, int> Capacities { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Buckets it can't be used in, from its calendar, breakdowns and maintenance
        public List<BucketRange> Unavailable { get; set; } = new List<BucketRange>();
        public Dictionary<string, string?> Attributes { get; set; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        // Combinations it can be set up in, one at a time in each window, e.g. a platen holding 7 A tools, or 5 A and 1 B
        public List<ConfigurationSet> Configurations { get; set; } = new List<ConfigurationSet>();
    }

    // The combinations a resource can be in, each a limit on some of its capacity dimensions, the resource is in exactly one
    // combination in each window, so the limits are either or between whole combinations rather than one per dimension
    // the resource's capacity in each dimension named here is the most any combination allows
    public class ConfigurationSet
    {
        public string RuleId { get; set; } = "";
        // When the combination can change, e.g. one window a day, a single window covering the horizon means it never changes
        public List<BucketRange> Windows { get; set; } = new List<BucketRange>();
        // Each time the combination changes from one window to the next the resource stops for this long at the window's start,
        // e.g. resetting the platen, and the change costs this much
        public int ChangeBuckets { get; set; }
        public int ChangeCost { get; set; }
        public List<ResourceConfiguration> Options { get; set; } = new List<ResourceConfiguration>();
    }

    public class ResourceConfiguration
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        // Dimension to how many fit, a dimension the combination doesn't name allows none
        public Dictionary<string, int> Limits { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    // Something with a number of copies that items draw from while they run, e.g. a mould tool with 2 copies
    public class WorkPool
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public int Copies { get; set; } = 1;
        public List<BucketRange> Unavailable { get; set; } = new List<BucketRange>();
    }

    public class BucketRange
    {
        public int Start { get; set; }
        public int End { get; set; }
        public string? Reason { get; set; }
    }

    // Two items that can't overlap on the same resource, and need GapBuckets between them when they're both on it
    // e.g. compounds that can't share a press (gap 0) or a colour change that needs a clean down
    public class Separation
    {
        public string ItemA { get; set; } = "";
        public string ItemB { get; set; } = "";
        public int GapBuckets { get; set; }
        public string RuleId { get; set; } = "";
    }

    // Before finishes, then GapBuckets later After can start
    public class Precedence
    {
        public string Before { get; set; } = "";
        public string After { get; set; } = "";
        public int GapBuckets { get; set; }
        public string RuleId { get; set; } = "";
    }

    // Items that share a limit wherever they run, e.g. at most 2 silicone jobs at once, or 3 setters on a shift
    public class SharedLimit
    {
        public string RuleId { get; set; } = "";
        public string Name { get; set; } = "";
        public int Capacity { get; set; } = 1;
        public List<string> ItemKeys { get; set; } = new List<string>();
    }

    // Work that's already decided, from a locked plan, it uses its resource, pool and capacity but is never moved
    public class FixedBooking
    {
        public string ItemKey { get; set; } = "";
        public string ResourceKey { get; set; } = "";
        public string? PoolKey { get; set; }
        public int Count { get; set; } = 1;
        public int StartBucket { get; set; }
        public int EndBucket { get; set; }
        public long Quantity { get; set; }
        public List<DimensionDemand> Demands { get; set; } = new List<DimensionDemand>();
    }

    public class BaselinePlacement
    {
        public string ResourceKey { get; set; } = "";
        public int StartBucket { get; set; }
    }

    // What the plan is judged on, each is a weight on a cost
    public class ObjectiveWeights
    {
        // On each item's LatenessWeight times the buckets it's late
        public int Lateness { get; set; } = 10;
        // On the last finish of everything
        public int Makespan { get; set; }
        // On every item's finish, keeps work as early as it can be
        public int Compact { get; set; } = 1;
        // On an item moving to another resource, and on each bucket it moves in time, from the baseline plan
        public int StabilityResource { get; set; }
        public int StabilityTime { get; set; }
        // On each item left out, times its LatenessWeight
        public int Unscheduled { get; set; } = 100000;
        // On mode costs from soft rules
        public int Preferences { get; set; } = 1;
    }
}
