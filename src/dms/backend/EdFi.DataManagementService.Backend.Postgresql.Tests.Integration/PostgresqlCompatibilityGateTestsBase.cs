// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// PostgreSQL-specific base class for compatibility gate integration tests.
/// Extends <see cref="CompatibilityGateTestsBase"/> with PostgreSQL-specific SQL quoting,
/// database provisioning via <see cref="PostgresqlGeneratedDdlTestDatabase"/>, and
/// bulk INSERT row restore logic.
///
/// These tests mirror the production startup validation flow described in
/// <c>reference/design/backend-redesign/design-docs/new-startup-flow.md</c> "End-to-End Startup Sequence (Detailed)", exercising the same <c>ResourceKeyValidator</c>
/// invoked by <c>ValidateResourceKeySeedMiddleware</c>. Results are cached by
/// <c>ResourceKeyValidationCacheProvider</c> and <c>DatabaseFingerprintProvider</c>
/// in production.
/// </summary>
public abstract class PostgresqlCompatibilityGateTestsBase : CompatibilityGateTestsBase
{
    private PostgresqlGeneratedDdlTestDatabase _database = null!;

    /// <summary>
    /// The readers below lease their connections from this, the same way production does. One cache per
    /// fixture, so a reader created more than once still shares the data source rather than building a
    /// second pool for the same database.
    /// </summary>
    private readonly NpgsqlDataSourceCache _dataSourceCache = new(NullLogger<NpgsqlDataSourceCache>.Instance);

    protected override string ResourceKeyTable => "dms.\"ResourceKey\"";
    protected override string ResourceKeyIdColumn => "\"ResourceKeyId\"";
    protected override string ResourceNameColumn => "\"ResourceName\"";
    protected override string ProjectNameColumn => "\"ProjectName\"";
    protected override string ResourceVersionColumn => "\"ResourceVersion\"";

    protected override SqlDialect GetSqlDialect() => SqlDialect.Pgsql;

    protected override IResourceKeyRowReader CreateResourceKeyRowReader() =>
        new PostgresqlResourceKeyRowReader(
            _dataSourceCache,
            NullLogger<PostgresqlResourceKeyRowReader>.Instance
        );

    protected override IDatabaseFingerprintReader CreateDatabaseFingerprintReader() =>
        new PostgresqlDatabaseFingerprintReader(
            _dataSourceCache,
            NullLogger<PostgresqlDatabaseFingerprintReader>.Instance
        );

    protected override async Task ExecuteTamperAsync(string sql)
    {
        await _database.ExecuteNonQueryAsync(sql);
    }

    protected override string GetConnectionString() => _database.ConnectionString;

    protected override async Task ProvisionDatabaseAsync(string ddl)
    {
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(ddl);
    }

    protected override async Task DisposeDatabaseAsync()
    {
        // Before the database, so nothing this fixture leased outlives the database it was leased
        // against.
        _dataSourceCache.Dispose();

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    protected override async Task RestoreResourceKeyRowsAsync(IReadOnlyList<ResourceKeyRow> rows)
    {
        await _database.ExecuteNonQueryAsync($"TRUNCATE {ResourceKeyTable} CASCADE");

        if (rows.Count == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.Append(
            $"INSERT INTO {ResourceKeyTable} "
                + $"({ResourceKeyIdColumn}, {ProjectNameColumn}, {ResourceNameColumn}, {ResourceVersionColumn}) VALUES "
        );

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var escaped_project = row.ProjectName.Replace("'", "''");
            var escaped_resource = row.ResourceName.Replace("'", "''");
            var escaped_version = row.ResourceVersion.Replace("'", "''");
            sb.Append(
                $"({row.ResourceKeyId}, '{escaped_project}', '{escaped_resource}', '{escaped_version}')"
            );
            if (i < rows.Count - 1)
            {
                sb.Append(", ");
            }
        }

        await _database.ExecuteNonQueryAsync(sb.ToString());
    }
}
