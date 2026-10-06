using System.Text.RegularExpressions;
using Shoebox.Api.Topology;

namespace Shoebox.Api.Run;

/// <summary>The error a database sent back, as an exception event records it.</summary>
internal sealed record DatabaseError(string Type, string Message, string? Code, string StackTrace);

/// <summary>
/// What a call into a datastore looks like on the caller's CLIENT span: the
/// statement that ran and, when the diagram breaks the edge, what the database
/// said about it.
///
/// The diagram says <c>broken: wrong column</c>. The trace has to say which query
/// and which column, because the person reading it has never seen the diagram.
/// Everything here is derived from the diagram and nothing else, so a shared link
/// replays the same SQL and the same error.
/// </summary>
internal sealed partial record DatabaseCall(
    string System, string Namespace, string Collection, string QueryText, DatabaseError? Error)
{
    public const string SqlServer = "microsoft.sql_server";
    public const string Postgres = "postgresql";

    /// <summary>Semconv span name: <c>{db.operation.name} {db.collection.name}</c>.</summary>
    public string SpanName => $"SELECT {Collection}";

    public IEnumerable<KeyValuePair<string, object?>> Tags()
    {
        yield return new("db.system.name", System);
        yield return new("db.namespace", Namespace);
        yield return new("db.collection.name", Collection);
        yield return new("db.operation.name", "SELECT");
        yield return new("db.query.text", QueryText);

        // The node label is the host, as a service name in a cluster would be, on the
        // engine's default port. Deterministic, so a shared link names the same server.
        yield return new("server.address", Namespace);
        yield return new("server.port", System == SqlServer ? 1433 : 5432);
        if (Error?.Code is { } code) yield return new("db.response.status_code", code);
    }

    /// <summary>
    /// The read <paramref name="caller"/> makes against <paramref name="db"/>, and the
    /// error it gets back when <paramref name="failure"/> is set.
    ///
    /// The failure classes are the four the SQL examples already draw. The query
    /// changes along with the error, because a database rejects the statement it
    /// was sent: "Invalid column name" next to a query that names no such column
    /// would be the trace contradicting itself. A reason outside the four still
    /// fails as an exception of the right type, with the reason as its message.
    /// </summary>
    public static DatabaseCall Describe(Pod db, Pod? caller, string? failure)
    {
        var system = SystemOf(db);
        var mssql = system == SqlServer;
        var entity = Entity((caller ?? db).ServiceName);
        var table = mssql ? $"dbo.{entity}" : Snake(entity);
        string Cols(params string[] c) => string.Join(", ", mssql ? c : c.Select(Snake));
        var param = mssql ? "@Id" : "$1";
        var where = $"WHERE {(mssql ? "Id" : "id")} = {param}";

        var query = $"SELECT {Cols("Id", "Status", "Total")} FROM {table} {where}";
        if (failure is null) return new DatabaseCall(system, db.ServiceName, table, query, null);

        // A name in quotes, as in "broken: wrong column 'Discount'", is the one the query gets wrong.
        (string Code, string Message)? error = null;
        var reason = failure.ToLowerInvariant();
        var named = Quoted().Match(failure) is { Success: true } m ? m.Groups[1].Value : null;

        if (reason.Contains("column"))
        {
            var column = mssql ? named ?? "Discount" : Snake(named ?? "Discount");
            query = $"SELECT {Cols("Id", "Status", "Total")}, {column} FROM {table} {where}";
            error = mssql ? ("207", $"Invalid column name '{column}'.") : ("42703", $"column \"{column}\" does not exist");
        }
        else if (reason.Contains("table") || reason.Contains("object") || reason.Contains("relation"))
        {
            var wrong = named ?? (mssql ? $"dbo.{entity}Archive" : Snake($"{entity}Archive"));
            query = $"SELECT {Cols("Id", "Status", "Total")} FROM {wrong} {where}";
            error = mssql ? ("208", $"Invalid object name '{wrong}'.") : ("42P01", $"relation \"{wrong}\" does not exist");
        }
        else if (reason.Contains("syntax"))
        {
            query = $"SELECT {Cols("Id", "Status", "Total")} FROM {table} WHERE {(mssql ? "Id" : "id")} = = {param}";
            error = mssql ? ("102", "Incorrect syntax near '='.") : ("42601", "syntax error at or near \"=\"");
        }
        else if (reason.Contains("zero"))
        {
            query = $"SELECT {Cols("Id")}, {Cols("Total")} / {Cols("Quantity")} AS {Cols("UnitPrice")} FROM {table} {where}";
            error = mssql ? ("8134", "Divide by zero error encountered.") : ("22012", "division by zero");
        }

        var dbError = mssql
            ? SqlException(error?.Code, error?.Message ?? failure, caller, entity)
            : PostgresException(error?.Code, error?.Message ?? failure, caller, entity);

        return new DatabaseCall(system, db.ServiceName, table, query, dbError);
    }

    /// <summary>
    /// Read off the label, like the broker is. Postgres when the label names
    /// nothing else, which is what every datastore emitted before this.
    /// </summary>
    private static string SystemOf(Pod db)
    {
        var label = db.Label.ToLowerInvariant();
        return label.Contains("sql server") || label.Contains("sqlserver") || label.Contains("mssql") || label.Contains("azure sql")
            ? SqlServer
            : Postgres;
    }

    private static DatabaseError SqlException(string? code, string message, Pod? caller, string entity) =>
        new("Microsoft.Data.SqlClient.SqlException", message, code,
            $"Microsoft.Data.SqlClient.SqlException (0x80131904): {message}\n" +
            "   at Microsoft.Data.SqlClient.SqlConnection.OnError(SqlException exception, Boolean breakConnection, Action`1 wrapCloseInAction)\n" +
            "   at Microsoft.Data.SqlClient.TdsParser.ThrowExceptionAndWarning(TdsParserStateObject stateObj, Boolean callerHasConnectionLock, Boolean asyncClose)\n" +
            "   at Microsoft.Data.SqlClient.TdsParser.TryRun(RunBehavior runBehavior, SqlCommand cmdHandler, SqlDataReader dataStream, BulkCopySimpleResultSet bulkCopyHandler, TdsParserStateObject stateObj, Boolean& dataReady)\n" +
            "   at Microsoft.Data.SqlClient.SqlCommand.ExecuteReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)\n" +
            RepositoryFrame(caller, entity) +
            (code is null ? string.Empty : $"\nError Number:{code},State:1,Class:16"));

    private static DatabaseError PostgresException(string? code, string message, Pod? caller, string entity)
    {
        var full = code is null ? message : $"{code}: {message}";
        return new("Npgsql.PostgresException", full, code,
            $"Npgsql.PostgresException (0x80004005): {full}\n" +
            "   at Npgsql.Internal.NpgsqlConnector.ReadMessageLong(Boolean async, DataRowLoadingMode dataRowLoadingMode, Boolean readingNotifications, Boolean isReadingPrependedMessage)\n" +
            "   at Npgsql.NpgsqlDataReader.NextResult(Boolean async, Boolean isConsuming, CancellationToken cancellationToken)\n" +
            "   at Npgsql.NpgsqlCommand.ExecuteReader(Boolean async, CommandBehavior behavior, CancellationToken cancellationToken)\n" +
            RepositoryFrame(caller, entity));
    }

    /// <summary>The caller's own frame, so the stack names the service that sent the query.</summary>
    private static string RepositoryFrame(Pod? caller, string entity) =>
        $"   at {Pascal(caller?.ServiceName ?? "app")}.Data.{entity}Repository.Get{entity}Async(Int32 id)";

    /// <summary>orders-api reads the Orders table. The suffix names the process, not the data.</summary>
    private static string Entity(string serviceName)
    {
        var name = serviceName;
        foreach (var suffix in new[] { "-api", "-service", "-svc", "-app" })
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return Pascal(name);
    }

    private static string Pascal(string slug) =>
        string.Concat(slug.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    private static string Snake(string pascal) =>
        string.Concat(pascal.Select((c, i) => char.IsUpper(c) && i > 0 && pascal[i - 1] != '.' ? $"_{char.ToLowerInvariant(c)}" : $"{char.ToLowerInvariant(c)}"));

    [GeneratedRegex("'([^']+)'")]
    private static partial Regex Quoted();
}
