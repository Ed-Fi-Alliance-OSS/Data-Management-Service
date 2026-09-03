// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// Scoped service that provides the appropriate NpgsqlDataSource for the current request
/// by reading the effective target the request selected and leasing it from the singleton cache.
/// Effective-target assignment is write-once per request, so one lazily taken lease is all a scope
/// can ever need.
/// </summary>
/// <remarks>
/// The lease is held for the life of the request scope rather than for the life of one connection,
/// because several seams open connections from the same data source during one request and the data
/// source must not be disposed between them. The DI scope disposes this provider, which releases
/// the lease it took.
///
/// Both disposal interfaces are implemented deliberately. Releasing a lease is synchronous, so there
/// is nothing to await, and a service that is only asynchronously disposable makes a synchronous
/// <c>IServiceScope.Dispose()</c> throw rather than release anything - which would break every caller
/// that builds a synchronous scope around a database operation.
/// </remarks>
public sealed class NpgsqlDataSourceProvider(
    IDataStoreSelection dataStoreSelection,
    NpgsqlDataSourceCache dataSourceCache,
    ILogger<NpgsqlDataSourceProvider> logger
) : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Guards the lease and the disposed flag together, so concurrent seams reading the source
    /// through one scope cannot both observe no lease and each take one - the loser's lease would
    /// never be released, pinning a retired data source forever. Holding it across AcquireLease is
    /// safe: the cache synchronizes internally and never calls back into this provider.
    /// </summary>
    private readonly Lock _sync = new();

    private NpgsqlDataSourceLease? _lease;
    private bool _disposed;

    /// <summary>
    /// Gets the NpgsqlDataSource for the target the current request selected.
    /// </summary>
    /// <remarks>
    /// Leased by the target's connection string rather than the parent's id, because a parent and its
    /// derivatives share an id but are different databases. Reading the target throws when none was
    /// selected; there is deliberately no fallback to the parent. The first read takes the scope's one
    /// lease; the target is write-once, so a later read could only name the same database again.
    /// </remarks>
    public NpgsqlDataSource DataSource
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, typeof(NpgsqlDataSourceProvider));

                if (_lease is not null)
                {
                    return _lease.DataSource;
                }

                var target = dataStoreSelection.GetEffectiveTarget();

                logger.LogDebug(
                    "NpgsqlDataSourceProvider leasing a data source for a {TargetKind} target",
                    target.Kind
                );

                _lease = dataSourceCache.AcquireLease(target.ConnectionString);

                return _lease.DataSource;
            }
        }
    }

    /// <summary>
    /// Releases the lease this request took, if any. Called by the DI scope at the end of the request.
    /// </summary>
    public void Dispose() => ReleaseLease();

    /// <inheritdoc cref="Dispose" />
    public ValueTask DisposeAsync()
    {
        ReleaseLease();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The one release path both disposal forms use, so whichever the container calls - and even if it
    /// calls both - the lease is given back exactly once.
    /// </summary>
    private void ReleaseLease()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _lease?.Dispose();
            _lease = null;
        }
    }
}
