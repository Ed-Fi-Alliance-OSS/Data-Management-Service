// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;

namespace KeysetSpike;

/// <summary>
/// The full binding specification of one parameter, as the production code bound it. Plan
/// capture and replay bind from this and never infer a type from the value. The provider type
/// is stored as its numeric enum value because <see cref="NpgsqlDbType" /> array types are a
/// flag combination that does not round-trip through its name.
/// </summary>
public sealed record SpikeParameterSpec(
    string Name,
    object? Value,
    DbType DbType,
    string ProviderTypeName,
    int ProviderTypeCode,
    string? DataTypeName,
    int Size,
    byte Precision,
    byte Scale,
    ParameterDirection Direction,
    string? TypeName
)
{
    public string BareName => Name.TrimStart('@', ':');

    public static SpikeParameterSpec From(DbParameter parameter) =>
        parameter switch
        {
            SqlParameter sql => new SpikeParameterSpec(
                sql.ParameterName,
                sql.Value,
                sql.DbType,
                sql.SqlDbType.ToString(),
                (int)sql.SqlDbType,
                null,
                sql.Size,
                sql.Precision,
                sql.Scale,
                sql.Direction,
                string.IsNullOrEmpty(sql.TypeName) ? null : sql.TypeName
            ),
            NpgsqlParameter npgsql => new SpikeParameterSpec(
                npgsql.ParameterName,
                npgsql.Value,
                npgsql.DbType,
                npgsql.NpgsqlDbType.ToString(),
                (int)npgsql.NpgsqlDbType,
                npgsql.DataTypeName,
                npgsql.Size,
                npgsql.Precision,
                npgsql.Scale,
                npgsql.Direction,
                null
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported parameter type {parameter.GetType().FullName}."
            ),
        };

    /// <summary>Creates a provider parameter carrying every field of this specification.</summary>
    public DbParameter Bind(DbCommand command)
    {
        object value = Value ?? DBNull.Value;
        switch (command)
        {
            case SqlCommand sqlCommand:
            {
                SqlParameter parameter = new()
                {
                    ParameterName = Name,
                    SqlDbType = (SqlDbType)ProviderTypeCode,
                    Size = Size,
                    Precision = Precision,
                    Scale = Scale,
                    Direction = Direction,
                    Value = value,
                };
                if (TypeName is not null)
                {
                    parameter.TypeName = TypeName;
                }

                sqlCommand.Parameters.Add(parameter);
                return parameter;
            }
            case NpgsqlCommand npgsqlCommand:
            {
                NpgsqlParameter parameter = new()
                {
                    ParameterName = Name,
                    Size = Size,
                    Precision = Precision,
                    Scale = Scale,
                    Direction = Direction,
                    Value = value,
                };

                // Unknown is what Npgsql reports for an untyped null; leaving it unset sends it
                // exactly as production did.
                if ((NpgsqlDbType)ProviderTypeCode != NpgsqlDbType.Unknown)
                {
                    parameter.NpgsqlDbType = (NpgsqlDbType)ProviderTypeCode;
                }

                npgsqlCommand.Parameters.Add(parameter);
                return parameter;
            }
            default:
                throw new InvalidOperationException(
                    $"Unsupported command type {command.GetType().FullName}."
                );
        }
    }

    public JsonObject ToJson() =>
        new()
        {
            ["name"] = Name,
            ["value"] = SpikeJson.Value(Value),
            ["clrType"] = Value?.GetType().FullName,
            ["dbType"] = DbType.ToString(),
            ["providerType"] = ProviderTypeName,
            ["providerTypeCode"] = ProviderTypeCode,
            ["dataTypeName"] = DataTypeName,
            ["size"] = Size,
            ["precision"] = Precision,
            ["scale"] = Scale,
            ["direction"] = Direction.ToString(),
            ["typeName"] = TypeName,
        };
}

/// <summary>
/// One command the driver executed: its text, its driver-observed interval, and its binding
/// specification. <see cref="BindingSource" /> names where the specification came from.
/// </summary>
public sealed record SpikeCapturedCommand(
    int Sequence,
    string CommandText,
    double ElapsedMs,
    IReadOnlyList<SpikeParameterSpec>? Parameters,
    string? BindingSource
);

