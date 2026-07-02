// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Deploy;
using FluentAssertions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

[TestFixture]
public class Given_CMS_PostgreSQL_database_shape
{
    private static readonly string[] ExpectedTableNames =
    [
        "ApiClient",
        "ApiClientDataStore",
        "Application",
        "ApplicationEducationOrganization",
        "ApplicationProfile",
        "AuthorizationStrategy",
        "ClaimSet",
        "ClaimsHierarchy",
        "DataStore",
        "DataStoreContext",
        "DataStoreDerivative",
        "OpenIddictApplication",
        "OpenIddictApplicationScope",
        "OpenIddictAuthorization",
        "OpenIddictClientRole",
        "OpenIddictKey",
        "OpenIddictRole",
        "OpenIddictScope",
        "OpenIddictToken",
        "Profile",
        "ResourceClaim",
        "Tenant",
        "Vendor",
        "VendorNamespacePrefix",
    ];

    private static readonly Dictionary<string, string[]> ExpectedRepresentativeColumns = new()
    {
        ["Vendor"] = ["Id", "TenantId", "Company", "ContactName", "ContactEmailAddress"],
        ["ClaimSet"] = ["Id", "TenantId", "ClaimSetName", "IsSystemReserved"],
        ["AuthorizationStrategy"] = ["Id", "TenantId", "AuthorizationStrategyName", "DisplayName"],
        ["ResourceClaim"] = ["Id", "TenantId", "ResourceName", "ClaimName"],
        ["DataStore"] = ["Id", "TenantId", "DataStoreType", "Name", "ConnectionString"],
        ["OpenIddictApplication"] = ["Id", "ClientId", "ClientSecret", "ProtocolMappers"],
        ["OpenIddictToken"] = ["Id", "ApplicationId", "ReferenceId", "ExpirationDate", "Payload"],
        ["Tenant"] = ["Id", "Name"],
        ["Profile"] = ["Id", "ProfileName"],
    };

    private static readonly ConstraintExpectation[] ExpectedRepresentativeConstraints =
    [
        new("PK_Vendor", "Vendor", "p", ["Id"], false),
        new("FK_Application_Vendor", "Application", "f", ["VendorId"], false),
        new(
            "UX_Application_VendorId_ApplicationName",
            "Application",
            "u",
            ["VendorId", "ApplicationName"],
            false
        ),
        new("UX_Vendor_TenantId_Company", "Vendor", "u", ["TenantId", "Company"], true),
        new("FK_Vendor_Tenant", "Vendor", "f", ["TenantId"], false),
        new("UX_ClaimSet_TenantId_ClaimSetName", "ClaimSet", "u", ["TenantId", "ClaimSetName"], true),
        new(
            "UX_AuthorizationStrategy_TenantId_AuthorizationStrategyName",
            "AuthorizationStrategy",
            "u",
            ["TenantId", "AuthorizationStrategyName"],
            true
        ),
        new("UX_ResourceClaim_TenantId_ClaimName", "ResourceClaim", "u", ["TenantId", "ClaimName"], true),
        new("UX_Tenant_Name", "Tenant", "u", ["Name"], false),
        new("UX_Profile_ProfileName", "Profile", "u", ["ProfileName"], false),
        new(
            "PK_OpenIddictApplicationScope",
            "OpenIddictApplicationScope",
            "p",
            ["ApplicationId", "ScopeId"],
            false
        ),
        new(
            "FK_OpenIddictApplicationScope_OpenIddictScope",
            "OpenIddictApplicationScope",
            "f",
            ["ScopeId"],
            false
        ),
        new("UX_OpenIddictApplication_ClientId", "OpenIddictApplication", "u", ["ClientId"], false),
    ];

    private static readonly string[] ExpectedNonUniqueLookupIndexes =
    [
        "IX_Vendor_TenantId",
        "IX_ClaimSet_TenantId",
        "IX_AuthorizationStrategy_TenantId",
        "IX_ResourceClaim_TenantId",
        "IX_DataStore_TenantId",
        "IX_DataStoreDerivative_DataStoreId",
        "IX_OpenIddictToken_ApplicationId",
        "IX_OpenIddictToken_Subject",
        "IX_OpenIddictToken_ReferenceId",
        "IX_OpenIddictToken_ExpirationDate",
    ];

