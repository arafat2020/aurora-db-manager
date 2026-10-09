using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace AuroraDbManager.Api.Tests.Databases;

/// <summary>
/// A database server reduced to what a database manager uses, behind ADO.NET's abstract classes:
/// an existence query with one parameter, <c>CREATE DATABASE</c> and <c>DROP DATABASE</c>. It
/// reads identifiers the way the engine would, by its quote character, so a statement whose
/// quoting is wrong ends up naming the wrong database or is not understood at all.
/// </summary>
public sealed class FakeSqlServer(char identifierQuote, bool permissive = false)
{
    /// <summary>
    /// In permissive mode, what a query returns as its single value; null for no rows. Permissive
    /// mode is for callers whose statements this server does not interpret: it records every
    /// statement, answers queries from here, and changes nothing.
    /// </summary>
    public Func<string, object?> Scalars { get; set; } = sql => sql == "SELECT 1" ? 1 : null;

    public HashSet<string> Databases { get; } = [];

    /// <summary>Every statement executed, in order.</summary>
    public List<string> Statements { get; } = [];

    /// <summary>The parameters of every command executed, by name, in the order of <see cref="Statements"/>.</summary>
    public List<IReadOnlyDictionary<string, object?>> Parameters { get; } = [];

    /// <summary>What a query read row by row returns: the values of its single column. Nothing by default.</summary>
    public Func<string, IReadOnlyList<string>> Rows { get; set; } = _ => [];

    public List<string> ConnectionStrings { get; } = [];

    public int OpenConnections { get; private set; }

    /// <summary>When set, opening a connection throws it.</summary>
    public Exception? OpenFailure { get; set; }

    /// <summary>Given a statement, returns what executing it throws, or null to execute it.</summary>
    public Func<string, Exception?> StatementFailure { get; set; } = _ => null;

    public DbConnection Connect(string connectionString)
    {
        ConnectionStrings.Add(connectionString);
        return new Connection(this, connectionString);
    }

    private object? Query(string sql, IReadOnlyList<DbParameter> parameters)
    {
        Run(sql);

        if (permissive)
        {
            return Scalars(sql);
        }

        // The name must arrive as a parameter, never as part of the statement.
        var name = Assert.IsType<string>(Assert.Single(parameters, parameter => parameter.ParameterName == "name").Value);
        Assert.DoesNotContain(name, sql);
        return Databases.Contains(name) ? 1 : null;
    }

    private void Execute(string sql)
    {
        Run(sql);

        if (permissive)
        {
            return;
        }

        if (TryReadName(sql, "CREATE DATABASE IF NOT EXISTS ", out var name, out var rest) && rest.Length == 0)
        {
            Databases.Add(name);
        }
        else if (TryReadName(sql, "CREATE DATABASE ", out name, out rest) && rest.Length == 0)
        {
            if (!Databases.Add(name))
            {
                throw new InvalidOperationException($"database \"{name}\" already exists");
            }
        }
        else if (TryReadName(sql, "DROP DATABASE IF EXISTS ", out name, out rest) && rest is "" or " WITH (FORCE)")
        {
            Databases.Remove(name);
        }
        else
        {
            throw new NotSupportedException($"The fake server does not understand: {sql}");
        }
    }

    private void Run(string sql)
    {
        Statements.Add(sql);
        if (StatementFailure(sql) is { } failure)
        {
            throw failure;
        }
    }

    /// <summary>Reads the quoted identifier after <paramref name="prefix"/>; a doubled quote is one literal quote.</summary>
    private bool TryReadName(string sql, string prefix, out string name, out string rest)
    {
        name = rest = string.Empty;
        if (!sql.StartsWith(prefix, StringComparison.Ordinal) || sql.Length <= prefix.Length || sql[prefix.Length] != identifierQuote)
        {
            return false;
        }

        var builder = new System.Text.StringBuilder();
        for (var i = prefix.Length + 1; i < sql.Length; i++)
        {
            if (sql[i] != identifierQuote)
            {
                builder.Append(sql[i]);
            }
            else if (i + 1 < sql.Length && sql[i + 1] == identifierQuote)
            {
                builder.Append(identifierQuote);
                i++;
            }
            else
            {
                name = builder.ToString();
                rest = sql[(i + 1)..];
                return true;
            }
        }

        return false;
    }

