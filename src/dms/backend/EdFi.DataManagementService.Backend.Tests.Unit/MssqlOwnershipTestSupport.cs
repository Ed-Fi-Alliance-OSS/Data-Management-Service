// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.Mssql;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// A substituted pool-clearing adapter that records every exact string it was asked to clear, can be
/// gated so a clear is held open, can be made to throw, and fails the run if it is entered while the
/// acquisition's state lock is held.
/// </summary>
internal sealed class GatedSqlServerPoolClearing : ISqlServerPoolClearing
{
    private readonly ConcurrentQueue<string> _cleared = new();
    private readonly ConcurrentBag<string> _lockViolations = [];

    /// <summary>
    /// Reports whether the calling thread holds the acquisition's state lock, so a clear can assert
    /// it is not running under it. Received from the acquisition's observer at construction.
    /// </summary>
    public Func<bool> IsStateLockHeld { get; set; } = static () => false;

    /// <summary>Runs at the top of every clear, before it is recorded.</summary>
    public Action<string>? OnClear { get; set; }

    /// <summary>Effective strings whose clear throws after being recorded.</summary>
    public HashSet<string> FailClearFor { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Cleared => [.. _cleared];

    public IReadOnlyCollection<string> LockViolations => [.. _lockViolations];

    public int ClearCountOf(string effectiveConnectionString) =>
        Cleared.Count(cleared => string.Equals(cleared, effectiveConnectionString, StringComparison.Ordinal));

    public void ClearPool(string effectiveConnectionString)
    {
        if (IsStateLockHeld())
        {
            _lockViolations.Add(nameof(ClearPool));
        }

        OnClear?.Invoke(effectiveConnectionString);
        _cleared.Enqueue(effectiveConnectionString);

        if (FailClearFor.Contains(effectiveConnectionString))
        {
            throw new InvalidOperationException("Simulated pool clearing failure.");
        }
    }
}

/// <summary>
/// A connection that never reaches a server, so the lease lifecycle can be exercised without one. It
/// also records whether it was constructed while the state lock was held.
/// </summary>
internal sealed class OwnershipProbeConnection : DbConnection
{
    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

    public override string Database => string.Empty;

    public override string DataSource => string.Empty;

    public override string ServerVersion => string.Empty;

    public override ConnectionState State => ConnectionState.Closed;

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    public override void Close() { }

    public override void Open() { }

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException();

    protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
}