/// <summary>
/// Records every database command the in-process host executes, with a full binding
/// specification per parameter. This replaces the harness <c>DriverCommandObserver</c>, which
/// keeps only text and elapsed time.
/// <list type="bullet">
/// <item>SQL Server: the parameters are read from the <see cref="SqlCommand" /> object carried by
/// SqlClient's <c>WriteCommandBefore</c> diagnostic event, before execution.</item>
/// <item>PostgreSQL: Npgsql 8's activities carry only the statement text, so the text and
/// interval come from the "Npgsql" ActivitySource and the parameters come from the production
/// binding code itself. The harness recorders keep each request's <see cref="PageKeysetSpec" />
/// and <see cref="RelationalCommand" />; <see cref="AttachProductionBindings" /> binds them onto a
/// fresh <see cref="NpgsqlCommand" /> through <see cref="HydrationBatchBuilder.AddParameters" />
/// and through the same three steps the production relational executors take. The smoke run's
/// parity check proves the result matches what the server receives.</item>
/// </list>
/// </summary>
public sealed class SpikeCommandCapture : IDisposable
{
    public const string SqlClientObserved = "sqlclient-observed";
    public const string ProductionKeysetBinder = "production-keyset-binder";
    public const string ProductionRelationalBinder = "production-relational-binder";

    private readonly object _gate = new();
    private readonly List<SpikeCapturedCommand> _commands = [];
    private readonly PerfProvider _provider;
    private IDisposable? _subscription;

    private SpikeCommandCapture(PerfProvider provider) => _provider = provider;

