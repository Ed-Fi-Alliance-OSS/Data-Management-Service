// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

/// <summary>
/// The three pieces of provider work the cache performs: building a data source, opening a connection
/// from one, and disposing one.
/// </summary>
/// <remarks>
/// Collected behind one seam because they share a single structural rule - none of them may run while
/// the cache's state lock is held - and because that rule, and the build-race and cleanup orderings
/// that depend on it, can only be proven by a substitute that observes when each is called. In
/// production this is <see cref="NpgsqlDataSourceLifetime" /> and nothing else.
/// </remarks>
internal interface INpgsqlDataSourceLifetime
{
    NpgsqlDataSource Build(string connectionString);

    Task<NpgsqlConnection> OpenConnectionAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken
    );

    void DisposeDataSource(NpgsqlDataSource dataSource);
}

/// <summary>
/// The production lifetime: real Npgsql building, opening, and disposal, with the connection-pool
/// tuning DMS applies to every data source it owns.
/// </summary>
internal sealed class NpgsqlDataSourceLifetime : INpgsqlDataSourceLifetime
{
    public static readonly NpgsqlDataSourceLifetime Instance = new();

    private NpgsqlDataSourceLifetime() { }

    public NpgsqlDataSource Build(string connectionString)
    {
        NpgsqlDataSourceBuilder builder = new(connectionString);
        NpgsqlConnectionStringBuilder csb = builder.ConnectionStringBuilder;

        // Skip RESET/DISCARD when returning pooled connections, we manage session state explicitly.
        csb.NoResetOnClose = true;

        // Make PostgreSQL monitoring output more readable
        if (string.IsNullOrWhiteSpace(csb.ApplicationName))
        {
            csb.ApplicationName = "EdFi.DMS";
        }

        // Let Npgsql handle plan caching automatically
        csb.AutoPrepareMinUsages = 3;
        csb.MaxAutoPrepare = 256;

        return builder.Build();
    }

    public Task<NpgsqlConnection> OpenConnectionAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken
    ) => dataSource.OpenConnectionAsync(cancellationToken).AsTask();

    public void DisposeDataSource(NpgsqlDataSource dataSource) => dataSource.Dispose();
}
