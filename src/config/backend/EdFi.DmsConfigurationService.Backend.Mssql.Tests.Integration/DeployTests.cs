// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public class DeployTests : DatabaseTestBase
{
    /// <summary>
    /// The only dmscs columns that may remain bigint after DMS-1337 narrowed the 11 spec-named
    /// resource identifiers to int. Asserted as an exact set, so both a regression of an in-scope
    /// column back to BIGINT and an unintended new BIGINT column fail the test.
    /// EducationOrganizationId is an Ed-Fi education organization id, not a CMS resource id, and the
    /// draft Management API v3 spec declares it int64. Tenant.Id has no Admin API counterpart and
    /// ClaimsHierarchy.Id is an internal concurrency token; both are out of scope, as are the
    /// TenantId foreign keys that reference Tenant.Id.
    /// </summary>
    private static readonly (string TableName, string ColumnName)[] ExpectedBigintColumns =
    [
        ("ApplicationEducationOrganization", "EducationOrganizationId"),
        ("AuthorizationStrategy", "TenantId"),
        ("ClaimSet", "TenantId"),
        ("ClaimsHierarchy", "Id"),
        ("DataStore", "TenantId"),
        ("OwnershipToken", "TenantId"),
        ("ResourceClaim", "TenantId"),
        ("Tenant", "Id"),
        ("Vendor", "TenantId"),
    ];

    /// <summary>
    /// The 10 persisted in-scope tables whose identity/primary-key column must report int.
    /// Of the 11 spec-named resources only 10 are persisted: actions has no table, because
    /// ClaimSetRepository.GetActions() returns a hard-coded Action[] - see It_creates_all_dmscs_tables,
    /// which lists no Action table. Action.Id is already int and is covered by the model identifier
    /// contract test instead, since it reaches neither the database nor OpenAPI.
    /// </summary>
    private static readonly string[] InScopeIdentityTables =
    [
        "ApiClient",
        "Application",
        "AuthorizationStrategy",
        "ClaimSet",
        "DataStore",
        "DataStoreContext",
        "DataStoreDerivative",
        "Profile",
        "ResourceClaim",
        "Vendor",
    ];

    private const string ColumnsSql = """
        SELECT table_info.name AS TableName,
               column_info.name AS ColumnName,
               type_info.name AS DataType
        FROM sys.columns column_info
        JOIN sys.tables table_info
            ON table_info.object_id = column_info.object_id
        JOIN sys.schemas schema_info
            ON schema_info.schema_id = table_info.schema_id
        JOIN sys.types type_info
            ON type_info.user_type_id = column_info.user_type_id
        WHERE schema_info.name = 'dmscs'
        ORDER BY table_info.name, column_info.column_id;
        """;

    [Test]
    public async Task It_creates_all_dmscs_tables()
    {
        await using var connection = await OpenConnectionAsync();
        var tables = (
            await connection.QueryAsync<string>(
                "SELECT LOWER(t.name) FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = 'dmscs'"
            )
        ).ToList();

        tables
            .Should()
            .Contain([
                "vendor",
                "vendornamespaceprefix",
                "application",
                "applicationeducationorganization",
                "apiclient",
                "claimset",
                "authorizationstrategy",
                "resourceclaim",
                "claimshierarchy",
                "openiddictapplication",
                "openiddictauthorization",
                "openiddictscope",
                "openiddictapplicationscope",
                "openiddicttoken",
                "openiddictrole",
                "openiddictclientrole",
                "openiddictkey",
                "datastore",
                "apiclientdatastore",
                "datastorecontext",
                "datastorederivative",
                "tenant",
                "profile",
                "applicationprofile",
            ]);
    }

    [Test]
    public async Task It_seeds_authorization_strategies_and_resource_claims()
    {
        await using var connection = await OpenConnectionAsync();
        var strategyCount = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dmscs.AuthorizationStrategy"
        );
        var resourceClaimCount = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dmscs.ResourceClaim"
        );

        strategyCount.Should().Be(13);
        // The seed script's VALUES list has 429 rows (identifiers are sparse, max Id 437),
        // matching the PostgreSQL seed script row-for-row.
        resourceClaimCount.Should().Be(429);
    }

    [Test]
    public void It_is_idempotent_on_redeploy()
    {
        var result = new Deploy.DatabaseDeploy().DeployDatabase(ConnectionString);
        result.Should().BeOfType<Backend.Deploy.DatabaseDeployResult.DatabaseDeploySuccess>();
    }

    [Test]
    public async Task It_declares_only_the_allowlisted_bigint_columns()
    {
        ColumnShape[] columns = await QueryDmscsColumnsAsync();

        (string TableName, string ColumnName)[] actualBigintColumns = columns
            .Where(column => column.DataType == "bigint")
            .Select(column => (column.TableName, column.ColumnName))
            .OrderBy(column => column.TableName, StringComparer.Ordinal)
            .ThenBy(column => column.ColumnName, StringComparer.Ordinal)
            .ToArray();

        actualBigintColumns
            .Should()
            .BeEquivalentTo(
                ExpectedBigintColumns,
                "the 11 spec-named resource identifiers are int32 and only education-organization, "
                    + "tenant and claims-hierarchy columns may remain bigint"
            );
    }

    [Test]
    public async Task It_declares_in_scope_identity_columns_as_int()
    {
        ColumnShape[] columns = await QueryDmscsColumnsAsync();

        foreach (string tableName in InScopeIdentityTables)
        {
            ColumnShape idColumn = columns
                .Should()
                .ContainSingle(column => column.TableName == tableName && column.ColumnName == "Id")
                .Which;

            idColumn
                .DataType.Should()
                .Be("int", $"{tableName}.Id is a spec-named resource identifier declared as int32");
        }
    }

    [Test]
    public async Task It_removes_the_redundant_DataStoreDerivative_lookup_index()
    {
        await using var connection = await OpenConnectionAsync();

        int redundantIndexes = await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM sys.indexes
            WHERE name = 'IX_DataStoreDerivative_DataStoreId'
              AND object_id = OBJECT_ID('dmscs.DataStoreDerivative');
            """
        );

        redundantIndexes
            .Should()
            .Be(
                0,
                "the backing index of UX_DataStoreDerivative_DataStoreId_DerivativeType leads with the "
                    + "same column, so it serves the parent lookup and the child-side foreign-key maintenance"
            );
    }

    [Test]
    public async Task It_creates_the_DataStoreDerivative_unique_constraint_over_the_intended_key_columns()
    {
        await using var connection = await OpenConnectionAsync();

        string[] keyColumns = (
            await connection.QueryAsync<string>(DataStoreDerivativeUniqueConstraintColumnsSql)
        ).ToArray();

        keyColumns.Should().Equal("DataStoreId", "DerivativeType");

        bool backingIndexIsUnique = await connection.ExecuteScalarAsync<bool>(
            """
            SELECT index_info.is_unique
            FROM sys.key_constraints constraint_info
            JOIN sys.indexes index_info
                ON index_info.object_id = constraint_info.parent_object_id
               AND index_info.index_id = constraint_info.unique_index_id
            WHERE constraint_info.name = 'UX_DataStoreDerivative_DataStoreId_DerivativeType'
              AND constraint_info.type = 'UQ';
            """
        );

        backingIndexIsUnique.Should().BeTrue();
    }

    [Test]
    public async Task It_creates_the_trusted_DataStoreDerivative_type_check_constraint()
    {
        await using var connection = await OpenConnectionAsync();

        CheckConstraintShape constraint = await connection.QuerySingleAsync<CheckConstraintShape>(
            """
            SELECT is_disabled AS IsDisabled, is_not_trusted AS IsNotTrusted
            FROM sys.check_constraints
            WHERE name = 'CK_DataStoreDerivative_DerivativeType'
              AND parent_object_id = OBJECT_ID('dmscs.DataStoreDerivative');
            """
        );

        constraint.IsDisabled.Should().BeFalse();
        constraint
            .IsNotTrusted.Should()
            .BeFalse("the constraint is added WITH CHECK, so it has validated the existing rows");
    }

    private static async Task<ColumnShape[]> QueryDmscsColumnsAsync()
    {
        await using var connection = await OpenConnectionAsync();
        return (await connection.QueryAsync<ColumnShape>(ColumnsSql)).ToArray();
    }

    private const string DataStoreDerivativeUniqueConstraintColumnsSql = """
        SELECT column_info.name
        FROM sys.key_constraints constraint_info
        JOIN sys.indexes index_info
            ON index_info.object_id = constraint_info.parent_object_id
           AND index_info.index_id = constraint_info.unique_index_id
        JOIN sys.index_columns index_column_info
            ON index_column_info.object_id = index_info.object_id
           AND index_column_info.index_id = index_info.index_id
        JOIN sys.columns column_info
            ON column_info.object_id = index_column_info.object_id
           AND column_info.column_id = index_column_info.column_id
        WHERE constraint_info.name = 'UX_DataStoreDerivative_DataStoreId_DerivativeType'
          AND index_column_info.is_included_column = 0
        ORDER BY index_column_info.key_ordinal;
        """;

    private sealed record ColumnShape(string TableName, string ColumnName, string DataType);

    private sealed record CheckConstraintShape(bool IsDisabled, bool IsNotTrusted);
}
