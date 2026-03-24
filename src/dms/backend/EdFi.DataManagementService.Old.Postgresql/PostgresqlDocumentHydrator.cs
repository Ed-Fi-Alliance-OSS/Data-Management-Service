// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Old.Postgresql;

/// <summary>
/// PostgreSQL implementation of <see cref="IDocumentHydrator"/> that executes compiled
/// hydration read plans against the per-request PostgreSQL data source.
/// </summary>
internal sealed class PostgresqlDocumentHydrator(NpgsqlDataSourceProvider dataSourceProvider)
    : IDocumentHydrator
{
    public async Task<HydratedPage> HydrateAsync(
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        CancellationToken ct
    )
    {
        await using var connection = await dataSourceProvider.DataSource.OpenConnectionAsync(ct);
        return await HydrationExecutor.ExecuteAsync(connection, plan, keyset, SqlDialect.Pgsql, ct);
    }
}
