// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Tests.E2E.Hooks;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Tests.E2E.Management;

/// <summary>
/// Keeps connection-local copies of the claims tables in the isolated E2E database. Uploads commit
/// through the service's own connection, so a rollback on this connection cannot restore the baseline.
/// </summary>
internal sealed class ClaimsDatabaseSnapshot : IAsyncDisposable
{
    private readonly DbConnection _connection = SetupHooks.CreateDatabaseConnection();
    private bool _captured;
    private static string CatalogBackup =>
        SetupHooks.UseMssql ? "#ClaimsCatalogBaseline" : "claims_catalog_baseline";
    private static string HierarchyBackup =>
        SetupHooks.UseMssql ? "#ClaimsHierarchyBaseline" : "claims_hierarchy_baseline";
    private static string FalseLiteral => SetupHooks.UseMssql ? "0" : "false";

    public List<CatalogEntry> Catalog { get; private set; } = [];
    public string Hierarchy { get; private set; } = string.Empty;

    public bool Captured => _captured;

    public async Task CaptureAsync()
    {
        await _connection.OpenAsync();
        await using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*) FROM "dmscs"."Application"
                """;
            Convert
                .ToInt64(await command.ExecuteScalarAsync())
                .Should()
                .Be(0, "this scenario requires an isolated catalog with no referencing applications");
        }

        Catalog = await ReadCatalogAsync();
        Hierarchy = await ReadHierarchyAsync();
        await using var transaction = await _connection.BeginTransactionAsync();
        await using var snapshot = _connection.CreateCommand();
        snapshot.Transaction = transaction;
        snapshot.CommandText = SetupHooks.UseMssql
            ? $"""
                SELECT * INTO {CatalogBackup} FROM "dmscs"."ClaimSet";
                SELECT * INTO {HierarchyBackup} FROM "dmscs"."ClaimsHierarchy";
                """
            : $"""
                CREATE TEMP TABLE {CatalogBackup} AS SELECT * FROM "dmscs"."ClaimSet";
                CREATE TEMP TABLE {HierarchyBackup} AS SELECT * FROM "dmscs"."ClaimsHierarchy";
                """;
        await snapshot.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        // Set before any mutation, so an assertion or failed DELETE still runs restoration.
        _captured = true;
    }

    public async Task IsolateAsync(string importedNameToInsert)
    {
        await using var isolate = _connection.CreateCommand();
        // The standard seed already reserves every name in the representative export. Temporarily
        // remove one imported row to prove insertion, preserving all other reserved rows. The full
        // original catalog (including this row's reserved flag and ID) is restored after the scenario.
        isolate.CommandText = $"""
            DELETE FROM "dmscs"."ClaimSet"
            WHERE "IsSystemReserved" = {FalseLiteral} OR "ClaimSetName" = @ImportedName
            """;
        var parameter = isolate.CreateParameter();
        parameter.ParameterName = "ImportedName";
        parameter.Value = importedNameToInsert;
        isolate.Parameters.Add(parameter);
        await isolate.ExecuteNonQueryAsync();
    }

    public async Task<List<CatalogEntry>> ReadCatalogAsync()
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "ClaimSetName", "IsSystemReserved" FROM "dmscs"."ClaimSet"
            """;
        await using var reader = await command.ExecuteReaderAsync();
        List<CatalogEntry> catalog = [];
        while (await reader.ReadAsync())
        {
            catalog.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetBoolean(2)));
        }
        return catalog;
    }

    public async Task<string> ReadHierarchyAsync()
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT "Hierarchy" FROM "dmscs"."ClaimsHierarchy"
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("a persisted hierarchy must exist");
        string hierarchy = reader.GetString(0);
        (await reader.ReadAsync()).Should().BeFalse("there must be exactly one persisted hierarchy");
        return hierarchy;
    }

    public async Task RestoreAsync()
    {
        if (!_captured)
        {
            return;
        }

        // Restore after reloading the provider, since reload also writes both database tables.
        // Restore original IDs, reserved flags, tenant/audit fields and timestamps, even after a failed assertion.
        const string catalogColumns = """
            "Id", "ClaimSetName", "IsSystemReserved", "CreatedAt", "CreatedBy",
            "LastModifiedAt", "ModifiedBy", "TenantId"
            """;
        string identityOn = SetupHooks.UseMssql ? "SET IDENTITY_INSERT dmscs.ClaimSet ON;" : string.Empty;
        string identityOff = SetupHooks.UseMssql ? "SET IDENTITY_INSERT dmscs.ClaimSet OFF;" : string.Empty;
        string overrideIdentity = SetupHooks.UseMssql ? string.Empty : "OVERRIDING SYSTEM VALUE";
        string[] hierarchyColumns =
        [
            "Hierarchy",
            "LastModifiedDate",
            "CreatedAt",
            "CreatedBy",
            "LastModifiedAt",
            "ModifiedBy",
        ];
        string hierarchyAssignments = string.Join(
            ", ",
            hierarchyColumns.Select(column => $"\"{column}\" = (SELECT \"{column}\" FROM {HierarchyBackup})")
        );

        await using var transaction = await _connection.BeginTransactionAsync();
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            DELETE FROM "dmscs"."ClaimSet";
            {identityOn}
            INSERT INTO "dmscs"."ClaimSet" ({catalogColumns}) {overrideIdentity}
                SELECT {catalogColumns} FROM {CatalogBackup};
            {identityOff}
            UPDATE "dmscs"."ClaimsHierarchy" SET {hierarchyAssignments}
                WHERE "Id" = (SELECT "Id" FROM {HierarchyBackup});
            """;
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();

        (await ReadCatalogAsync())
            .Should()
            .BeEquivalentTo(Catalog, "cleanup must restore catalog identities and flags");
        JsonNode
            .DeepEquals(JsonNode.Parse(await ReadHierarchyAsync()), JsonNode.Parse(Hierarchy))
            .Should()
            .BeTrue("cleanup must restore the original persisted hierarchy");
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    internal sealed record CatalogEntry(int Id, string Name, bool IsSystemReserved);
}
