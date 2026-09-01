// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// A substituted provider lifetime that records every build, open, and disposal, can gate each of
/// them to drive an exact interleaving, and fails the run if any of them is entered while the cache's
/// state lock is held.
/// </summary>
/// <remarks>
/// The data sources it hands back are real <see cref="NpgsqlDataSource" /> objects. Building one
/// neither connects nor resolves a host, so identity, single-winner, and exactly-once-disposal
/// assertions are made against the same kind of object production uses rather than a stand-in.
/// </remarks>
internal sealed class GatedNpgsqlDataSourceLifetime : INpgsqlDataSourceLifetime, IDisposable
{
    private readonly object _sync = new();
    private readonly List<NpgsqlDataSource> _built = [];
    private readonly List<NpgsqlDataSource> _disposed = [];
    private readonly ConcurrentBag<string> _lockViolations = [];

    /// <summary>
    /// Reports whether the calling thread holds the cache's state lock, so provider work can assert
    /// it is not running under it. Received from the cache's test constructor, which is why
    /// construction passes <see cref="ReceiveStateLockProbe" /> as the probe receiver.
    /// </summary>
    private Func<bool> _isStateLockHeld = static () => false;

    public void ReceiveStateLockProbe(Func<bool> probe) => _isStateLockHeld = probe;

    /// <summary>Runs at the top of every build, before the data source is created.</summary>
    public Action<string>? OnBuild { get; set; }

    /// <summary>Runs at the top of every open.</summary>
    public Func<NpgsqlDataSource, CancellationToken, Task>? OnOpen { get; set; }

    /// <summary>Runs at the top of every disposal, before the data source is disposed.</summary>
    public Action<NpgsqlDataSource>? OnDispose { get; set; }

    /// <summary>Connection strings whose build throws instead of producing a data source.</summary>
    public HashSet<string> FailBuildFor { get; } = new(StringComparer.Ordinal);

    /// <summary>Data sources whose disposal throws after being recorded.</summary>
    public HashSet<NpgsqlDataSource> FailDisposeFor { get; } = [];

    public IReadOnlyList<NpgsqlDataSource> Built
    {
        get
        {
            lock (_sync)
            {
                return [.. _built];
            }
        }
    }

    public IReadOnlyList<NpgsqlDataSource> Disposed
    {
        get
        {
            lock (_sync)
            {
                return [.. _disposed];
            }
        }
    }

    /// <summary>
    /// Every provider operation that was entered while the state lock was held. Must always be empty:
    /// the whole no-stalled-databases argument for this cache rests on it.
    /// </summary>
    public IReadOnlyCollection<string> LockViolations => [.. _lockViolations];

    public int BuildCount => Built.Count;

    public int DisposeCountOf(NpgsqlDataSource dataSource) =>
        Disposed.Count(disposed => ReferenceEquals(disposed, dataSource));

    public NpgsqlDataSource Build(string connectionString)
    {
        RecordLockState(nameof(Build));
        OnBuild?.Invoke(connectionString);

        if (FailBuildFor.Contains(connectionString))
        {
            throw new InvalidOperationException("Simulated provider build failure.");
        }

        NpgsqlDataSource dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();

        lock (_sync)
        {
            _built.Add(dataSource);
        }

        return dataSource;
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken
    )
    {
        RecordLockState(nameof(OpenConnectionAsync));

        if (OnOpen is not null)
        {
            await OnOpen(dataSource, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Never opened, so no server is involved; disposal of an unopened connection is a no-op.
        return new NpgsqlConnection(dataSource.ConnectionString);
    }

    public void DisposeDataSource(NpgsqlDataSource dataSource)
    {
        RecordLockState(nameof(DisposeDataSource));
        OnDispose?.Invoke(dataSource);

        lock (_sync)
        {
            _disposed.Add(dataSource);
        }

        if (FailDisposeFor.Contains(dataSource))
        {
            throw new InvalidOperationException("Simulated provider disposal failure.");
        }

        dataSource.Dispose();
    }

    /// <summary>Disposes anything the cache never got around to, so a test leaves no pool behind.</summary>
    public void Dispose()
    {
        foreach (NpgsqlDataSource dataSource in Built)
        {
            try
            {
                dataSource.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed through the cache; nothing to do.
            }
        }
    }

    private void RecordLockState(string operation)
    {
        if (_isStateLockHeld())
        {
            _lockViolations.Add(operation);
        }
    }
}

/// <summary>
/// Captures formatted log messages so a test can assert what did - and did not - reach the log.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => [.. _messages];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        _messages.Enqueue($"{formatter(state, exception)} {exception}");
    }
}

/// <summary>
/// Stands in for a resource whose disposal fails, so an owner's cleanup ordering can be proven
/// without a database. The message names which resource, for the double-fault cases where only one
/// of two failures may propagate.
/// </summary>
internal sealed class ThrowingAsyncDisposable(string message = "Simulated transaction disposal failure.")
    : IAsyncDisposable
{
    public ValueTask DisposeAsync() => throw new InvalidOperationException(message);
}

/// <summary>Stands in for a transaction that disposes normally, counting how often it was asked to.</summary>
internal sealed class RecordingAsyncDisposable : IAsyncDisposable
{
    public int DisposeCount { get; private set; }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Disposes one handle twice, to prove disposal is idempotent. Written as a loop over two references
/// to the same handle rather than as two statements, because two statements read as a redundant
/// disposal and this is the property under test.
/// </summary>
internal static class DoubleDisposal
{
    public static void Of(IDisposable handle)
    {
        IDisposable[] twice = [handle, handle];

        foreach (IDisposable each in twice)
        {
            each.Dispose();
        }
    }

    /// <summary>
    /// Disposes one handle through both disposal forms, for a type that implements each and must
    /// still release only once.
    /// </summary>
    public static async Task ThroughBothFormsAsync<T>(T handle)
        where T : IDisposable, IAsyncDisposable
    {
        IDisposable[] synchronously = [handle];

        foreach (IDisposable each in synchronously)
        {
            each.Dispose();
        }

        IAsyncDisposable[] asynchronously = [handle];

        foreach (IAsyncDisposable each in asynchronously)
        {
            await each.DisposeAsync();
        }
    }

    public static async Task OfAsync(IAsyncDisposable handle)
    {
        IAsyncDisposable[] twice = [handle, handle];

        foreach (IAsyncDisposable each in twice)
        {
            await each.DisposeAsync();
        }
    }
}

internal static class OwnershipSnapshots
{
    /// <summary>One owner per connection string, all under one tenant and parent.</summary>
    public static DataStoreOwnershipSnapshot Of(long version, params string[] connectionStrings) =>
        new(
            version,
            [
                .. connectionStrings.Select(connectionString => new ConfiguredTargetOwner(
                    "tenant-a",
                    1,
                    EffectiveTargetKind.Primary,
                    connectionString
                )),
            ]
        );

    /// <summary>An explicit owner list, for the shared-string and multi-tenant cases.</summary>
    public static DataStoreOwnershipSnapshot Of(long version, params ConfiguredTargetOwner[] owners) =>
        new(version, [.. owners]);

    public static ConfiguredTargetOwner Owner(
        string tenantKey,
        long parentDataStoreId,
        EffectiveTargetKind kind,
        string connectionString
    ) => new(tenantKey, parentDataStoreId, kind, connectionString);

    public static DataStoreOwnershipSnapshot Empty(long version) =>
        new(version, ImmutableArray<ConfiguredTargetOwner>.Empty);
}