    public static SpikeCommandCapture Start(PerfProvider provider)
    {
        SpikeCommandCapture capture = new(provider);
        capture._subscription =
            provider == PerfProvider.Postgresql
                ? capture.StartNpgsqlListener()
                : new SqlClientSubscriber(capture);
        return capture;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _commands.Count;
            }
        }
    }

    public IReadOnlyList<SpikeCapturedCommand> Commands
    {
        get
        {
            lock (_gate)
            {
                return [.. _commands];
            }
        }
    }

    public IReadOnlyList<SpikeCapturedCommand> CommandsSince(int baseline)
    {
        lock (_gate)
        {
            return [.. _commands.Skip(baseline)];
        }
    }

    public void Dispose() => _subscription?.Dispose();

    /// <summary>
    /// PostgreSQL only: attaches production binding specifications to the commands captured since
    /// <paramref name="baseline" />, using the keysets and relational commands the harness recorded
    /// for the same requests. Relational commands match by exact text; a hydration batch takes the
    /// next recorded keyset whose parameters it references. Commands from other seams (for example
    /// the cache lookup adapter) keep no specification and cannot be replayed.
    /// </summary>
    public void AttachProductionBindings(
        int baseline,
        IReadOnlyList<PageKeysetSpec> keysets,
        IReadOnlyList<RelationalCommand> relationalCommands
    )
    {
        if (_provider != PerfProvider.Postgresql)
        {
            return;
        }

        Queue<PageKeysetSpec> pendingKeysets = new(keysets);
        List<RelationalCommand> pendingRelational = [.. relationalCommands];
        lock (_gate)
        {
            for (int index = baseline; index < _commands.Count; index++)
            {
                SpikeCapturedCommand command = _commands[index];
                if (command.Parameters is not null)
                {
                    continue;
                }

                int relationalIndex = pendingRelational.FindIndex(candidate =>
                    candidate.CommandText == command.CommandText
                );
                if (relationalIndex >= 0)
                {
                    _commands[index] = command with
                    {
                        Parameters = FromProductionRelationalBinding(
                            _provider,
                            pendingRelational[relationalIndex]
                        ),
                        BindingSource = ProductionRelationalBinder,
                    };
                    pendingRelational.RemoveAt(relationalIndex);
                    continue;
                }

                if (pendingKeysets.TryPeek(out PageKeysetSpec? keyset))
                {
                    IReadOnlyList<SpikeParameterSpec> specs = FromProductionKeysetBinding(_provider, keyset);
                    if (specs.All(spec => ReferencesParameter(command.CommandText, spec.BareName)))
                    {
                        pendingKeysets.Dequeue();
                        _commands[index] = command with
                        {
                            Parameters = specs,
                            BindingSource = ProductionKeysetBinder,
                        };
                    }
                }
            }
        }
    }

    public static IReadOnlyList<SpikeParameterSpec> FromProductionKeysetBinding(
        PerfProvider provider,
        PageKeysetSpec keyset
    )
    {
        using DbCommand command = CreateProviderCommand(provider);
        HydrationBatchBuilder.AddParameters(command, keyset);
        return [.. command.Parameters.Cast<DbParameter>().Select(SpikeParameterSpec.From)];
    }

    /// <summary>
    /// The same three steps every production relational executor takes
    /// (<c>PostgresqlRelationalCommandExecutor.AddParameters</c>,
    /// <c>MssqlRelationalCommandExecutor.AddParameters</c>): name, value, then the command's own
    /// provider configuration.
    /// </summary>
    public static IReadOnlyList<SpikeParameterSpec> FromProductionRelationalBinding(
        PerfProvider provider,
        RelationalCommand relationalCommand
    )
    {
        using DbCommand command = CreateProviderCommand(provider);
        foreach (RelationalParameter parameter in relationalCommand.Parameters)
        {
            DbParameter dbParameter = command.CreateParameter();
            dbParameter.ParameterName = parameter.Name;
            dbParameter.Value = parameter.Value ?? DBNull.Value;
            parameter.ConfigureParameter?.Invoke(dbParameter);
            command.Parameters.Add(dbParameter);
        }

        return [.. command.Parameters.Cast<DbParameter>().Select(SpikeParameterSpec.From)];
    }

    public static DbCommand CreateProviderCommand(PerfProvider provider) =>
        provider == PerfProvider.Postgresql ? new NpgsqlCommand() : new SqlCommand();

    public static bool ReferencesParameter(string sql, string bareName) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            sql,
            "@" + System.Text.RegularExpressions.Regex.Escape(bareName) + "(?![A-Za-z0-9_])"
        );

    private void Record(string commandText, double elapsedMs, IReadOnlyList<SpikeParameterSpec>? parameters)
    {
        lock (_gate)
        {
            _commands.Add(
                new SpikeCapturedCommand(
                    _commands.Count,
                    commandText,
                    elapsedMs,
                    parameters,
                    parameters is null ? null : SqlClientObserved
                )
            );
        }
    }

    private IDisposable StartNpgsqlListener()
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                string? statement =
                    activity.GetTagItem("db.statement")?.ToString()
                    ?? activity.GetTagItem("db.query.text")?.ToString();
                if (statement is not null)
                {
                    Record(statement, activity.Duration.TotalMilliseconds, null);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class SqlClientSubscriber : IObserver<DiagnosticListener>, IDisposable
    {
        private readonly SpikeCommandCapture _owner;
        private readonly IDisposable _allListeners;
        private readonly List<IDisposable> _subscriptions = [];

        public SqlClientSubscriber(SpikeCommandCapture owner)
        {
            _owner = owner;
            _allListeners = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "SqlClientDiagnosticListener")
            {
                lock (_subscriptions)
                {
                    _subscriptions.Add(listener.Subscribe(new SqlClientEvents(_owner)));
                }
            }
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void Dispose()
        {
            _allListeners.Dispose();
            lock (_subscriptions)
            {
                foreach (IDisposable subscription in _subscriptions)
                {
                    subscription.Dispose();
                }

                _subscriptions.Clear();
            }
        }
    }

    private sealed class SqlClientEvents(SpikeCommandCapture owner) : IObserver<KeyValuePair<string, object?>>
    {
        private readonly ConcurrentDictionary<
            Guid,
            (long Start, string Text, IReadOnlyList<SpikeParameterSpec> Parameters)
        > _pending = new();

        public void OnNext(KeyValuePair<string, object?> value)
        {
            Guid? operationId = Read(value.Value, "OperationId") as Guid?;
            if (operationId is null)
            {
                return;
            }

            if (value.Key.EndsWith("WriteCommandBefore", StringComparison.Ordinal))
            {
                if (Read(value.Value, "Command") is SqlCommand command)
                {
                    _pending[operationId.Value] = (
                        Stopwatch.GetTimestamp(),
                        command.CommandText,
                        [.. command.Parameters.Cast<DbParameter>().Select(SpikeParameterSpec.From)]
                    );
                }
            }
            else if (value.Key.EndsWith("WriteCommandAfter", StringComparison.Ordinal))
            {
                if (_pending.TryRemove(operationId.Value, out var started))
                {
                    owner.Record(
                        started.Text,
                        Stopwatch.GetElapsedTime(started.Start).TotalMilliseconds,
                        started.Parameters
                    );
                }
            }
            else if (value.Key.EndsWith("WriteCommandError", StringComparison.Ordinal))
            {
                _pending.TryRemove(operationId.Value, out _);
            }
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        private static object? Read(object? payload, string propertyName) =>
            payload?.GetType().GetProperty(propertyName)?.GetValue(payload);
    }
}

/// <summary>JSON helpers shared by the spike's artifacts (LF-only, indented).</summary>
public static class SpikeJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
    };

    public static string Write(JsonNode node) => node.ToJsonString(Options);

    public static JsonNode? Value(object? value) =>
        value switch
        {
            null or DBNull => null,
            string text => text,
            bool flag => flag,
            int number => number,
            long number => number,
            short number => number,
            byte number => number,
            double number => number,
            decimal number => number,
            Guid guid => guid.ToString(),
            DateTime time => time.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset time => time.ToString("O", CultureInfo.InvariantCulture),
            IEnumerable sequence => new JsonArray([.. sequence.Cast<object?>().Select(Value)]),
            _ => value.ToString(),
        };

    public static JsonArray Parameters(IReadOnlyList<SpikeParameterSpec>? parameters) =>
        parameters is null
            ? []
            : new JsonArray([.. parameters.Select(parameter => (JsonNode)parameter.ToJson())]);
}
