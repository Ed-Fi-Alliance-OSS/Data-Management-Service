// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using EdFi.DataManagementService.Performance.Harness.Configuration;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// One database command the driver reported: its full text and elapsed milliseconds.
/// </summary>
public sealed record ObservedDbCommand(string CommandText, double ElapsedMs);

/// <summary>
/// Driver-level command observation with zero production-code changes. PostgreSQL commands
/// arrive through Npgsql's "Npgsql" ActivitySource (only activities carrying a statement tag
/// are recorded); SQL Server commands arrive through the long-stable
/// SqlClientDiagnosticListener before/after event pair. Whether the live drivers actually
/// emit these signals is proven by an explicit probe per provider before any evidence run —
/// if a probe fails, the harness stops rather than substituting a weaker source.
/// </summary>
public sealed class DriverCommandObserver : IDisposable
{
    private readonly object _gate = new();
    private readonly List<ObservedDbCommand> _commands = [];
    private IDisposable? _subscription;

    private DriverCommandObserver() { }

    public static DriverCommandObserver Start(PerfProvider provider)
    {
        DriverCommandObserver observer = new();
        observer._subscription =
            provider == PerfProvider.Postgresql
                ? observer.StartNpgsqlListener()
                : observer.StartSqlClientListener();
        return observer;
    }

    public IReadOnlyList<ObservedDbCommand> Commands
    {
        get
        {
            lock (_gate)
            {
                return [.. _commands];
            }
        }
    }

    public void Dispose() => _subscription?.Dispose();

    private void Record(string commandText, double elapsedMs)
    {
        lock (_gate)
        {
            _commands.Add(new ObservedDbCommand(commandText, elapsedMs));
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
                    Record(statement, activity.Duration.TotalMilliseconds);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private IDisposable StartSqlClientListener() => new SqlClientListenerSubscriber(this);

    private sealed class SqlClientListenerSubscriber : IObserver<DiagnosticListener>, IDisposable
    {
        private readonly DriverCommandObserver _owner;
        private readonly IDisposable _allListeners;
        private readonly List<IDisposable> _subscriptions = [];

        public SqlClientListenerSubscriber(DriverCommandObserver owner)
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
                    _subscriptions.Add(listener.Subscribe(new SqlClientEventObserver(_owner)));
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

    private sealed class SqlClientEventObserver(DriverCommandObserver owner)
        : IObserver<KeyValuePair<string, object?>>
    {
        private readonly ConcurrentDictionary<Guid, (long StartTimestamp, string CommandText)> _pending =
            new();

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key.EndsWith("WriteCommandBefore", StringComparison.Ordinal))
            {
                Guid? operationId = ReadProperty(value.Value, "OperationId") as Guid?;
                string? commandText = (ReadProperty(value.Value, "Command") as DbCommand)?.CommandText;
                if (operationId is not null && commandText is not null)
                {
                    _pending[operationId.Value] = (Stopwatch.GetTimestamp(), commandText);
                }
            }
            else if (value.Key.EndsWith("WriteCommandAfter", StringComparison.Ordinal))
            {
                Guid? operationId = ReadProperty(value.Value, "OperationId") as Guid?;
                if (
                    operationId is not null
                    && _pending.TryRemove(
                        operationId.Value,
                        out (long StartTimestamp, string CommandText) started
                    )
                )
                {
                    owner.Record(
                        started.CommandText,
                        Stopwatch.GetElapsedTime(started.StartTimestamp).TotalMilliseconds
                    );
                }
            }
            else if (value.Key.EndsWith("WriteCommandError", StringComparison.Ordinal))
            {
                // A failed command is not a measurement; the request-level failure surfaces
                // through the measured operation itself.
                Guid? operationId = ReadProperty(value.Value, "OperationId") as Guid?;
                if (operationId is not null)
                {
                    _pending.TryRemove(operationId.Value, out _);
                }
            }
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        private static object? ReadProperty(object? payload, string propertyName) =>
            payload?.GetType().GetProperty(propertyName)?.GetValue(payload);
    }
}
