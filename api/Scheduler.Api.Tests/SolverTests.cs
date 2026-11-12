using Scheduler.Solver;
using Xunit;

namespace Scheduler.Api.Tests
{
    public class SolverTests
    {
        [Fact]
        public void CpSat_LoadsAndSolves()
        {
            Assert.True(CpSatHealthCheck.Run());
        }
    }
}
