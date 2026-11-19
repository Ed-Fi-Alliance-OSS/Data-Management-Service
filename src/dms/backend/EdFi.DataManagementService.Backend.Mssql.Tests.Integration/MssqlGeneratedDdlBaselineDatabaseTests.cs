// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal sealed record MssqlGeneratedDdlBaselineMutableCounts(
    long DocumentCount,
    long SchoolCount,
    long SchoolAddressCount
);

[TestFixture]
[Category(MssqlCiShards.Shard4)]
public class Given_MssqlGeneratedDdlBaselineSnapshotPath
{
    [Test]
    public void It_preserves_linux_container_path_separators()
    {
        var snapshotPath = MssqlGeneratedDdlBaselineDatabase.BuildSnapshotPath(
            physicalName: "/var/opt/mssql/data/dmsfp123.mdf",
            databaseName: "dmsfp123",
            fileId: 1,
            logicalName: "dmsfp123"
        );

        snapshotPath.Should().Be("/var/opt/mssql/data/dmsfp123_baseline_1_dmsfp123.ss");
    }

    [Test]
    public void It_preserves_windows_path_separators()
    {
        var snapshotPath = MssqlGeneratedDdlBaselineDatabase.BuildSnapshotPath(
            physicalName: @"C:\SqlData\dmsfp123.mdf",
            databaseName: "dmsfp123",
            fileId: 1,
            logicalName: "dmsfp123"
        );

        snapshotPath.Should().Be(@"C:\SqlData\dmsfp123_baseline_1_dmsfp123.ss");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
public class Given_MssqlGeneratedDdlBaselineDatabase
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    private MssqlGeneratedDdlFixture _fixture = null!;

    [SetUp]
    public void Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
    }

    [Test]
    public async Task It_restores_a_returned_slot_to_a_clean_snapshot_baseline_before_reuse()
    {
        await using var baselineDatabase = await MssqlGeneratedDdlBaselineDatabase.CreateAsync(
            CreateFixtureSignature("clean-snapshot-baseline"),
            _fixture.GeneratedDdl
        );

        string databaseName = string.Empty;
        string snapshotName = string.Empty;
        MssqlGeneratedDdlBaselineCounts baselineCounts = null!;
        MssqlGeneratedDdlDocumentState firstDocumentState = null!;
        var firstCollectionItemId = 0L;

        await using (var firstLease = await baselineDatabase.AcquireRestoredDatabaseAsync())
        {
            databaseName = firstLease.Database.DatabaseName;
            snapshotName = firstLease.SnapshotName;
            baselineCounts = await ReadBaselineCountsAsync(firstLease.Database);
            firstDocumentState = await InsertSchoolDocumentAsync(
                firstLease.Database,
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                100
            );
            firstCollectionItemId = await InsertSchoolAddressAsync(
                firstLease.Database,
                firstDocumentState.DocumentId,
                1,
                "Austin"
            );
        }

        await using var secondLease = await baselineDatabase.AcquireRestoredDatabaseAsync();
        var resetBaselineCounts = await ReadBaselineCountsAsync(secondLease.Database);
        var resetMutableCounts = await ReadMutableCountsAsync(secondLease.Database);
        var secondDocumentState = await InsertSchoolDocumentAsync(
            secondLease.Database,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            100
        );
        var secondCollectionItemId = await InsertSchoolAddressAsync(
            secondLease.Database,
            secondDocumentState.DocumentId,
            1,
            "Austin"
        );

        secondLease.Database.DatabaseName.Should().Be(databaseName);
        secondLease.SnapshotName.Should().Be(snapshotName);
        resetBaselineCounts.Should().Be(baselineCounts);
        resetMutableCounts.Should().Be(new MssqlGeneratedDdlBaselineMutableCounts(0, 0, 0));
        secondDocumentState.Should().Be(firstDocumentState);
        secondCollectionItemId.Should().Be(firstCollectionItemId);
    }