    private static readonly string[] RemovedRedundantIndexNames =
    [
        "idx_Company",
        "idx_ClaimSetName",
        "idx_vendor_applicationname",
        "idx_datastore_context_unique",
        "ix_profile_name",
    ];

    private string _databaseName = string.Empty;
    private string _databaseConnectionString = string.Empty;
    private NpgsqlDataSource? _dataSource;
    private int _dbUpDeployCount;
    private string[] _replayedDeployScriptNames = [];
    private string[] _tableNames = [];
    private ColumnShape[] _columns = [];
    private ConstraintShape[] _constraints = [];
    private IndexShape[] _indexes = [];

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        _databaseConnectionString = CreateIsolatedDatabaseConnectionString();

        DeploySuccessfully(_databaseConnectionString);
        _dbUpDeployCount++;

        _replayedDeployScriptNames = await ReplayEmbeddedDeployScriptsAsync(_databaseConnectionString);

        DeploySuccessfully(_databaseConnectionString);
        _dbUpDeployCount++;

        _dataSource = NpgsqlDataSource.Create(_databaseConnectionString);

        await using var connection = await _dataSource.OpenConnectionAsync();
        _tableNames = (await connection.QueryAsync<string>(TableNamesSql)).ToArray();
        _columns = (await connection.QueryAsync<ColumnShape>(ColumnsSql)).ToArray();
        _constraints = (await connection.QueryAsync<ConstraintShape>(ConstraintsSql)).ToArray();
        _indexes = (await connection.QueryAsync<IndexShape>(IndexesSql)).ToArray();
    }

    [OneTimeTearDown]
    public async Task OneTimeTeardown()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (string.IsNullOrEmpty(_databaseName))
        {
            return;
        }

        await using var connection = new NpgsqlConnection(CreateMaintenanceConnectionString());
        await connection.OpenAsync();
        await connection.ExecuteAsync($"""DROP DATABASE IF EXISTS "{_databaseName}" WITH (FORCE);""");
    }

    [Test]
    public void It_should_run_the_install_path_and_replay_embedded_scripts_against_the_fresh_database()
    {
        _dbUpDeployCount.Should().Be(2);
        _replayedDeployScriptNames.Should().BeEquivalentTo(DeployScriptResourceNames());
    }

    [Test]
    public void It_should_create_only_quoted_PascalCase_dmscs_tables()
    {
        _tableNames.Should().BeEquivalentTo(ExpectedTableNames);

        foreach (string expectedTableName in ExpectedTableNames)
        {
            _tableNames.Should().Contain(expectedTableName);
            _tableNames.Should().NotContain(expectedTableName.ToLowerInvariant());
        }
    }

    [Test]
    public void It_should_create_representative_columns_with_quoted_PascalCase_names()
    {
        foreach ((string tableName, string[] expectedColumnNames) in ExpectedRepresentativeColumns)
        {
            string[] actualColumnNames = _columns
                .Where(column => column.TableName == tableName)
                .Select(column => column.ColumnName)
                .ToArray();

            actualColumnNames.Should().Contain(expectedColumnNames);
            actualColumnNames
                .Should()
                .NotContain(expectedColumnNames.Select(column => column.ToLowerInvariant()));
        }
    }

    [Test]
    public void It_should_use_DMS_style_constraint_names_for_representative_core_tenant_and_OpenIddict_tables()
    {
        _constraints.Should().OnlyContain(constraint => HasDmsStyleConstraintName(constraint));

        foreach (ConstraintExpectation expected in ExpectedRepresentativeConstraints)
        {
            ConstraintShape actual = _constraints
                .Should()
                .ContainSingle(constraint => constraint.Name == expected.Name)
                .Which;

            actual.TableName.Should().Be(expected.TableName);
            actual.ConstraintType.Should().Be(expected.ConstraintType);
            ColumnsFor(actual).Should().Equal(expected.Columns);
            actual.NullsNotDistinct.Should().Be(expected.NullsNotDistinct);
        }
    }

    [Test]
    public void It_should_model_tenant_scoped_and_global_logical_uniqueness_as_UX_constraints()
    {
        string[] tenantScopedUniqueNames =
        [
            "UX_Vendor_TenantId_Company",
            "UX_ClaimSet_TenantId_ClaimSetName",
            "UX_AuthorizationStrategy_TenantId_AuthorizationStrategyName",
            "UX_ResourceClaim_TenantId_ClaimName",
        ];

        foreach (string constraintName in tenantScopedUniqueNames)
        {
            ConstraintShape actual = _constraints
                .Should()
                .ContainSingle(constraint => constraint.Name == constraintName)
                .Which;

            actual.ConstraintType.Should().Be("u");
            ColumnsFor(actual).Should().StartWith("TenantId");
            actual.NullsNotDistinct.Should().BeTrue();
        }

        string[] globalUniqueNames =
        [
            "UX_Tenant_Name",
            "UX_Profile_ProfileName",
            "UX_OpenIddictApplication_ClientId",
            "UX_OpenIddictScope_Name",
            "UX_OpenIddictRole_Name",
        ];

        foreach (string constraintName in globalUniqueNames)
        {
            ConstraintShape actual = _constraints
                .Should()
                .ContainSingle(constraint => constraint.Name == constraintName)
                .Which;

            actual.ConstraintType.Should().Be("u");
            ColumnsFor(actual).Should().NotContain("TenantId");
            actual.NullsNotDistinct.Should().BeFalse();
        }
    }

    [Test]
    public void It_should_keep_lookup_indexes_non_unique_and_remove_redundant_unique_indexes()
    {
        string[] indexNames = _indexes.Select(index => index.Name).ToArray();

        indexNames.Should().NotContain(RemovedRedundantIndexNames);

        _indexes
            .Where(index => index.IsUnique && !index.IsConstraintBacked)
            .Should()
            .BeEmpty("logical uniqueness should be represented by UX_* constraints");

        foreach (string expectedIndexName in ExpectedNonUniqueLookupIndexes)
        {
            IndexShape actual = _indexes
                .Should()
                .ContainSingle(index => index.Name == expectedIndexName)
                .Which;

            actual.IsUnique.Should().BeFalse();
            actual.IsConstraintBacked.Should().BeFalse();
        }
    }

    private string CreateIsolatedDatabaseConnectionString()
    {
        NpgsqlConnectionStringBuilder builder = new(Configuration.DatabaseOptions.Value.DatabaseConnection)
        {
            Database = $"dms1244_shape_{Guid.NewGuid():N}",
            Pooling = false,
        };

        _databaseName = builder.Database!;
        return builder.ConnectionString;
    }

    private static string CreateMaintenanceConnectionString()
    {
        NpgsqlConnectionStringBuilder builder = new(Configuration.DatabaseOptions.Value.DatabaseConnection)
        {
            Database = "postgres",
            Pooling = false,
        };

        return builder.ConnectionString;
    }

    private static void DeploySuccessfully(string connectionString)
    {
        DatabaseDeployResult result = new Deploy.DatabaseDeploy().DeployDatabase(connectionString);

        if (result is DatabaseDeployResult.DatabaseDeployFailure failure)
        {
            Assert.Fail($"Database deploy failed: {failure.Error}");
        }

        result.Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();
    }

    private static async Task<string[]> ReplayEmbeddedDeployScriptsAsync(string connectionString)
    {
        string[] scriptNames = DeployScriptResourceNames();

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();

        foreach (string scriptName in scriptNames)
        {
            string script = await ReadDeployScriptAsync(scriptName);
            await connection.ExecuteAsync(script);
        }

        return scriptNames;
    }

    private static string[] DeployScriptResourceNames()
    {
        return typeof(Deploy.DatabaseDeploy)
            .Assembly.GetManifestResourceNames()
            .Where(name =>
                name.Contains(".Deploy.Scripts.", StringComparison.Ordinal) && name.EndsWith(".sql")
            )
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<string> ReadDeployScriptAsync(string scriptName)
    {
        Stream stream =
            typeof(Deploy.DatabaseDeploy).Assembly.GetManifestResourceStream(scriptName)
            ?? throw new InvalidOperationException($"Embedded deploy script not found: {scriptName}");

        using (stream)
        using (StreamReader reader = new(stream))
        {
            return await reader.ReadToEndAsync();
        }
    }

    private static bool HasDmsStyleConstraintName(ConstraintShape constraint)
    {
        return constraint.ConstraintType switch
        {
            "p" => constraint.Name.StartsWith("PK_", StringComparison.Ordinal),
            "f" => constraint.Name.StartsWith("FK_", StringComparison.Ordinal),
            "u" => constraint.Name.StartsWith("UX_", StringComparison.Ordinal),
            "c" => constraint.Name.StartsWith("CK_", StringComparison.Ordinal),
            _ => false,
        };
    }

    private static string[] ColumnsFor(ConstraintShape constraint)
    {
        return constraint
            .ColumnsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private const string TableNamesSql = """
        SELECT table_name
        FROM information_schema.tables
        WHERE table_schema = 'dmscs'
          AND table_type = 'BASE TABLE'
        ORDER BY table_name;
        """;

    private const string ColumnsSql = """
        SELECT table_name AS TableName,
               column_name AS ColumnName
        FROM information_schema.columns
        WHERE table_schema = 'dmscs'
        ORDER BY table_name, ordinal_position;
        """;

    private const string ConstraintsSql = """
        SELECT constraint_info.conname AS Name,
               table_info.relname AS TableName,
               constraint_info.contype::text AS ConstraintType,
               COALESCE(string_agg(attribute_info.attname, ',' ORDER BY key_columns.ordinality), '') AS ColumnsCsv,
               COALESCE(index_info.indnullsnotdistinct, false) AS NullsNotDistinct
        FROM pg_constraint constraint_info
        JOIN pg_class table_info
            ON table_info.oid = constraint_info.conrelid
        JOIN pg_namespace schema_info
            ON schema_info.oid = table_info.relnamespace
        LEFT JOIN LATERAL unnest(constraint_info.conkey) WITH ORDINALITY AS key_columns(attnum, ordinality)
            ON true
        LEFT JOIN pg_attribute attribute_info
            ON attribute_info.attrelid = table_info.oid
           AND attribute_info.attnum = key_columns.attnum
        LEFT JOIN pg_index index_info
            ON index_info.indexrelid = constraint_info.conindid
        WHERE schema_info.nspname = 'dmscs'
          AND constraint_info.contype IN ('p', 'f', 'u', 'c')
        GROUP BY constraint_info.oid,
                 constraint_info.conname,
                 table_info.relname,
                 constraint_info.contype,
                 index_info.indnullsnotdistinct
        ORDER BY table_info.relname, constraint_info.conname;
        """;

    private const string IndexesSql = """
        SELECT index_info.relname AS Name,
               index_catalog.indisunique AS IsUnique,
               constraint_info.oid IS NOT NULL AS IsConstraintBacked
        FROM pg_index index_catalog
        JOIN pg_class index_info
            ON index_info.oid = index_catalog.indexrelid
        JOIN pg_class table_info
            ON table_info.oid = index_catalog.indrelid
        JOIN pg_namespace schema_info
            ON schema_info.oid = table_info.relnamespace
        LEFT JOIN pg_constraint constraint_info
            ON constraint_info.conindid = index_catalog.indexrelid
        WHERE schema_info.nspname = 'dmscs'
        ORDER BY table_info.relname, index_info.relname;
        """;

    private sealed record ConstraintExpectation(
        string Name,
        string TableName,
        string ConstraintType,
        string[] Columns,
        bool NullsNotDistinct
    );

    private sealed record ColumnShape(string TableName, string ColumnName);

    private sealed record ConstraintShape(
        string Name,
        string TableName,
        string ConstraintType,
        string ColumnsCsv,
        bool NullsNotDistinct
    );

    private sealed record IndexShape(string Name, bool IsUnique, bool IsConstraintBacked);
}
