using Google.OrTools.Sat;

namespace Scheduler.Solver
{
    // Checks the OR-Tools native runtime loads on this machine
    public static class CpSatHealthCheck
    {
        public static bool Run()
        {
            CpModel model = new CpModel();
            IntVar x = model.NewIntVar(0, 10, "x");
            IntVar y = model.NewIntVar(0, 10, "y");
            model.Add(x + y == 10);
            model.Add(x - y == 4);
            CpSolver solver = new CpSolver();
            CpSolverStatus status = solver.Solve(model);
            return status == CpSolverStatus.Optimal && solver.Value(x) == 7 && solver.Value(y) == 3;
        }
    }
}