    [Test]
    public async Task It_refreshes_the_reset_plan_after_restoring_a_returned_slot()
    {
        await using var baselineDatabase = await MssqlGeneratedDdlBaselineDatabase.CreateAsync(
            CreateFixtureSignature("refresh-reset-plan-after-restore"),
            _fixture.GeneratedDdl
        );

        await using (var firstLease = await baselineDatabase.AcquireRestoredDatabaseAsync())
        {
            await firstLease.Database.ExecuteNonQueryAsync(
                """
                IF SCHEMA_ID(N'dms_test') IS NULL EXEC(N'CREATE SCHEMA [dms_test];');

                CREATE TABLE [dms_test].[ResetPlanScratch]
                (
                    [Id] int IDENTITY(1, 1) NOT NULL
                );
                """
            );
            await firstLease.Database.RefreshResetPlanAsync();

            firstLease.Database.ResetPlan.Sql.Should().Contain("[dms_test].[ResetPlanScratch]");
        }

        await using var secondLease = await baselineDatabase.AcquireRestoredDatabaseAsync();

        (await TableExistsAsync(secondLease.Database, "dms_test", "ResetPlanScratch")).Should().BeFalse();
        secondLease.Database.ResetPlan.Sql.Should().NotContain("[dms_test].[ResetPlanScratch]");

        await secondLease.Database.ResetAsync();
    }

    [Test]
    public async Task It_provides_isolated_slots_to_overlapping_consumers_sharing_a_fixture_signature()
    {
        var fixtureSignature = CreateFixtureSignature("isolated-overlapping-consumers");
        await using var firstBaseline = await MssqlGeneratedDdlBaselineDatabase.CreateAsync(
            fixtureSignature,
            _fixture.GeneratedDdl
        );
        await using var secondBaseline = await MssqlGeneratedDdlBaselineDatabase.CreateAsync(
            fixtureSignature,
            _fixture.GeneratedDdl
        );
        await using var firstLease = await firstBaseline.AcquireRestoredDatabaseAsync();
        await using var secondLease = await secondBaseline.AcquireRestoredDatabaseAsync();

        firstLease.Database.DatabaseName.Should().NotBe(secondLease.Database.DatabaseName);
        firstLease.SnapshotName.Should().NotBe(secondLease.SnapshotName);

        var firstDocumentState = await InsertSchoolDocumentAsync(
            firstLease.Database,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            100
        );
        var firstCollectionItemId = await InsertSchoolAddressAsync(
            firstLease.Database,
            firstDocumentState.DocumentId,
            1,
            "Austin"
        );

        var secondMutableCountsBeforeInsert = await ReadMutableCountsAsync(secondLease.Database);
        var secondDocumentState = await InsertSchoolDocumentAsync(
            secondLease.Database,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            100
        );
        var secondCollectionItemId = await InsertSchoolAddressAsync(
            secondLease.Database,
            secondDocumentState.DocumentId,
            1,
            "Austin"
        );

        secondMutableCountsBeforeInsert.Should().Be(new MssqlGeneratedDdlBaselineMutableCounts(0, 0, 0));
        secondDocumentState.Should().Be(firstDocumentState);
        secondCollectionItemId.Should().Be(firstCollectionItemId);
    }

    [Test]
    public async Task It_keeps_pooled_slots_alive_until_the_last_manager_and_slot_lease_are_disposed()
    {
        var fixtureSignature = CreateFixtureSignature("shared-baseline-reuse");
        MssqlGeneratedDdlBaselineDatabase? firstBaseline =
            await MssqlGeneratedDdlBaselineDatabase.CreateAsync(fixtureSignature, _fixture.GeneratedDdl);
        MssqlGeneratedDdlBaselineDatabase? secondBaseline = null;
        MssqlGeneratedDdlBaselineLease? baselineLease = null;
        var databaseName = string.Empty;
        var snapshotName = string.Empty;

        try
        {
            secondBaseline = await MssqlGeneratedDdlBaselineDatabase.CreateAsync(
                fixtureSignature,
                _fixture.GeneratedDdl
            );
            baselineLease = await secondBaseline.AcquireRestoredDatabaseAsync();
            databaseName = baselineLease.Database.DatabaseName;
            snapshotName = baselineLease.SnapshotName;

            await firstBaseline.DisposeAsync();
            firstBaseline = null;
            await secondBaseline.DisposeAsync();
            secondBaseline = null;

            var databaseExistsAfterManagerDispose = await DatabaseExistsAsync(databaseName);
            var snapshotExistsAfterManagerDispose = await DatabaseExistsAsync(snapshotName);
            databaseExistsAfterManagerDispose.Should().BeTrue();
            snapshotExistsAfterManagerDispose.Should().BeTrue();

            var mutableCounts = await ReadMutableCountsAsync(baselineLease.Database);
            mutableCounts.Should().Be(new MssqlGeneratedDdlBaselineMutableCounts(0, 0, 0));
        }
        finally
        {
            if (baselineLease is not null)
            {
                await baselineLease.DisposeAsync();
            }

            if (secondBaseline is not null)
            {
                await secondBaseline.DisposeAsync();
            }

            if (firstBaseline is not null)
            {
                await firstBaseline.DisposeAsync();
            }
        }

        var databaseExistsAfterLastDispose = await DatabaseExistsAsync(databaseName);
        var snapshotExistsAfterLastDispose = await DatabaseExistsAsync(snapshotName);
        databaseExistsAfterLastDispose.Should().BeFalse();
        snapshotExistsAfterLastDispose.Should().BeFalse();
    }

