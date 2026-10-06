using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;
using Shoebox.Api.Emit;
using Shoebox.Api.Run;
using Shoebox.Api.Topology;

namespace Shoebox.Api.UnitTests.Run
{
    /// <summary>
    /// A broken edge into a database has to read as a database failure to somebody
    /// who has only the telemetry: which query, which cause, and who it broke.
    /// </summary>
    [TestFixture]
    public class DatabaseFailureEmissionTests
    {
        private const string WrongColumn = @"
flowchart LR
  api[Orders API] -->|broken: wrong column| db[(SQL Server)]";

        private static List<Activity> Capture(string diagram, int runIndex = 1)
        {
            var captured = new List<Activity>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = captured.Add,
            };
            ActivitySource.AddActivityListener(listener);

            using var pool = new PodTracerPool(target: null);
            new TopologyRunner(pool).Run(MermaidParser.Parse(diagram), runIndex, "test");

            return captured;
        }

        private static Activity DbSpan(IEnumerable<Activity> spans) =>
            spans.Single(s => s.GetTagItem("db.system.name") is not null);

        [Test]
        public void The_Db_Span_Is_An_Error_Carrying_The_Statement_That_Failed()
        {
            var db = DbSpan(Capture(WrongColumn));

            db.Kind.Should().Be(ActivityKind.Client);
            db.Status.Should().Be(ActivityStatusCode.Error);
            db.StatusDescription.Should().Be("Invalid column name 'Discount'.");
            db.DisplayName.Should().Be("SELECT dbo.Orders");

            db.GetTagItem("db.system.name").Should().Be("microsoft.sql_server");
            db.GetTagItem("db.namespace").Should().Be("sql-server");
            db.GetTagItem("db.collection.name").Should().Be("dbo.Orders");
            db.GetTagItem("db.operation.name").Should().Be("SELECT");
            db.GetTagItem("db.query.text").Should().Be("SELECT Id, Status, Total, Discount FROM dbo.Orders WHERE Id = @Id");
            db.GetTagItem("db.response.status_code").Should().Be("207");
            db.GetTagItem("error.type").Should().Be("207");
        }

        [Test]
        public void The_Db_Span_Records_The_Exception_The_Driver_Would_Throw()
        {
            var ex = DbSpan(Capture(WrongColumn)).Events.Single(e => e.Name == "exception");
            var tags = ex.Tags.ToDictionary(t => t.Key, t => t.Value);

            tags["exception.type"].Should().Be("Microsoft.Data.SqlClient.SqlException");
            tags["exception.message"].Should().Be("Invalid column name 'Discount'.");

            var stack = (string)tags["exception.stacktrace"]!;
            stack.Should().StartWith("Microsoft.Data.SqlClient.SqlException (0x80131904): Invalid column name 'Discount'.");
            stack.Should().Contain("at Microsoft.Data.SqlClient.SqlCommand.ExecuteReaderAsync");
            stack.Should().Contain("at OrdersApi.Data.OrdersRepository.GetOrdersAsync(Int32 id)");
            stack.Should().EndWith("Error Number:207,State:1,Class:16");
        }