    private sealed class Connection(FakeSqlServer server, string connectionString) : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;

        [AllowNull]
        public override string ConnectionString { get; set; } = connectionString;

        public override string Database => string.Empty;

        public override string DataSource => "fake";

        public override string ServerVersion => "0";

        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Open()
        {
            if (server.OpenFailure is not null)
            {
                throw server.OpenFailure;
            }

            _state = ConnectionState.Open;
            server.OpenConnections++;
        }

        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Open();
            return Task.CompletedTask;
        }

        public override void Close()
        {
            if (_state == ConnectionState.Open)
            {
                _state = ConnectionState.Closed;
                server.OpenConnections--;
            }
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Close();
            base.Dispose(disposing);
        }

        protected override DbCommand CreateDbCommand() => new Command(server, this);
    }

    private sealed class Command(FakeSqlServer server, Connection connection) : DbCommand
    {
        private readonly ParameterCollection _parameters = new();

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; } = CommandType.Text;

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; } = connection;

        protected override DbParameterCollection DbParameterCollection => _parameters;

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override void Prepare()
        {
        }

        public override int ExecuteNonQuery()
        {
            EnsureOpen();
            server.Parameters.Add(_parameters.Items.ToDictionary(parameter => parameter.ParameterName, parameter => parameter.Value));
            server.Execute(CommandText);
            return 0;
        }

        public override object? ExecuteScalar()
        {
            EnsureOpen();
            server.Parameters.Add(_parameters.Items.ToDictionary(parameter => parameter.ParameterName, parameter => parameter.Value));
            return server.Query(CommandText, _parameters.Items);
        }

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ExecuteNonQuery());
        }

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ExecuteScalar());
        }

        protected override DbParameter CreateDbParameter() => new Parameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            EnsureOpen();
            server.Parameters.Add(_parameters.Items.ToDictionary(parameter => parameter.ParameterName, parameter => parameter.Value));
            server.Run(CommandText);

            var table = new DataTable();
            table.Columns.Add("value", typeof(string));
            foreach (var value in server.Rows(CommandText))
            {
                table.Rows.Add(value);
            }

            return table.CreateDataReader();
        }

        private void EnsureOpen() => Assert.Equal(ConnectionState.Open, connection.State);
    }

    private sealed class Parameter : DbParameter
    {
        public override DbType DbType { get; set; }

        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

        public override bool IsNullable { get; set; }

        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;

        public override int Size { get; set; }

        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;

        public override bool SourceColumnNullMapping { get; set; }

        public override object? Value { get; set; }

        public override void ResetDbType()
        {
        }
    }

    private sealed class ParameterCollection : DbParameterCollection
    {
        public List<DbParameter> Items { get; } = [];

        public override int Count => Items.Count;

        public override object SyncRoot => Items;

        public override int Add(object value)
        {
            Items.Add((DbParameter)value);
            return Items.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (var value in values)
            {
                Add(value);
            }
        }

        public override void Clear() => Items.Clear();

        public override bool Contains(object value) => Items.Contains((DbParameter)value);

        public override bool Contains(string value) => IndexOf(value) >= 0;

        public override void CopyTo(Array array, int index) => ((ICollection)Items).CopyTo(array, index);

        public override IEnumerator GetEnumerator() => Items.GetEnumerator();

        public override int IndexOf(object value) => Items.IndexOf((DbParameter)value);

        public override int IndexOf(string parameterName) => Items.FindIndex(item => item.ParameterName == parameterName);

        public override void Insert(int index, object value) => Items.Insert(index, (DbParameter)value);

        public override void Remove(object value) => Items.Remove((DbParameter)value);

        public override void RemoveAt(int index) => Items.RemoveAt(index);

        public override void RemoveAt(string parameterName) => Items.RemoveAt(IndexOf(parameterName));

        protected override DbParameter GetParameter(int index) => Items[index];

        protected override DbParameter GetParameter(string parameterName) => Items[IndexOf(parameterName)];

        protected override void SetParameter(int index, DbParameter value) => Items[index] = value;

        protected override void SetParameter(string parameterName, DbParameter value) => Items[IndexOf(parameterName)] = value;
    }
}