    [Test]
    public async Task It_discovers_and_drops_all_owned_snapshots_before_dropping_the_source_database()
    {
        MssqlGeneratedDdlBaselineDatabase? baselineDatabase =
            await MssqlGeneratedDdlBaselineDatabase.CreateAsync(
                CreateFixtureSignature("composite-snapshot-source-teardown"),
                _fixture.GeneratedDdl
            );
        MssqlGeneratedDdlBaselineLease? lease = null;
        var databaseName = string.Empty;
        var baselineSnapshotName = string.Empty;
        var additionalSnapshotName = string.Empty;

        try
        {
            lease = await baselineDatabase.AcquireRestoredDatabaseAsync();
            databaseName = lease.Database.DatabaseName;
            baselineSnapshotName = lease.SnapshotName;
            additionalSnapshotName = $"{databaseName}_additional";
            await CreateAdditionalSnapshotAsync(databaseName, additionalSnapshotName);

            await lease.DisposeAsync();
            lease = null;
            await baselineDatabase.DisposeAsync();
            baselineDatabase = null;

            (await DatabaseExistsAsync(baselineSnapshotName)).Should().BeFalse();
            (await DatabaseExistsAsync(additionalSnapshotName)).Should().BeFalse();
            (await DatabaseExistsAsync(databaseName)).Should().BeFalse();
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }

            if (baselineDatabase is not null)
            {
                await baselineDatabase.DisposeAsync();
            }
        }
    }

    private static async Task<MssqlGeneratedDdlBaselineCounts> ReadBaselineCountsAsync(
        MssqlGeneratedDdlTestDatabase database
    )
    {
        return new(
            await ReadTableCountAsync(database, "[dms].[EffectiveSchema]"),
            await ReadTableCountAsync(database, "[dms].[ResourceKey]"),
            await ReadTableCountAsync(database, "[dms].[SchemaComponent]")
        );
    }

    private static async Task<MssqlGeneratedDdlBaselineMutableCounts> ReadMutableCountsAsync(
        MssqlGeneratedDdlTestDatabase database
    )
    {
        return new(
            await ReadTableCountAsync(database, "[dms].[Document]"),
            await ReadTableCountAsync(database, "[edfi].[School]"),
            await ReadTableCountAsync(database, "[edfi].[SchoolAddress]")
        );
    }

    private static Task<long> ReadTableCountAsync(
        MssqlGeneratedDdlTestDatabase database,
        string qualifiedTableName
    )
    {
        return database.ExecuteScalarAsync<long>($"""SELECT COUNT(*) FROM {qualifiedTableName};""");
    }

    private static Task<bool> TableExistsAsync(
        MssqlGeneratedDdlTestDatabase database,
        string schema,
        string tableName
    )
    {
        return database.ExecuteScalarAsync<bool>(
            """
            SELECT CASE
                WHEN EXISTS (
                    SELECT 1
                    FROM sys.tables tables
                    INNER JOIN sys.schemas schemas
                        ON schemas.[schema_id] = tables.[schema_id]
                    WHERE schemas.[name] = @schema
                      AND tables.[name] = @tableName
                )
                THEN CAST(1 AS bit)
                ELSE CAST(0 AS bit)
            END;
            """,
            new SqlParameter("@schema", schema),
            new SqlParameter("@tableName", tableName)
        );
    }

    private static async Task<MssqlGeneratedDdlDocumentState> InsertSchoolDocumentAsync(
        MssqlGeneratedDdlTestDatabase database,
        Guid documentUuid,
        int schoolId
    )
    {
        var resourceKeyId = await database.ExecuteScalarAsync<short>(
            """
            SELECT [ResourceKeyId]
            FROM [dms].[ResourceKey]
            WHERE [ProjectName] = @projectName
              AND [ResourceName] = @resourceName;
            """,
            new SqlParameter("@projectName", "Ed-Fi"),
            new SqlParameter("@resourceName", "School")
        );

        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            VALUES (@documentUuid, @resourceKeyId);
            """,
            new SqlParameter("@documentUuid", documentUuid),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );

        var documentRows = await database.QueryRowsAsync(
            """
            SELECT [DocumentId], [ContentVersion]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid)
        );
        var documentRow = documentRows.Should().ContainSingle().Which;
        var documentState = new MssqlGeneratedDdlDocumentState(
            Convert.ToInt64(documentRow["DocumentId"]),
            Convert.ToInt64(documentRow["ContentVersion"])
        );

        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[School] ([DocumentId], [SchoolId])
            VALUES (@documentId, @schoolId);
            """,
            new SqlParameter("@documentId", documentState.DocumentId),
            new SqlParameter("@schoolId", schoolId)
        );

        return documentState;
    }

    private static Task<long> InsertSchoolAddressAsync(
        MssqlGeneratedDdlTestDatabase database,
        long schoolDocumentId,
        int ordinal,
        string city
    )
    {
        return InsertSchoolAddressCoreAsync(database, schoolDocumentId, ordinal, city);
    }

    private static async Task<long> InsertSchoolAddressCoreAsync(
        MssqlGeneratedDdlTestDatabase database,
        long schoolDocumentId,
        int ordinal,
        string city
    )
    {
        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[SchoolAddress] ([School_DocumentId], [Ordinal], [City])
            VALUES (@schoolDocumentId, @ordinal, @city);
            """,
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter("@city", city)
        );

        return await database.ExecuteScalarAsync<long>(
            """
            SELECT [CollectionItemId]
            FROM [edfi].[SchoolAddress]
            WHERE [School_DocumentId] = @schoolDocumentId
              AND [Ordinal] = @ordinal;
            """,
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@ordinal", ordinal)
        );
    }

    private static string CreateFixtureSignature(string scenario)
    {
        return $"{FixtureRelativePath}#{scenario}";
    }

    internal static async Task CreateAdditionalSnapshotAsync(string databaseName, string snapshotName)
    {
        IReadOnlyList<MssqlDatabaseFileMetadata> databaseFiles =
            await MssqlGeneratedDdlBackupBaselineDatabase.ReadDatabaseFilesAsync(databaseName);
        var snapshotFileDefinitions = string.Join(
            "," + Environment.NewLine,
            databaseFiles
                .Where(file => file.Type.Equals("D", StringComparison.OrdinalIgnoreCase))
                .Select(file =>
                {
                    var snapshotPath = MssqlTestDatabaseHelper.BuildSiblingFilePath(
                        file.PhysicalName,
                        $"{snapshotName}_{file.FileId}.ss"
                    );
                    return $"""    ( NAME = N'{MssqlTestDatabaseHelper.EscapeSqlLiteral(file.LogicalName)}', FILENAME = N'{MssqlTestDatabaseHelper.EscapeSqlLiteral(snapshotPath)}' )""";
                })
        );
        var sql = $"""
            CREATE DATABASE {MssqlTestDatabaseHelper.QuoteIdentifier(snapshotName)}
            ON
            {snapshotFileDefinitions}
            AS SNAPSHOT OF {MssqlTestDatabaseHelper.QuoteIdentifier(databaseName)};
            """;

        await MssqlDatabaseLifecycleCoordinator.ExecuteAsync(async connection =>
        {
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 300;
            await command.ExecuteNonQueryAsync();
        });
    }

    internal static async Task<bool> DatabaseExistsAsync(string databaseName)
    {
        await using SqlConnection connection = new(Configuration.MssqlAdminConnectionString!);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE
                WHEN EXISTS (
                    SELECT 1
                    FROM sys.databases
                    WHERE [name] = @databaseName
                )
                THEN CAST(1 AS bit)
                ELSE CAST(0 AS bit)
            END;
            """;
        command.Parameters.Add(new SqlParameter("@databaseName", databaseName));

        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }
}

/// <summary>
/// Two baseline slots, each with two snapshots of its source. The first slot's teardown is paused inside
/// the lifecycle gate, after its snapshot cleanup and before its source fence, and the second slot's
/// teardown starts while it waits. Every source fence must find its source without any snapshot that
/// refers to it.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
public class Given_Concurrently_Disposed_Baseline_Slots
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";
    private const int SlotCount = 2;

    private readonly List<MssqlGeneratedDdlBaselineDatabase> _managers = [];
    private readonly List<MssqlGeneratedDdlBaselineLease> _leases = [];
    private readonly List<string> _sourceNames = [];
    private readonly List<string> _snapshotNames = [];
    private readonly ConcurrentQueue<SourceFenceObservation> _observations = new();
    private readonly ConcurrentBag<Exception> _disposalExceptions = [];
    private readonly TaskCompletionSource _firstAtFence = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource _releaseFirst = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private bool _secondPendingWhileFirstHeld;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        MssqlGeneratedDdlFixture fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath
        );

        for (var index = 0; index < SlotCount; index++)
        {
            MssqlGeneratedDdlBaselineDatabase manager = await MssqlGeneratedDdlBaselineDatabase.CreateAsync(
                $"{FixtureRelativePath}#concurrent-slot-teardown-{index}-{Guid.NewGuid():N}",
                fixture.GeneratedDdl
            );
            _managers.Add(manager);

            MssqlGeneratedDdlBaselineLease lease = await manager.AcquireRestoredDatabaseAsync();
            _leases.Add(lease);

            var sourceName = lease.Database.DatabaseName;
            var additionalSnapshotName = $"{sourceName}_additional";
            _sourceNames.Add(sourceName);
            _snapshotNames.Add(lease.SnapshotName);
            await Given_MssqlGeneratedDdlBaselineDatabase.CreateAdditionalSnapshotAsync(
                sourceName,
                additionalSnapshotName
            );
            _snapshotNames.Add(additionalSnapshotName);
        }

        await TestContext.Progress.WriteLineAsync(
            $"{nameof(Given_Concurrently_Disposed_Baseline_Slots)} databases: sources "
                + $"{string.Join(", ", _sourceNames)}; snapshots {string.Join(", ", _snapshotNames)}"
        );

        MssqlTestDatabaseHelper.BeforeSourceFenceObservationHook = ObserveSourceFenceAsync;
        List<Task> disposals = [];
        try
        {
            Task firstDisposal = DisposeSlotAsync(0);
            disposals.Add(firstDisposal);

            // A first disposal that fails before its fence never signals; stop waiting when it ends.
            if (await Task.WhenAny(_firstAtFence.Task, firstDisposal) == _firstAtFence.Task)
            {
                Task secondDisposal = DisposeSlotAsync(1);
                disposals.Add(secondDisposal);
                _secondPendingWhileFirstHeld =
                    !secondDisposal.IsCompleted
                    && !_observations.Any(observation => observation.SourceName == _sourceNames[1]);
            }
        }
        finally
        {
            // A first disposal left paused would hold the lifecycle gate for every later fixture.
            _releaseFirst.TrySetResult();

            // Drain with no timeout: an abandoned disposal would keep running into later fixtures
            // while holding the lifecycle gate. The test process hang limit bounds a hang.
            await Task.WhenAll(disposals);
            MssqlTestDatabaseHelper.BeforeSourceFenceObservationHook = null;
        }
    }

    private Task DisposeSlotAsync(int index) =>
        Task.Run(async () =>
        {
            try
            {
                await _leases[index].DisposeAsync();
                await _managers[index].DisposeAsync();
            }
            catch (Exception exception)
            {
                _disposalExceptions.Add(exception);
            }
        });

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        MssqlTestDatabaseHelper.BeforeSourceFenceObservationHook = null;

        List<Exception> cleanupExceptions = [];
        foreach (IAsyncDisposable disposable in _leases.Cast<IAsyncDisposable>().Concat(_managers))
        {
            try
            {
                await disposable.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupExceptions.Add(exception);
            }
        }

        foreach (var snapshotName in _snapshotNames)
        {
            await MssqlDatabaseLifecycleCoordinator.ExecuteAsync(connection =>
                MssqlTestDatabaseHelper.DropSnapshotIfExistsAsync(connection, snapshotName, 300)
            );
        }

        foreach (var sourceName in _sourceNames)
        {
            if (await Given_MssqlGeneratedDdlBaselineDatabase.DatabaseExistsAsync(sourceName))
            {
                await MssqlTestDatabaseHelper.DropDatabaseUnderLifecycleGateAsync(sourceName);
            }
        }

        if (cleanupExceptions.Count != 0)
        {
            await TestContext.Progress.WriteLineAsync(
                $"{nameof(Given_Concurrently_Disposed_Baseline_Slots)} cleanup disposal failures: "
                    + string.Join(Environment.NewLine, cleanupExceptions)
            );
        }
    }

    [Test]
    public void It_starts_the_second_disposal_while_the_first_holds_the_gate()
    {
        // The arrangement this fixture depends on: the second teardown was in flight while the first
        // waited inside the lifecycle gate, before its source fence.
        _secondPendingWhileFirstHeld.Should().BeTrue(DescribeObservations());
    }

    [Test]
    public void It_observes_every_source_fence_while_the_source_exists()
    {
        _sourceNames
            .Should()
            .OnlyContain(
                sourceName =>
                    _observations.Any(observation =>
                        observation.SourceName == sourceName && observation.SourceExists
                    ),
                DescribeObservations()
            );
    }

    [Test]
    public void It_drops_every_owned_snapshot_before_its_source_fence()
    {
        _observations
            .Should()
            .OnlyContain(observation => observation.OwnedSnapshotNames.Count == 0, DescribeObservations());
    }

    [Test]
    public async Task It_leaves_no_snapshot_or_source_behind()
    {
        List<string> remainingDatabases = [];
        foreach (var databaseName in _snapshotNames.Concat(_sourceNames))
        {
            if (await Given_MssqlGeneratedDdlBaselineDatabase.DatabaseExistsAsync(databaseName))
            {
                remainingDatabases.Add(databaseName);
            }
        }

        remainingDatabases.Should().BeEmpty(DescribeObservations());
        _disposalExceptions.Should().BeEmpty(DescribeObservations());
    }

    private async Task ObserveSourceFenceAsync(SqlConnection connection, string databaseName)
    {
        if (!_sourceNames.Contains(databaseName, StringComparer.Ordinal))
        {
            return;
        }

        const string sql = """
            SELECT CASE WHEN DB_ID(@databaseName) IS NULL THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END;

            SELECT [name]
            FROM sys.databases
            WHERE [source_database_id] = DB_ID(@databaseName)
            ORDER BY [name];
            """;

        bool sourceExists;
        List<string> ownedSnapshotNames = [];
        await using (SqlCommand command = connection.CreateCommand())
        {
            command.CommandText = sql;
            command.Parameters.Add(new SqlParameter("@databaseName", databaseName));

            await using SqlDataReader reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            sourceExists = reader.GetBoolean(0);
            await reader.NextResultAsync();
            while (await reader.ReadAsync())
            {
                ownedSnapshotNames.Add(reader.GetString(0));
            }
        }

        _observations.Enqueue(new(databaseName, sourceExists, ownedSnapshotNames));

        if (string.Equals(databaseName, _sourceNames[0], StringComparison.Ordinal))
        {
            // Hold the first teardown inside the lifecycle gate until the second has started. A retry
            // that re-enters the hook does not pause again, because the release is already complete.
            _firstAtFence.TrySetResult();
            await _releaseFirst.Task;
        }
    }

    private string DescribeObservations()
    {
        return "source fence observations:"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                _observations.Select(observation =>
                    $"  {observation.SourceName}: exists={observation.SourceExists}, "
                    + $"owned snapshots=[{string.Join(", ", observation.OwnedSnapshotNames)}]"
                )
            )
            + Environment.NewLine
            + $"disposal exceptions: {(_disposalExceptions.IsEmpty ? "none" : string.Join(Environment.NewLine, _disposalExceptions))}";
    }

    private sealed record SourceFenceObservation(
        string SourceName,
        bool SourceExists,
        IReadOnlyList<string> OwnedSnapshotNames
    );
}
