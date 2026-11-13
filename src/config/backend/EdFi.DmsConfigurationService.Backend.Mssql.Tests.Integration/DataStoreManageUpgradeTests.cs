// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Deploy;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.Jobs;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.ManagedDataStores;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("DataStoreManageUpgrade")]
[Category("MssqlIntegration")]
public class Given_the_managed_DataStore_migration
{
    private JobUpgradeTestDatabase _database = null!;
    private const string ScriptName =
        "EdFi.DmsConfigurationService.Backend.Mssql.Deploy.Scripts.0041_Create_DataStoreManage_Table.sql";
    private static readonly ManagedDataStoreEndpoint Endpoint = new(
        "postgresql",
        "tcp",
        "db.example.test",
        5432,
        null,
        "postgres"
    );

    [SetUp]
    public void Setup()
    {
        MssqlTestConfiguration.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require ConnectionStrings__MssqlAdmin."
        );
        _database = new();
    }

    [TearDown]
    public Task Teardown() => _database is null ? Task.CompletedTask : _database.DropAsync();

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_creates_or_upgrades_without_changing_ordinary_rows_and_replays_a_missing_journal(
        bool upgrade
    )
    {
        if (upgrade)
        {
            _database.DeployThrough(40);
            await using SqlConnection seed = new(_database.ConnectionString);
            await InsertOrdinaryAsync(seed, "ordinary");
        }
        string[] before = upgrade ? await _database.JournalAsync() : [];
        _database.DeployThrough(41);
        (await _database.TableExistsAsync("DataStoreManage")).Should().BeTrue();
        string[] journal = await _database.JournalAsync();
        journal.Should().Contain(ScriptName);
        if (upgrade)
        {
            journal.Except(before).Should().Equal(ScriptName);
        }
        await using SqlConnection connection = new(_database.ConnectionString);
        (await connection.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM dmscs."DataStoreManage";"""))
            .Should()
            .Be(0);
        (await connection.QueryAsync<string>("""SELECT "Name" FROM dmscs."DataStore";"""))
            .Should()
            .Equal(upgrade ? ["ordinary"] : Array.Empty<string>());
        int id = await InsertAsync(connection, "retained");
        string[] objects = await _database.ConstraintAndIndexNamesAsync("DataStoreManage");
        await connection.ExecuteAsync(
            "DELETE FROM dbo.dmscs_SchemaVersions WHERE ScriptName = @ScriptName;",
            new { ScriptName }
        );
        _database.DeployThrough(41);
        _database.DeployThrough(41);
        (await _database.JournalAsync()).Should().Equal(journal);
        (await _database.ConstraintAndIndexNamesAsync("DataStoreManage")).Should().Equal(objects);
        (await connection.QuerySingleAsync<int>("""SELECT "Id" FROM dmscs."DataStoreManage";"""))
            .Should()
            .Be(id);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_enforces_active_name_uniqueness_including_NULL_tenants_and_releases_only_Deleted(
        bool tenant
    )
    {
        _database.DeployThrough(41);
        await using SqlConnection connection = new(_database.ConnectionString);
        long? tenantId = tenant ? await InsertTenantAsync(connection, "one") : null;
        long otherTenant = await InsertTenantAsync(connection, "two");
        int first = await InsertAsync(connection, "Store", tenantId);
        await InsertAsync(connection, "store", otherTenant);
        int other = await InsertAsync(connection, "other", tenantId);
        foreach (
            ManagedDataStoreStatus status in Enum.GetValues<ManagedDataStoreStatus>()
                .Where(s => s != ManagedDataStoreStatus.Deleted)
        )
        {
            await UpdateAsync(connection, first, "Status", status.ToString());
            Func<Task> duplicate = () => InsertAsync(connection, "\u3000STORE\u00a0", tenantId);
            await RejectAsync(duplicate, "UX_DataStoreManage_TenantId_Name");
            Func<Task> rename = () => UpdateAsync(connection, other, "Name", "store");
            await RejectAsync(rename, "UX_DataStoreManage_TenantId_Name");
        }
        await UpdateAsync(connection, first, "Status", "Deleted");
        await InsertAsync(connection, "store", tenantId);
        Func<Task> reactivate = () => UpdateAsync(connection, first, "Status", "PendingCreate");
        await RejectAsync(reactivate, "UX_DataStoreManage_TenantId_Name");
        foreach (string distinct in new[] { "café", "cafe", "CAFÉ", "cafe\u0301", "ストア", "\u200Bstore" })
        {
            await InsertAsync(connection, distinct, tenantId);
        }
    }

    [TestCaseSource(nameof(ManagedNameVectors))]
    public async Task It_uses_the_same_generated_name_key_as_ordinary_DataStore(string name, string canonical)
    {
        _database.DeployThrough(41);
        await using SqlConnection connection = new(_database.ConnectionString);
        int managed = await InsertAsync(connection, name);
        int ordinary = await InsertOrdinaryAsync(connection, name);
        (
            await connection.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*) FROM dmscs."DataStoreManage" m JOIN dmscs."DataStore" d
                  ON m."NameComparisonKey" = d."NameComparisonKey"
                WHERE m."Id" = @Managed AND d."Id" = @Ordinary;
                """,
                new { Managed = managed, Ordinary = ordinary }
            )
        ).Should().Be(1);
        await UpdateAsync(connection, managed, "Name", "Updated");
        await connection.ExecuteAsync(
            """UPDATE dmscs."DataStore" SET "Name" = 'UPDATED' WHERE "Id" = @Id;""",
            new { Id = ordinary }
        );
        (
            await connection.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*) FROM dmscs."DataStoreManage" m JOIN dmscs."DataStore" d
                  ON m."NameComparisonKey" = d."NameComparisonKey"
                WHERE m."Id" = @Managed AND d."Id" = @Ordinary;
                """,
                new { Managed = managed, Ordinary = ordinary }
            )
        ).Should().Be(1);
    }

    [Test]
    public async Task It_reserves_targets_across_tenants_and_releases_only_Deleted()
    {
        _database.DeployThrough(41);
        await using SqlConnection connection = new(_database.ConnectionString);
        long tenant = await InsertTenantAsync(connection, "other");
        int first = await InsertAsync(
            connection,
            "first",
            values: new() { ["TargetDatabaseName"] = "Shared_Target" }
        );
        foreach (
            ManagedDataStoreStatus status in Enum.GetValues<ManagedDataStoreStatus>()
                .Where(s => s != ManagedDataStoreStatus.Deleted)
        )
        {
            await UpdateAsync(connection, first, "Status", status.ToString());
            Func<Task> duplicate = () =>
                InsertAsync(connection, "second", tenant, new() { ["TargetDatabaseName"] = "shared_target" });
            await RejectAsync(duplicate, "UX_DataStoreManage_ActiveTarget");
        }
        await InsertAsync(
            connection,
            "different endpoint",
            tenant,
            new()
            {
                ["TargetDatabaseName"] = "shared_target",
                ["EndpointDescriptor"] = (Endpoint with { Host = "another.example.test" }).CanonicalEncoding,
            }
        );
        await UpdateAsync(connection, first, "Status", "Deleted");
        await InsertAsync(connection, "second", tenant, new() { ["TargetDatabaseName"] = "shared_target" });
        Func<Task> reactivate = () => UpdateAsync(connection, first, "Status", "CreateError");
        await RejectAsync(reactivate, "UX_DataStoreManage_ActiveTarget");
    }

    [TestCase("Status", "unknown", "CK_DataStoreManage_Status")]
    [TestCase("Status", "created", "CK_DataStoreManage_Status")]
    [TestCase("Status", "Created ", "CK_DataStoreManage_Status")]
    [TestCase("Template", "Populated", "CK_DataStoreManage_Template")]
    [TestCase("Template", "minimal", "CK_DataStoreManage_Template")]
    [TestCase("Template", "Minimal ", "CK_DataStoreManage_Template")]
    [TestCase("Name", "   ", "CK_DataStoreManage_Name")]
    [TestCase("TargetDatabaseName", "bad-name", "CK_DataStoreManage_TargetDatabaseName")]
    [TestCase("LastAttempt", -1, "CK_DataStoreManage_LastAttempt")]
    [TestCase("EndpointDescriptor", "[]", "CK_DataStoreManage_EndpointDescriptor")]
    [TestCase(
        "EndpointDescriptor",
        "[2,\"postgresql\",\"tcp\",\"db\",5432,null,\"postgres\"]",
        "CK_DataStoreManage_EndpointDescriptor"
    )]
    [TestCase(
        "EndpointDescriptor",
        "[1,\"unknown\",\"tcp\",\"db\",5432,null,\"postgres\"]",
        "CK_DataStoreManage_EndpointDescriptor"
    )]
    [TestCase("LastFailureCategory", "secret exception text", "CK_DataStoreManage_LastFailure")]
    [TestCase("LastFailureJobCode", "unregistered", "CK_DataStoreManage_LastFailure")]
    public async Task It_rejects_invalid_direct_writes(string column, object value, string constraint)
    {
        _database.DeployThrough(41);
        await using SqlConnection connection = new(_database.ConnectionString);
        Func<Task> invalid = () => InsertAsync(connection, "invalid", values: new() { [column] = value });
        await RejectAsync(invalid, constraint);
    }

    [Test]
    public async Task It_rejects_empty_source_identity_partial_pins_and_missing_foreign_keys()
    {
        _database.DeployThrough(41);
        await using SqlConnection connection = new(_database.ConnectionString);
        await RejectAsync(
            () => InsertAsync(connection, "empty", values: new() { ["ExpectedSourceIdentity"] = Guid.Empty }),
            "CK_DataStoreManage_ExpectedSourceIdentity"
        );
        await RejectAsync(
            () => InsertAsync(connection, "partial", values: new() { ["PinnedPackageId"] = "one" }),
            "CK_DataStoreManage_ArtifactPin"
        );
        await RejectAsync(
            () => InsertAsync(connection, "missing tenant", long.MaxValue),
            "FK_DataStoreManage_Tenant"
        );
        await RejectAsync(
            () => InsertAsync(connection, "missing store", values: new() { ["DataStoreId"] = int.MaxValue }),
            "FK_DataStoreManage_DataStore"
        );
    }

    [Test]
    public async Task It_keeps_one_current_link_and_restricts_tenant_and_ordinary_deletion()
    {
        _database.DeployThrough(41);
        await using SqlConnection connection = new(_database.ConnectionString);
        long tenant = await InsertTenantAsync(connection, "tenant");
        int ordinary = await InsertOrdinaryAsync(connection, "published");
        int managed = await InsertAsync(connection, "managed", tenant, new() { ["DataStoreId"] = ordinary });
        await RejectAsync(
            () => InsertAsync(connection, "duplicate link", values: new() { ["DataStoreId"] = ordinary }),
            "UX_DataStoreManage_DataStoreId"
        );
        await RejectAsync(
            () =>
                connection.ExecuteAsync(
                    """DELETE FROM dmscs."Tenant" WHERE "Id" = @Id;""",
                    new { Id = tenant }
                ),
            "FK_DataStoreManage_Tenant"
        );
        await RejectAsync(
            () =>
                connection.ExecuteAsync(
                    """DELETE FROM dmscs."DataStore" WHERE "Id" = @Id;""",
                    new { Id = ordinary }
                ),
            "FK_DataStoreManage_DataStore"
        );
        await UpdateAsync(connection, managed, "HistoricalDataStoreId", ordinary);
        await UpdateAsync(connection, managed, "HistoricalDataStoreName", "published");
        await UpdateAsync(connection, managed, "DataStoreId", null);
        await connection.ExecuteAsync(
            """DELETE FROM dmscs."DataStore" WHERE "Id" = @Id;""",
            new { Id = ordinary }
        );
        (
            await connection.QuerySingleAsync<int>(
                """SELECT "HistoricalDataStoreId" FROM dmscs."DataStoreManage" WHERE "Id" = @Id;""",
                new { Id = managed }
            )
        )
            .Should()
            .Be(ordinary);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task It_round_trips_every_persisted_field_and_keeps_job_ids_after_retention(
        bool namedInstance,
        bool deleted
    )
    {
        _database.DeployThrough(41);
        await using SqlConnection connection = new(_database.ConnectionString);
        long tenant = await InsertTenantAsync(connection, "tenant");
        int ordinary = await InsertOrdinaryAsync(connection, "published");
        DateTime timestamp = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
        ManagedDataStoreEndpoint endpoint = namedInstance
            ? new("mssql", "tcp", "db.example.test", null, "instance", "master")
            : Endpoint with
            {
                Transport = "unix",
                Host = "/var/é/" + new string('x', 5000),
            };
        Dictionary<string, object?> fields = new()
        {
            ["Template"] = "Sample",
            ["Status"] = deleted ? "Deleted" : "DeleteError",
            ["TargetDatabaseName"] = "Target_One",
            ["ExpectedSourceIdentity"] = Guid.NewGuid(),
            ["EndpointDescriptor"] = endpoint.CanonicalEncoding,
            ["DataStoreId"] = deleted ? null : ordinary,
            ["HistoricalDataStoreId"] = ordinary,
            ["HistoricalDataStoreName"] = "all fields",
            ["CreateJobId"] = "create-job",
            ["DeleteJobId"] = "delete-job",
            ["LastAttempt"] = 3,
            ["LastFailureCategory"] = deleted ? null : "OperatorInspectionRequired",
            ["LastFailureProvisioningCode"] = deleted ? null : "TargetUnowned",
            ["LastFailureJobCode"] = null,
            ["CreatedAt"] = timestamp,
            ["UpdatedAt"] = timestamp.AddHours(1),
            ["DeletedAt"] = deleted ? timestamp.AddHours(2) : null,
            ["LastRefreshed"] = timestamp.AddMinutes(2),
            ["LastModifiedDate"] = timestamp.AddMinutes(3),
            ["PinnedContractVersion"] = 1,
            ["PinnedProvider"] = endpoint.Provider,
            ["PinnedPackageId"] = "package.id",
            ["PinnedPackageVersion"] = "1.2.3",
            ["PinnedPackageSha256"] = new string('a', 64),
            ["PinnedArtifactSha256"] = new string('b', 64),
            ["PinnedTemplateKind"] = "Populated",
            ["PinnedDataStandardVersion"] = "5.2",
            ["PinnedEffectiveSchemaHash"] = new string('c', 64),
            ["PinnedInventorySha256"] = new string('d', 64),
            ["PinnedApiSchemaFormatVersion"] = "1.0",
            ["PinnedRelationalMappingVersion"] = "v3",
        };
        int id = await InsertAsync(connection, "all fields", tenant, fields);
        var row =
            (IDictionary<string, object>)
                await connection.QuerySingleAsync(
                    """SELECT * FROM dmscs."DataStoreManage" WHERE "Id" = @Id;""",
                    new { Id = id }
                );
        row["Name"].Should().Be("all fields");
        row["TenantId"].Should().Be(tenant);
        foreach ((string key, object? value) in fields)
        {
            row[key].Should().Be(value, $"{key} must survive persistence without loss");
        }
        ManagedDataStorePersistedState state = ReadState(row);
        state.Endpoint.Should().Be(endpoint);
        ManagedDataStore.Rehydrate(state).Should().BeEquivalentTo(state);
        foreach (string pinColumn in fields.Keys.Where(k => k.StartsWith("Pinned", StringComparison.Ordinal)))
        {
            await RejectAsync(
                () => UpdateAsync(connection, id, pinColumn, null),
                "CK_DataStoreManage_ArtifactPin"
            );
        }
        foreach (string jobId in new[] { "create-job", "delete-job" })
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO dmscs."Job" ("JobId", "JobType", "PayloadVersion", "Payload", "Status", "FinishedAt")
                VALUES (@JobId, 'test', 1, '{}', 'Completed', @Timestamp);
                """,
                new { JobId = jobId, Timestamp = timestamp }
            );
        }
        await connection.ExecuteAsync(
            """DELETE FROM dmscs."Job" WHERE "JobId" IN ('create-job', 'delete-job');"""
        );
        (
            await connection.QuerySingleAsync<(string CreateJobId, string DeleteJobId)>(
                """SELECT "CreateJobId", "DeleteJobId" FROM dmscs."DataStoreManage" WHERE "Id" = @Id;""",
                new { Id = id }
            )
        )
            .Should()
            .Be(("create-job", "delete-job"));
    }

    [Test]
    public async Task It_inventories_all_unique_name_indexes_including_custom_residuals_without_dropping_them()
    {
        _database.DeployThrough(40);
        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.ExecuteAsync(
            """CREATE UNIQUE INDEX "Custom_Ordinary_DisplayName" ON dmscs."DataStore" ("Name");"""
        );
        _database.DeployThrough(41);
        await connection.ExecuteAsync(
            """CREATE UNIQUE INDEX "Custom_Managed_DisplayName" ON dmscs."DataStoreManage" ("Name");"""
        );
        var indexes = (
            await connection.QueryAsync<(string TableName, string IndexName, string Definition)>(INVENTORY)
        ).ToArray();
        indexes
            .Should()
            .Contain(i =>
                i.TableName == "DataStore"
                && i.IndexName == "Custom_Ordinary_DisplayName"
                && i.Definition.Contains("Name")
            );
        indexes
            .Should()
            .Contain(i =>
                i.TableName == "DataStoreManage"
                && i.IndexName == "Custom_Managed_DisplayName"
                && i.Definition.Contains("Name")
            );
        indexes
            .Should()
            .Contain(i =>
                i.IndexName == "UX_DataStore_TenantId_Name" && i.Definition.Contains("NameComparisonKey")
            );
        indexes
            .Should()
            .Contain(i =>
                i.IndexName == "UX_DataStoreManage_TenantId_Name"
                && i.Definition.Contains("NameComparisonKey")
            );
        _database.DeployThrough(41);
        (await connection.QueryAsync<(string TableName, string IndexName, string Definition)>(INVENTORY))
            .Should()
            .BeEquivalentTo(indexes);
    }

    [Test]
    public async Task It_preserves_exact_name_and_job_identifier_bounds()
    {
        _database.DeployThrough(41);
        await using SqlConnection connection = new(_database.ConnectionString);
        int id = await InsertAsync(
            connection,
            new string('A', 46),
            values: new()
            {
                ["CreateJobId"] = new string('Ж', 150),
                ["DeleteJobId"] = new string('猫', 150),
                ["TargetDatabaseName"] = new string('T', 63),
            }
        );
        (
            await connection.QuerySingleAsync<(string CreateJobId, string DeleteJobId)>(
                """SELECT "CreateJobId", "DeleteJobId" FROM dmscs."DataStoreManage" WHERE "Id" = @Id;""",
                new { Id = id }
            )
        )
            .Should()
            .Be((new string('Ж', 150), new string('猫', 150)));
        foreach (
            (string column, object value) in new (string, object)[]
            {
                ("Name", new string('A', 47)),
                ("CreateJobId", new string('c', 151)),
                ("DeleteJobId", new string('d', 151)),
                ("TargetDatabaseName", new string('T', 64)),
            }
        )
        {
            Func<Task> invalid = () => UpdateAsync(connection, id, column, value);
            await invalid.Should().ThrowAsync<DbException>();
        }
        (
            await connection.QuerySingleAsync<string>(
                """SELECT "Name" FROM dmscs."DataStoreManage" WHERE "Id" = @Id;""",
                new { Id = id }
            )
        )
            .Should()
            .HaveLength(46);
    }

    [Test]
    public async Task It_reserves_using_every_descriptor_field_and_recomputes_reservations_on_update()
    {
        _database.DeployThrough(41);
        await using SqlConnection connection = new(_database.ConnectionString);
        ManagedDataStoreEndpoint[] endpoints =
        [
            Endpoint,
            Endpoint with
            {
                Provider = "mssql",
            },
            Endpoint with
            {
                Transport = "unix",
            },
            Endpoint with
            {
                Host = "another",
            },
            Endpoint with
            {
                Port = 5544,
            },
            Endpoint with
            {
                Instance = "named",
            },
            Endpoint with
            {
                MaintenanceDatabase = "other",
            },
        ];
        List<int> ids = [];
        foreach (ManagedDataStoreEndpoint endpoint in endpoints)
        {
            ids.Add(
                await InsertAsync(
                    connection,
                    "store " + ids.Count,
                    values: new()
                    {
                        ["TargetDatabaseName"] = "shared",
                        ["EndpointDescriptor"] = endpoint.CanonicalEncoding,
                    }
                )
            );
        }
        (
            await connection.ExecuteScalarAsync<int>(
                """SELECT COUNT(DISTINCT "EndpointDigest") FROM dmscs."DataStoreManage";"""
            )
        )
            .Should()
            .Be(endpoints.Length);
        await RejectAsync(
            () => UpdateAsync(connection, ids[1], "EndpointDescriptor", Endpoint.CanonicalEncoding),
            "UX_DataStoreManage_ActiveTarget"
        );
        int anotherTarget = await InsertAsync(connection, "other target");
        await RejectAsync(
            () => UpdateAsync(connection, anotherTarget, "TargetDatabaseName", "SHARED"),
            "UX_DataStoreManage_ActiveTarget"
        );
    }

    [Test]
    public async Task It_rolls_back_all_schema_changes_after_an_index_failure_and_can_retry()
    {
        _database.DeployThrough(40);
        string[] journal = await _database.JournalAsync();
        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.ExecuteAsync(
            """
            CREATE TRIGGER fail_managed_index ON DATABASE FOR CREATE_INDEX AS
            BEGIN
                IF OBJECT_ID(N'dmscs.DataStoreManage', N'U') IS NOT NULL
                    THROW 50001, 'injected managed index failure', 1;
            END;
            """
        );
        var failure = _database
            .Deploy(41)
            .Should()
            .BeOfType<DatabaseDeployResult.DatabaseDeployFailure>()
            .Subject;
        failure.Error.Message.Should().Contain("injected managed index failure");
        (await _database.TableExistsAsync("DataStoreManage")).Should().BeFalse();
        (await _database.JournalAsync()).Should().Equal(journal);
        await connection.ExecuteAsync("DROP TRIGGER fail_managed_index ON DATABASE;");
        _database.DeployThrough(41);
        (await _database.TableExistsAsync("DataStoreManage")).Should().BeTrue();
        (await _database.JournalAsync()).Except(journal).Should().Equal(ScriptName);
    }

    // Non-ASCII vectors are deliberate direct-storage probes of the common key, not valid managed API names.
    public static IEnumerable<TestCaseData> ManagedNameVectors() =>
        CatalogNameComparisonCases
            .Vectors()
            .Where(v =>
                v.Arguments[0] is string { Length: <= 46 } && v.Arguments[1] is string { Length: > 0 }
            );

    private static ManagedDataStorePersistedState ReadState(IDictionary<string, object> row)
    {
        using JsonDocument descriptor = JsonDocument.Parse((string)row["EndpointDescriptor"]);
        JsonElement e = descriptor.RootElement;
        ManagedDataStoreEndpoint endpoint = new(
            e[1].GetString()!,
            e[2].GetString()!,
            e[3].GetString()!,
            e[4].ValueKind == JsonValueKind.Null ? null : e[4].GetInt32(),
            e[5].GetString(),
            e[6].GetString()!
        );
        DmsTargetDatabaseName
            .TryCreate(
                (string)row["TargetDatabaseName"],
                ProtectedDatabaseNames.Create("cms", []),
                out var target
            )
            .Should()
            .Be(DmsTargetNameValidation.Valid);
        DmsDatabaseTemplate.TryFromWireValue((string)row["Template"], out var template).Should().BeTrue();
        DmsTrustedArtifactPin pin = new()
        {
            ContractVersion = (int)row["PinnedContractVersion"],
            Provider = (string)row["PinnedProvider"],
            PackageId = (string)row["PinnedPackageId"],
            PackageVersion = (string)row["PinnedPackageVersion"],
            PackageSha256 = (string)row["PinnedPackageSha256"],
            ArtifactSha256 = (string)row["PinnedArtifactSha256"],
            TemplateKind = (string)row["PinnedTemplateKind"],
            DataStandardVersion = (string)row["PinnedDataStandardVersion"],
            EffectiveSchemaHash = (string)row["PinnedEffectiveSchemaHash"],
            InventorySha256 = (string)row["PinnedInventorySha256"],
            ApiSchemaFormatVersion = (string)row["PinnedApiSchemaFormatVersion"],
            RelationalMappingVersion = (string)row["PinnedRelationalMappingVersion"],
        };
        DmsProvisioningFailureCode? provisioningCode = row["LastFailureProvisioningCode"] is string code
            ? Enum.Parse<DmsProvisioningFailureCode>(code)
            : null;
        ManagedDataStoreFailure? failure = row["LastFailureCategory"] is string category
            ? ManagedDataStoreFailure.Rehydrate(
                Enum.Parse<DmsProvisioningFailureCategory>(category),
                provisioningCode,
                (string?)row["LastFailureJobCode"]
            )
            : null;
        return new(
            (int)row["Id"],
            (long?)row["TenantId"],
            (string)row["Name"],
            template!,
            target,
            (Guid)row["ExpectedSourceIdentity"],
            endpoint,
            pin,
            Enum.Parse<ManagedDataStoreStatus>((string)row["Status"]),
            (int?)row["DataStoreId"],
            (int?)row["HistoricalDataStoreId"],
            (string?)row["HistoricalDataStoreName"],
            (string)row["CreateJobId"],
            (string?)row["DeleteJobId"],
            (int)row["LastAttempt"],
            failure,
            new DateTimeOffset((DateTime)row["CreatedAt"], TimeSpan.Zero),
            new DateTimeOffset((DateTime)row["UpdatedAt"], TimeSpan.Zero),
            row["DeletedAt"] is DateTime deletedAt ? new DateTimeOffset(deletedAt, TimeSpan.Zero) : null,
            new DateTimeOffset((DateTime)row["LastRefreshed"], TimeSpan.Zero),
            new DateTimeOffset((DateTime)row["LastModifiedDate"], TimeSpan.Zero)
        );
    }

    private const string INVENTORY = """
        SELECT t.name AS TableName, i.name AS IndexName,
        STRING_AGG(CONCAT(c.name, ':', COALESCE(c.collation_name, ''), COALESCE(' AS ' + cc.definition, '')), ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
        + COALESCE(' WHERE ' + i.filter_definition, '') AS Definition
        FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.indexes i ON i.object_id = t.object_id
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        WHERE s.name = 'dmscs' AND t.name IN ('DataStore', 'DataStoreManage') AND i.is_unique = 1
        GROUP BY t.name, i.name, i.filter_definition;
        """;

    private static async Task RejectAsync(Func<Task> action, string constraint) =>
        (await action.Should().ThrowAsync<DbException>()).Which.Message.Should().Contain(constraint);

    private static Task<int> UpdateAsync(SqlConnection connection, int id, string column, object? value) =>
        connection.ExecuteAsync(
            $"""UPDATE dmscs."DataStoreManage" SET "{column}" = @Value WHERE "Id" = @Id;""",
            new { Id = id, Value = value }
        );

    private static Task<long> InsertTenantAsync(SqlConnection connection, string name) =>
        connection.ExecuteScalarAsync<long>(
            """INSERT INTO dmscs."Tenant" ("Name") OUTPUT inserted.Id VALUES (@Name);""",
            new { Name = name }
        );

    private static Task<int> InsertOrdinaryAsync(SqlConnection connection, string name) =>
        connection.ExecuteScalarAsync<int>(
            """INSERT INTO dmscs."DataStore" ("Name", "DataStoreType") OUTPUT inserted.Id VALUES (@Name, 'SQL');""",
            new { Name = name }
        );

    private static Task<int> InsertAsync(
        SqlConnection connection,
        string name,
        long? tenantId = null,
        Dictionary<string, object?>? values = null
    )
    {
        DateTime timestamp = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
        Dictionary<string, object?> fields = new()
        {
            ["Name"] = name,
            ["TenantId"] = tenantId,
            ["Template"] = "Minimal",
            ["Status"] = "PendingCreate",
            ["TargetDatabaseName"] = "target_" + Guid.NewGuid().ToString("N"),
            ["ExpectedSourceIdentity"] = Guid.NewGuid(),
            ["EndpointDescriptor"] = Endpoint.CanonicalEncoding,
            ["CreateJobId"] = Guid.NewGuid().ToString("N"),
            ["CreatedAt"] = timestamp,
            ["UpdatedAt"] = timestamp,
            ["LastRefreshed"] = timestamp,
            ["LastModifiedDate"] = timestamp,
        };
        if (values is not null)
        {
            foreach ((string key, object? value) in values)
            {
                fields[key] = value;
            }
        }
        string columns = string.Join(", ", fields.Keys.Select(k => $"\"{k}\""));
        string parameters = string.Join(", ", fields.Keys.Select(k => "@" + k));
        return connection.ExecuteScalarAsync<int>(
            $"""INSERT INTO dmscs."DataStoreManage" ({columns}) OUTPUT inserted.Id VALUES ({parameters}) ;""",
            fields
        );
    }
}
