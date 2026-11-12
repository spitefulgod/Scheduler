using Scheduler.Api.Services;
using Xunit;

namespace Scheduler.Api.Tests
{
    // Nothing that could change a source, or read outside what it's allowed to, gets past the guard
    public class ReadOnlySqlGuardTests
    {
        private static readonly SourceObjectName[] Allowed = new SourceObjectName[]
        {
            new SourceObjectName("src", "Job"),
            new SourceObjectName("src", "Press"),
            new SourceObjectName("dbo", "Orders")
        };

        private static GuardResult Check(string sql)
        {
            return ReadOnlySqlGuard.Validate(sql, Allowed, "Factory");
        }

        [Theory]
        [InlineData("SELECT * FROM src.Job")]
        [InlineData("SELECT TOP (200) JobNo, Quantity FROM [src].[Job] WHERE Quantity > 10 ORDER BY JobNo")]
        [InlineData("SELECT j.JobNo, p.PressCode FROM src.Job j JOIN src.Press p ON 1 = 1")]
        [InlineData("SELECT * FROM Orders")]
        [InlineData("SELECT * FROM Factory.dbo.Orders")]
        [InlineData("SELECT * FROM (SELECT JobNo FROM src.Job) AS q")]
        [InlineData("WITH big AS (SELECT * FROM src.Job WHERE Quantity > 500) SELECT * FROM big")]
        [InlineData("SELECT JobNo FROM src.Job UNION ALL SELECT PressCode FROM src.Press")]
        [InlineData("SELECT COUNT(*) FROM src.Job WITH (NOLOCK)")]
        [InlineData("select upper(JobNo) from src.job -- trailing comment")]
        public void Guard_AllowsOneSelectOfTheConfiguredObjects(string sql)
        {
            GuardResult result = Check(sql);
            Assert.True(result.IsAllowed, result.Reason);
        }

        [Theory]
        [InlineData("INSERT INTO src.Job (JobNo) VALUES ('x')", "Only SELECT")]
        [InlineData("UPDATE src.Job SET Quantity = 0", "Only SELECT")]
        [InlineData("DELETE FROM src.Job", "Only SELECT")]
        [InlineData("MERGE src.Job AS t USING src.Press AS s ON 1 = 0 WHEN NOT MATCHED THEN INSERT (JobNo) VALUES (s.PressCode);", "Only SELECT")]
        [InlineData("TRUNCATE TABLE src.Job", "Only SELECT")]
        [InlineData("DROP TABLE src.Job", "Only SELECT")]
        [InlineData("CREATE TABLE x (a int)", "Only SELECT")]
        [InlineData("EXEC sp_who", "Only SELECT")]
        [InlineData("EXEC('DELETE FROM src.Job')", "Only SELECT")]
        [InlineData("WITH x AS (SELECT * FROM src.Job) DELETE FROM x", "Only SELECT")]
        [InlineData("SELECT * INTO src.JobCopy FROM src.Job", "INTO")]
        [InlineData("SELECT * FROM src.Job; DELETE FROM src.Job", "Exactly one")]
        [InlineData("SELECT * FROM src.Job\nGO\nSELECT * FROM src.Press", "Exactly one")]
        [InlineData("SELECT 1; SELECT 2", "Exactly one")]
        [InlineData("SELECT * FROM src.Secret", "not a configured")]
        [InlineData("SELECT * FROM src.Job j JOIN sys.sql_logins l ON 1 = 1", "not a configured")]
        [InlineData("SELECT * FROM src.Job WHERE JobNo IN (SELECT name FROM sys.databases)", "not a configured")]
        [InlineData("SELECT * FROM Payroll.dbo.Orders", "another database")]
        [InlineData("SELECT * FROM Remote.Factory.dbo.Orders", "Linked-server")]
        [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=x;', 'SELECT 1')", "OPENROWSET")]
        [InlineData("SELECT * FROM OPENQUERY(Remote, 'DELETE FROM t')", "OPENQUERY")]
        [InlineData("SELECT * FROM OPENDATASOURCE('SQLNCLI', 'Data Source=x').db.dbo.t", "OPENDATASOURCE")]
        [InlineData("SELECT * FROM dbo.fn_GetOrders()", "Table-valued")]
        [InlineData("SELECT NEXT VALUE FOR dbo.OrderSeq", "NEXT VALUE FOR")]
        [InlineData("SELEC * FROM src.Job", "could not be parsed")]
        [InlineData("   ", "empty")]
        public void Guard_TurnsDownAnythingThatCouldWriteOrReadElsewhere(string sql, string expectedReason)
        {
            GuardResult result = Check(sql);
            Assert.False(result.IsAllowed);
            Assert.Contains(expectedReason, result.Reason, StringComparison.OrdinalIgnoreCase);
        }
    }
}
