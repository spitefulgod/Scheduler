using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Diagnostics;
using Xunit;

namespace Scheduler.Api.Tests
{
    // Starts the API against a fresh test database built from the Database scripts, with the demo data
    // Windows sign in on localhost unless SCHEDULER_TEST_SERVER, SCHEDULER_TEST_USER and SCHEDULER_TEST_PASSWORD say otherwise
    public class ApiFactory : WebApplicationFactory<Program>
    {
        public const string DatabaseName = "Scheduler_Test";
        public static readonly string SqlServer = Environment.GetEnvironmentVariable("SCHEDULER_TEST_SERVER") ?? "localhost";
        private static readonly string? SqlUser = Environment.GetEnvironmentVariable("SCHEDULER_TEST_USER");
        private static readonly string? SqlPassword = Environment.GetEnvironmentVariable("SCHEDULER_TEST_PASSWORD");
        private static string SignIn => string.IsNullOrEmpty(SqlUser) ? "-E" : $"-U {SqlUser} -P \"{SqlPassword}\" -C";
        public static string ConnectionString => string.IsNullOrEmpty(SqlUser)
            ? $"Data Source={SqlServer};Initial Catalog={DatabaseName};Integrated Security=True;TrustServerCertificate=True;"
            : $"Data Source={SqlServer};Initial Catalog={DatabaseName};User ID={SqlUser};Password={SqlPassword};TrustServerCertificate=True;";

        public ApiFactory()
        {
            // the test database is built from the same scripts as the real one
            RunSql($"IF DB_ID(N'{DatabaseName}') IS NOT NULL BEGIN ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}] END");
            string scripts = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Database"));
            RunSqlCmd($"-S {SqlServer} {SignIn} -I -b -v DatabaseName={DatabaseName} -i \"{Path.Combine(scripts, "RunAll.sql")}\"", scripts);
            RunSqlCmd($"-S {SqlServer} {SignIn} -I -b -v DatabaseName={DatabaseName} -i \"{Path.Combine(scripts, "RunDemo.sql")}\"", scripts);
            // a read only SQL Server login for the connection tests, it goes when the tests finish
            RunSql($"IF SUSER_ID(N'{SourceUser}') IS NOT NULL DROP LOGIN [{SourceUser}]; CREATE LOGIN [{SourceUser}] WITH PASSWORD = N'{SourcePassword}', CHECK_POLICY = OFF");
            RunQuery($"CREATE USER [{SourceUser}] FOR LOGIN [{SourceUser}]; ALTER ROLE db_datareader ADD MEMBER [{SourceUser}]");
        }
        // The SQL Server login connections in the tests use, a new password each run
        public const string SourceUser = "scheduler_source_test";
        public static readonly string SourcePassword = "Src!" + Guid.NewGuid().ToString("N");
        // A new key each run, a real server keeps its own in Sources:Key
        public static readonly string SourceKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:SchedulerDBConn", ConnectionString);
            builder.UseSetting("Sources:Key", SourceKey);
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            RunSql($"IF DB_ID(N'{DatabaseName}') IS NOT NULL BEGIN ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}] END");
            RunSql($"IF SUSER_ID(N'{SourceUser}') IS NOT NULL DROP LOGIN [{SourceUser}]");
        }
        // Runs some sql against the test database and gives back the first value
        public static string RunQuery(string sql)
        {
            return RunSqlCmd($"-S {SqlServer} {SignIn} -I -b -d {DatabaseName} -h -1 -W -Q \"SET NOCOUNT ON; {sql}\"", null).Trim();
        }
        private static void RunSql(string sql)
        {
            RunSqlCmd($"-S {SqlServer} {SignIn} -I -b -d master -Q \"{sql}\"", null);
        }
        private static string RunSqlCmd(string arguments, string? folder)
        {
            ProcessStartInfo info = new ProcessStartInfo("sqlcmd", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = folder ?? AppContext.BaseDirectory
            };
            using (Process process = Process.Start(info)!)
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new Exception($"sqlcmd failed: {output} {error}");
                return output;
            }
        }
    }
}

namespace Scheduler.Api.Tests
{
    // Every test class shares the one api and test database, so they don't drop each other's database
    [CollectionDefinition("Api")]
    public class ApiCollection : ICollectionFixture<ApiFactory>
    {
    }
}