        [Test]
        public void A_Column_Named_In_The_Reason_Is_The_One_The_Query_Gets_Wrong()
        {
            var db = DbSpan(Capture(@"
flowchart LR
  api[Orders API] -->|broken: wrong column 'ShipDate'| db[(SQL Server)]"));

            db.StatusDescription.Should().Be("Invalid column name 'ShipDate'.");
            ((string)db.GetTagItem("db.query.text")!).Should().Contain("ShipDate");
        }

        [TestCase("wrong table", "208", "Invalid object name 'dbo.OrdersArchive'.", "FROM dbo.OrdersArchive")]
        [TestCase("syntax error", "102", "Incorrect syntax near '='.", "WHERE Id = = @Id")]
        [TestCase("division by zero", "8134", "Divide by zero error encountered.", "Total / Quantity")]
        public void Each_Sql_Example_Fails_With_Its_Own_Error_And_Query(string reason, string code, string message, string inQuery)
        {
            var db = DbSpan(Capture($@"
flowchart LR
  api[Orders API] -->|broken: {reason}| db[(SQL Server)]"));

            db.GetTagItem("db.response.status_code").Should().Be(code);
            db.StatusDescription.Should().Be(message);
            ((string)db.GetTagItem("db.query.text")!).Should().Contain(inQuery);
        }

        [Test]
        public void An_Unrecognised_Reason_Still_Fails_As_The_Drivers_Exception()
        {
            var db = DbSpan(Capture(@"
flowchart LR
  api[Orders API] -->|broken: login failed| db[(SQL Server)]"));

            db.StatusDescription.Should().Be("login failed");
            db.GetTagItem("db.response.status_code").Should().BeNull();
            db.GetTagItem("error.type").Should().Be("Microsoft.Data.SqlClient.SqlException");
            db.Events.Single().Name.Should().Be("exception");
        }

        [Test]
        public void Postgres_Fails_In_Its_Own_Words()
        {
            var db = DbSpan(Capture(@"
flowchart LR
  api[Orders API] -->|broken: wrong column| db[(Postgres)]"));

            db.GetTagItem("db.system.name").Should().Be("postgresql");
            db.GetTagItem("db.query.text").Should().Be("SELECT id, status, total, discount FROM orders WHERE id = $1");
            db.GetTagItem("db.response.status_code").Should().Be("42703");
            db.StatusDescription.Should().Be("42703: column \"discount\" does not exist");
            db.Events.Single().Tags.Should().Contain(new KeyValuePair<string, object?>("exception.type", "Npgsql.PostgresException"));
        }

        [Test]
        public void The_Caller_Fails_With_The_Same_Error()
        {
            var spans = Capture(WrongColumn);
            var caller = spans.Single(s => s.Kind == ActivityKind.Server);

            caller.Source.Name.Should().Contain("orders-api");
            caller.Status.Should().Be(ActivityStatusCode.Error);
            caller.StatusDescription.Should().Be("Invalid column name 'Discount'.");
        }

        [Test]
        public void A_Healthy_Call_To_The_Same_Database_Stays_Ok()
        {
            // Two services, one database, one broken edge. The failure is that
            // query, not that database.
            var spans = Capture(@"
flowchart LR
  gw[Gateway] --> orders[Orders API]
  gw --> billing[Billing API]
  orders -->|broken: wrong column| db[(SQL Server)]
  billing --> db");

            var dbSpans = spans.Where(s => s.GetTagItem("db.system.name") is not null).ToList();
            dbSpans.Should().HaveCount(2);

            var healthy = dbSpans.Single(s => s.Source.Name.Contains("billing-api"));
            healthy.Status.Should().Be(ActivityStatusCode.Unset);
            healthy.Events.Should().BeEmpty();
            healthy.GetTagItem("db.query.text").Should().Be("SELECT Id, Status, Total FROM dbo.Billing WHERE Id = @Id");
            healthy.GetTagItem("db.response.status_code").Should().BeNull();

            spans.Single(s => s.Source.Name.Contains("billing-api") && s.Kind == ActivityKind.Client && s != healthy)
                .Status.Should().Be(ActivityStatusCode.Unset, "billing's own span did nothing wrong");
            spans.Single(s => s.Source.Name.Contains("orders-api") && s.GetTagItem("db.system.name") is null)
                .Status.Should().Be(ActivityStatusCode.Error);
        }

        [Test]
        public void Broken_On_One_Instance_Fails_Only_That_Run()
        {
            const string diagram = @"
flowchart LR
  api[Orders API x3] -->|broken on #2: wrong column| db[(SQL Server)]";

            DbSpan(Capture(diagram, 1)).Status.Should().Be(ActivityStatusCode.Unset);
            DbSpan(Capture(diagram, 2)).Status.Should().Be(ActivityStatusCode.Error);
            DbSpan(Capture(diagram, 3)).Status.Should().Be(ActivityStatusCode.Unset);
        }

        [Test]
        public void A_Healthy_Db_Span_Carries_A_Real_Statement()
        {
            var db = DbSpan(Capture(@"
flowchart LR
  api[Orders API] --> db[(SQL Server)]"));

            db.Status.Should().Be(ActivityStatusCode.Unset);
            db.DisplayName.Should().Be("SELECT dbo.Orders");
            db.GetTagItem("db.query.text").Should().Be("SELECT Id, Status, Total FROM dbo.Orders WHERE Id = @Id");
        }
    }
}
