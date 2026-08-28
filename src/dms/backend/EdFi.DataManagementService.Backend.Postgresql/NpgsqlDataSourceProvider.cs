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
/// Uses a Dictionary cache to handle potential scope issues where the provider may be
/// used across different target contexts.
/// </summary>
/// <remarks>
/// The lease is held for the life of the request scope rather than for the life of one connection,
/// because several seams open connections from the same data source during one request and the data
/// source must not be disposed between them. The DI scope disposes this provider, which releases
/// every lease it took.
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
    private readonly Dictionary<string, NpgsqlDataSourceLease> _leases = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>
    /// Gets the NpgsqlDataSource for the target the current request selected.
    /// Reads the target on each access to handle cases where selection may occur in a
    /// different scope context.
    /// </summary>
    /// <remarks>
    /// Keyed by the target's connection string rather than the parent's id, because a parent and its
    /// derivatives share an id but are different databases. Reading the target throws when none was
    /// selected; there is deliberately no fallback to the parent.
    /// </remarks>
    public NpgsqlDataSource DataSource
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, typeof(NpgsqlDataSourceProvider));

            // Always read the current target to handle potential scope issues
            var target = dataStoreSelection.GetEffectiveTarget();

            // Check if we have already leased this target's data source for this request
            if (_leases.TryGetValue(target.ConnectionString, out NpgsqlDataSourceLease? held))
            {
                return held.DataSource;
            }

            logger.LogDebug(
                "NpgsqlDataSourceProvider leasing a data source for a {TargetKind} target",
                target.Kind
            );

            NpgsqlDataSourceLease lease = dataSourceCache.AcquireLease(target.ConnectionString);
            _leases[target.ConnectionString] = lease;

            return lease.DataSource;
        }
    }

    /// <summary>
    /// Releases every lease this request took. Called by the DI scope at the end of the request.
    /// </summary>
    public void Dispose() => ReleaseLeases();

    /// <inheritdoc cref="Dispose" />
    public ValueTask DisposeAsync()
    {
        ReleaseLeases();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The one release path both disposal forms use, so whichever the container calls - and even if it
    /// calls both - every lease is given back exactly once.
    /// </summary>
    private void ReleaseLeases()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (NpgsqlDataSourceLease lease in _leases.Values)
        {
            lease.Dispose();
        }

        _leases.Clear();
    }
}
