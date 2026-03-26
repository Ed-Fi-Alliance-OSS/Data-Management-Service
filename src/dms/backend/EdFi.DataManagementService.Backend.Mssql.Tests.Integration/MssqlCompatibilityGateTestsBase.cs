// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// MSSQL-specific base class for compatibility gate integration tests.
/// Extends <see cref="CompatibilityGateTestsBase"/> with MSSQL-specific SQL quoting,
/// database provisioning via <see cref="MssqlGeneratedDdlTestDatabase"/>, and
/// row restore logic.
///
/// These tests mirror the production startup validation flow described in
/// <c>docs/new-startup-flow.md</c> §6, exercising the same <c>ResourceKeyValidator</c>
/// invoked by <c>ValidateResourceKeySeedMiddleware</c>. Results are cached by
/// <c>ResourceKeyValidationCacheProvider</c> and <c>DatabaseFingerprintProvider</c>
/// in production.
/// </summary>
public abstract class MssqlCompatibilityGateTestsBase : CompatibilityGateTestsBase
{
    private MssqlGeneratedDdlTestDatabase _database = null!;

    // -------------------------------------------------------------------------
    // MSSQL-quoted SQL identifiers
    // -------------------------------------------------------------------------

    protected override string ResourceKeyTable => "[dms].[ResourceKey]";

    protected override string ResourceKeyIdColumn => "[ResourceKeyId]";

    protected override string ResourceNameColumn => "[ResourceName]";

    protected override string ProjectNameColumn => "[ProjectName]";

    protected override string ResourceVersionColumn => "[ResourceVersion]";

    // -------------------------------------------------------------------------
    // Dialect
    // -------------------------------------------------------------------------

    protected override SqlDialect GetSqlDialect() => SqlDialect.Mssql;

    // -------------------------------------------------------------------------
    // Reader factories
    // -------------------------------------------------------------------------

    protected override IResourceKeyRowReader CreateResourceKeyRowReader() =>
        new MssqlResourceKeyRowReader(NullLogger<MssqlResourceKeyRowReader>.Instance);

    protected override IDatabaseFingerprintReader CreateDatabaseFingerprintReader() =>
        new MssqlDatabaseFingerprintReader(NullLogger<MssqlDatabaseFingerprintReader>.Instance);

    // -------------------------------------------------------------------------
    // Database lifecycle
    // -------------------------------------------------------------------------

    protected override void GuardAgainstMissingInfrastructure()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }
    }

    protected override async Task ProvisionDatabaseAsync(string ddl)
    {
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(ddl);
    }

    protected override async Task DisposeDatabaseAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    // -------------------------------------------------------------------------
    // Tamper / query helpers
    // -------------------------------------------------------------------------

    protected override async Task ExecuteTamperAsync(string sql)
    {
        await _database.ExecuteNonQueryAsync(sql);
    }

    protected override string GetConnectionString() => _database.ConnectionString;

    // -------------------------------------------------------------------------
    // Row restore
    // -------------------------------------------------------------------------

    protected override async Task RestoreResourceKeyRowsAsync(IReadOnlyList<ResourceKeyRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"DELETE FROM {ResourceKeyTable};");

        if (rows.Count > 0)
        {
            foreach (var row in rows.OrderBy(r => r.ResourceKeyId))
            {
                sb.AppendLine(
                    $"INSERT INTO {ResourceKeyTable} "
                        + $"({ResourceKeyIdColumn}, {ProjectNameColumn}, {ResourceNameColumn}, {ResourceVersionColumn}) "
                        + $"VALUES ({row.ResourceKeyId}, '{row.ProjectName.Replace("'", "''")}', '{row.ResourceName.Replace("'", "''")}', '{row.ResourceVersion.Replace("'", "''")}');"
                );
            }
        }

        await _database.ExecuteNonQueryAsync(sb.ToString());
    }
}
