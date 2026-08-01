// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Composite;

/// <summary>
/// A write session that answers each command with the next scripted response — a
/// <see cref="DbDataReader"/> to return or a <see cref="DbException"/> to throw — and records every
/// <see cref="RelationalCommand"/> it was asked to create.
/// </summary>
/// <remarks>
/// Recording at <see cref="CreateCommand"/> is what lets a fixture assert the exact command stream a
/// phase issues, which is the property the batching work exists to hold. <see cref="Connection"/> and
/// <see cref="Transaction"/> are null because no scripted response needs a provider, so
/// <see cref="CreateCommandExecutor"/> is overridden rather than inheriting the default that derives its
/// dialect from the connection.
/// </remarks>
internal sealed class ScriptedWriteSession(params object[] scripts) : IRelationalWriteSession
{
    private readonly Queue<object> _scripts = new(scripts);

    public DbConnection Connection { get; } = null!;

    public DbTransaction Transaction { get; } = null!;

    /// <summary>The dialect the session-scoped command executor reports.</summary>
    public SqlDialect Dialect { get; init; } = SqlDialect.Pgsql;

    /// <summary>Every command the session was asked to create, in order.</summary>
    public List<RelationalCommand> Commands { get; } = [];

    public int CommitCount { get; private set; }

    public int RollbackCount { get; private set; }

    public DbCommand CreateCommand(RelationalCommand command)
    {
        Commands.Add(command);

        if (_scripts.Count == 0)
        {
            throw new InvalidOperationException($"No command script remains for command {Commands.Count}.");
        }

        return new ScriptedDbCommand(_scripts.Dequeue());
    }

    public IRelationalCommandExecutor CreateCommandExecutor() => new ScriptedRelationalCommandExecutor(this);

    public Task CommitAsync(CancellationToken cancellationToken = default)
    {
        CommitCount++;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        RollbackCount++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class ScriptedRelationalCommandExecutor(ScriptedWriteSession session)
        : IRelationalCommandExecutor
    {
        public SqlDialect Dialect => session.Dialect;

        public async Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            await using var dbCommand = session.CreateCommand(command);
            await using var dbReader = await dbCommand
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var reader = new DbRelationalCommandReader(dbReader);

            return await readAsync(reader, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// A command whose execution returns one scripted <see cref="DbDataReader"/> or throws one scripted
/// <see cref="DbException"/>.
/// </summary>
internal sealed class ScriptedDbCommand(object script) : DbCommand
{
    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; }

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection { get; set; }

    protected override DbParameterCollection DbParameterCollection { get; } =
        new ScriptedDbParameterCollection();

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel() { }

    public override int ExecuteNonQuery() => throw new NotSupportedException();

    /// <summary>
    /// The scripted reader's first cell, which is what a single-value command — the one-key collection
    /// reservation — reads.
    /// </summary>
    public override object? ExecuteScalar()
    {
        var reader = (DbDataReader)script;

        return reader.Read() ? reader.GetValue(0) : null;
    }

    public override void Prepare() { }

    protected override DbParameter CreateDbParameter() => new ScriptedDbParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        script switch
        {
            DbException exception => throw exception,
            DbDataReader reader => reader,
            _ => throw new InvalidOperationException("Unsupported command script."),
        };

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ExecuteDbDataReader(behavior));
    }
}

internal sealed class ScriptedDbParameterCollection : DbParameterCollection
{
    private readonly List<object> _parameters = [];

    public override int Count => _parameters.Count;

    public override object SyncRoot => this;

    public override int Add(object value)
    {
        _parameters.Add(value);
        return _parameters.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var value in values)
        {
            Add(value!);
        }
    }

    public override void Clear() => _parameters.Clear();

    public override bool Contains(object value) => _parameters.Contains(value);

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index) => _parameters.ToArray().CopyTo(array, index);

    public override System.Collections.IEnumerator GetEnumerator() => _parameters.GetEnumerator();

    public override int IndexOf(object value) => _parameters.IndexOf(value);

    public override int IndexOf(string parameterName) =>
        _parameters.FindIndex(parameter =>
            parameter is DbParameter dbParameter && dbParameter.ParameterName == parameterName
        );

    public override void Insert(int index, object value) => _parameters.Insert(index, value);

    public override void Remove(object value) => _parameters.Remove(value);

    public override void RemoveAt(int index) => _parameters.RemoveAt(index);

    public override void RemoveAt(string parameterName) => _parameters.RemoveAt(IndexOf(parameterName));

    protected override DbParameter GetParameter(int index) => (DbParameter)_parameters[index];

    protected override DbParameter GetParameter(string parameterName) =>
        (DbParameter)_parameters[IndexOf(parameterName)];

    protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);

        if (index < 0)
        {
            Add(value);
        }
        else
        {
            _parameters[index] = value;
        }
    }
}

internal sealed class ScriptedDbParameter : DbParameter
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

    public override void ResetDbType() { }
}
