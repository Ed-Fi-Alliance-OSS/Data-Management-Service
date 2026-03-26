// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
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
/// <c>docs/new-startup-flow.md</c> §6, exercising the same <c>ResourceKeyValidator</c>
/// invoked by <c>ValidateResourceKeySeedMiddleware</c>. Results are cached by
/// <c>ResourceKeyValidationCacheProvider</c> and <c>DatabaseFingerprintProvider</c>
/// in production.
/// </summary>
public abstract class PostgresqlCompatibilityGateTestsBase : CompatibilityGateTestsBase
{
    private PostgresqlGeneratedDdlTestDatabase _database = null!;

    protected override string ResourceKeyTable => "dms.\"ResourceKey\"";
    protected override string ResourceKeyIdColumn => "\"ResourceKeyId\"";
    protected override string ResourceNameColumn => "\"ResourceName\"";
    protected override string ProjectNameColumn => "\"ProjectName\"";
    protected override string ResourceVersionColumn => "\"ResourceVersion\"";

    protected override SqlDialect GetSqlDialect() => SqlDialect.Pgsql;

    protected override IResourceKeyRowReader CreateResourceKeyRowReader() =>
        new PostgresqlResourceKeyRowReader(
            NullLogger<PostgresqlResourceKeyRowReader>.Instance
        );

    protected override IDatabaseFingerprintReader CreateDatabaseFingerprintReader() =>
        new PostgresqlDatabaseFingerprintReader(
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
        await _database.DisposeAsync();
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
