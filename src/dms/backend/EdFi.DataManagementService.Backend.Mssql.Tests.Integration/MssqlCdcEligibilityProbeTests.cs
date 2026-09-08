// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Cdc.Control;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// The pre-binding eligibility gate against a live SQL Server instance database. It runs before a
/// binding exists, so it takes no administrative mutex and mutates nothing, and it reports the
/// lifecycle, the cache-ahead latch, and the three row-presence facts as one consistent read.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
[Category("CdcEligibilityProbe")]
public class Given_A_Mssql_CdcEligibilityProbe
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";
    private const string OperationId = "operation-1";
    private const string SetupControllerRunId = "run-1";
    private const string ProofId = "proof-1";

    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly QualifiedResourceName PersonResource = new("Ed-Fi", "Person");

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;

    [SetUp]
    public async Task SetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server CDC eligibility probe tests require a MssqlAdmin connection string."
        );

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);

        // The DMS SQL Server provisioner requires READ_COMMITTED_SNAPSHOT and refuses a database that
        // does not have it, so an instance database the gate ever runs against always has it. The test
        // database is created without it, so it is set here to match what the gate is entitled to
        // assume: a single-statement read that neither waits on a writer nor sees an uncommitted row.
        await SetReadCommittedSnapshotOnAsync(_database.DatabaseName);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task It_reports_an_empty_new_database_as_eligible_evidence()
    {
        CoreCdc.InitialCdcEligibilityObservation observation = await ProbeAsync();

        using var _ = new AssertionScope();
        observation.LifecycleState.Should().Be(CoreCdc.CdcLifecycleState.Disabled);
        observation.CacheAheadState.Should().Be(CoreCdc.CdcCacheAheadState.Clear);
        observation.CanonicalRowsPresent.Should().BeFalse();
        observation.CacheRowsPresent.Should().BeFalse();
        observation.WorkRowsPresent.Should().BeFalse();
        observation.ConsistencyScope.Should().Be(CoreCdc.CdcConsistencyScope.SingleProviderTransaction);
        observation.ProviderConsistencyToken.Should().NotBeNullOrWhiteSpace();
        observation.DurableObservedAt.Should().BeOnOrBefore(observation.ObservedAt);
        observation.PhysicalSourceFingerprint.Should().Be(await ExpectedFingerprintAsync());
        observation.Diagnostics.Should().BeEmpty();
        Validate(observation).Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task It_reports_a_canonical_row()
    {
        await InsertDocumentAsync();

        CoreCdc.InitialCdcEligibilityObservation observation = await ProbeAsync();

        using var _ = new AssertionScope();
        observation.CanonicalRowsPresent.Should().BeTrue();
        observation.CacheRowsPresent.Should().BeFalse();
        observation.WorkRowsPresent.Should().BeFalse();
        Validate(observation).Succeeded.Should().BeFalse();
    }

    [Test]
    public async Task It_reports_a_cache_row()
    {
        long documentId = await InsertDocumentAsync();
        await InsertCacheRowAsync(documentId);

        CoreCdc.InitialCdcEligibilityObservation observation = await ProbeAsync();

        using var _ = new AssertionScope();
        observation.CacheRowsPresent.Should().BeTrue();
        observation.CanonicalRowsPresent.Should().BeTrue();
    }

    [Test]
    public async Task It_reports_a_work_row()
    {
        long documentId = await InsertDocumentAsync();
        await InsertWorkRowAsync(documentId);

        CoreCdc.InitialCdcEligibilityObservation observation = await ProbeAsync();

        observation.WorkRowsPresent.Should().BeTrue();
    }

    [TestCase(DocumentCacheLifecycleState.Resetting, CoreCdc.CdcLifecycleState.Resetting)]
    [TestCase(DocumentCacheLifecycleState.Rebuilding, CoreCdc.CdcLifecycleState.Rebuilding)]
    [TestCase(DocumentCacheLifecycleState.Tracking, CoreCdc.CdcLifecycleState.Tracking)]
    public async Task It_reports_the_durable_lifecycle_state(
        DocumentCacheLifecycleState durableState,
        CoreCdc.CdcLifecycleState expected
    )
    {
        await SetLifecycleAsync(durableState, cacheAheadRecoveryRequired: false);

        CoreCdc.InitialCdcEligibilityObservation observation = await ProbeAsync();

        using var _ = new AssertionScope();
        observation.LifecycleState.Should().Be(expected);
        observation.CacheAheadState.Should().Be(CoreCdc.CdcCacheAheadState.Clear);
    }

    [Test]
    public async Task It_reports_a_set_cache_ahead_latch()
    {
        await SetLifecycleAsync(DocumentCacheLifecycleState.Disabled, cacheAheadRecoveryRequired: true);

        CoreCdc.InitialCdcEligibilityObservation observation = await ProbeAsync();

        observation.CacheAheadState.Should().Be(CoreCdc.CdcCacheAheadState.RecoveryRequired);
    }

    [Test]
    public async Task It_reports_an_absent_data_store_identity_without_a_fingerprint()
    {
        await _database.ExecuteNonQueryAsync("""DROP TABLE [dms].[DataStoreIdentity];""");

        CoreCdc.InitialCdcEligibilityObservation observation = await ProbeAsync();

        using var _ = new AssertionScope();
        observation.PhysicalSourceFingerprint.Should().BeNull();
        observation
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Code == "eligibilityPhysicalSourceUnusable");

        // The rest of the state is still observed: an unidentified physical source is reported, not
        // treated as an unreadable database.
        observation.LifecycleState.Should().Be(CoreCdc.CdcLifecycleState.Disabled);
    }

    [Test]
    public async Task It_reports_an_unprovisioned_database_as_blocking_rather_than_empty()
    {
        await _database.ExecuteNonQueryAsync("""DROP TABLE [dms].[DocumentProjectionWork];""");

        CoreCdc.InitialCdcEligibilityObservation observation = await ProbeAsync();

        using var _ = new AssertionScope();
        observation.LifecycleState.Should().Be(CoreCdc.CdcLifecycleState.Unknown);
        observation.CanonicalRowsPresent.Should().BeTrue();
        observation.CacheRowsPresent.Should().BeTrue();
        observation.WorkRowsPresent.Should().BeTrue();
        Validate(observation).Succeeded.Should().BeFalse();
    }

    /// <summary>
    /// The gate reads committed state only, and reads it without waiting on the writer holding it, so
    /// an in-flight insert is neither observed nor able to block enablement.
    /// </summary>
    [Test]
    public async Task It_observes_one_consistent_committed_state_without_blocking_on_an_open_writer()
    {
        await using SqlConnection writer = new(_database.ConnectionString);
        await writer.OpenAsync();
        await using SqlTransaction uncommitted = (SqlTransaction)await writer.BeginTransactionAsync();
        await InsertDocumentAsync(writer, uncommitted);

        CoreCdc.InitialCdcEligibilityObservation duringWrite = await ProbeAsync();
        await uncommitted.CommitAsync();
        CoreCdc.InitialCdcEligibilityObservation afterCommit = await ProbeAsync();

        using var _ = new AssertionScope();
        duringWrite.CanonicalRowsPresent.Should().BeFalse();
        afterCommit.CanonicalRowsPresent.Should().BeTrue();
        afterCommit
            .ProviderConsistencyToken.Should()
            .NotBe(
                duringWrite.ProviderConsistencyToken,
                "each probe reports the transaction its own read ran in"
            );
    }

    [Test]
    public async Task It_leaves_the_instance_database_unchanged()
    {
        long documentId = await InsertDocumentAsync();
        await InsertCacheRowAsync(documentId);
        await SetLifecycleAsync(DocumentCacheLifecycleState.Disabled, cacheAheadRecoveryRequired: true);

        await ProbeAsync();

        using var _ = new AssertionScope();
        (await ReadCountAsync("Document")).Should().Be(1);
        (await ReadCountAsync("DocumentCache")).Should().Be(1);
        (await ReadCountAsync("DocumentProjectionWork")).Should().Be(0);
        (await ReadLifecycleAsync())
            .Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Disabled, true));
    }

    private Task<CoreCdc.InitialCdcEligibilityObservation> ProbeAsync() =>
        new CdcEligibilityProbe(
            CoreCdc.CdcProvider.SqlServer,
            TimeProvider.System,
            NullLogger<CdcEligibilityProbe>.Instance
        ).ProbeAsync(new(Context(), Proof(), _database.ConnectionString));

    private static CdcObservationContext Context() =>
        new(OperationId, TargetIdentity(), PhysicalSourceFingerprint: null);

    private static CoreCdc.InitialCdcProvisioningProof Proof() =>
        new(
            CoreCdc.CdcJsonContract.CurrentContractVersion,
            ProofId,
            OperationId,
            TargetIdentity(),
            CoreCdc.CdcProvider.SqlServer,
            SetupControllerRunId,
            CoreCdc.CdcDatabaseCreationMode.CreatedForInitialCdcProvisioning,
            CoreCdc.CdcWriteAdmissionState.ClosedNeverOpened,
            ObservedAt
        );

    private static CoreCdc.CdcTargetIdentity TargetIdentity() =>
        new(
            "dms",
            CoreCdc.CdcTargetValidator.DefaultBindingTenantKey,
            "1",
            "instance",
            1,
            CoreCdc.CdcProvider.SqlServer
        );

    private static CoreCdc.CdcContractValidationResult Validate(
        CoreCdc.InitialCdcEligibilityObservation observation
    ) =>
        CoreCdc.InitialCdcEligibilityObservationValidator.Validate(
            observation,
            Proof(),
            new(
                OperationId,
                TargetIdentity(),
                PhysicalSourceFingerprint: null,
                DateTimeOffset.UtcNow.AddMinutes(1)
            )
        );

    private async Task<string> ExpectedFingerprintAsync()
    {
        string sourceIdentity = await _database.ExecuteScalarAsync<string>(
            """
            SELECT CONVERT(varchar(64), [SourceIdentity])
            FROM [dms].[DataStoreIdentity]
            WHERE [DataStoreIdentitySingletonId] = 1;
            """
        );

        return CdcSourceFingerprintMetadata.Compute(CdcProvider.SqlServer, sourceIdentity).Value;
    }

    private async Task<long> InsertDocumentAsync()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            InsertDocumentCommandText,
            new SqlParameter("@documentUuid", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() },
            new SqlParameter("@resourceKeyId", SqlDbType.SmallInt) { Value = ResourceKeyId() },
            new SqlParameter("@contentVersion", SqlDbType.BigInt) { Value = 10L },
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = ObservedAt.UtcDateTime }
        );

        return Convert.ToInt64(rows.Single()["DocumentId"]);
    }

    private async Task InsertDocumentAsync(SqlConnection connection, SqlTransaction transaction)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = InsertDocumentCommandText;
        command.Parameters.Add(
            new SqlParameter("@documentUuid", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() }
        );
        command.Parameters.Add(
            new SqlParameter("@resourceKeyId", SqlDbType.SmallInt) { Value = ResourceKeyId() }
        );
        command.Parameters.Add(new SqlParameter("@contentVersion", SqlDbType.BigInt) { Value = 20L });
        command.Parameters.Add(
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = ObservedAt.UtcDateTime }
        );

        await command.ExecuteScalarAsync();
    }

    private const string InsertDocumentCommandText = """
        DECLARE @inserted TABLE ([DocumentId] bigint);

        INSERT INTO [dms].[Document] (
            [DocumentUuid],
            [ResourceKeyId],
            [ContentVersion],
            [ContentLastModifiedAt]
        )
        OUTPUT INSERTED.[DocumentId] INTO @inserted ([DocumentId])
        VALUES (
            @documentUuid,
            @resourceKeyId,
            @contentVersion,
            @lastModifiedAt
        );

        SELECT [DocumentId] FROM @inserted;
        """;

    private async Task InsertCacheRowAsync(long documentId)
    {
        ResourceKeyEntry resourceKey = _fixture.MappingSet.ResourceKeyById[ResourceKeyId()];

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [dms].[DocumentCache] (
                [DocumentId],
                [DocumentUuid],
                [ProjectName],
                [ResourceName],
                [ResourceVersion],
                [ContentVersion],
                [StreamEtag],
                [LastModifiedAt],
                [DocumentJson],
                [ComputedAt]
            )
            SELECT
                [DocumentId],
                [DocumentUuid],
                @projectName,
                @resourceName,
                @resourceVersion,
                [ContentVersion],
                @streamEtag,
                @lastModifiedAt,
                @documentJson,
                @computedAt
            FROM [dms].[Document]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId },
            new SqlParameter("@projectName", SqlDbType.VarChar, 256)
            {
                Value = resourceKey.Resource.ProjectName,
            },
            new SqlParameter("@resourceName", SqlDbType.VarChar, 256)
            {
                Value = resourceKey.Resource.ResourceName,
            },
            new SqlParameter("@resourceVersion", SqlDbType.VarChar, 32)
            {
                Value = resourceKey.ResourceVersion,
            },
            new SqlParameter("@streamEtag", SqlDbType.VarChar, 64) { Value = "etag-10" },
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2) { Value = ObservedAt.UtcDateTime },
            new SqlParameter("@documentJson", SqlDbType.NVarChar, -1)
            {
                Value = new JsonObject { ["value"] = "cache" }.ToJsonString(),
            },
            new SqlParameter("@computedAt", SqlDbType.DateTime2)
            {
                Value = ObservedAt.AddMinutes(1).UtcDateTime,
            }
        );
    }

    private Task InsertWorkRowAsync(long documentId) =>
        _database.ExecuteNonQueryAsync(
            """
            IF NOT EXISTS (SELECT 1 FROM [dms].[DocumentProjectionWork] WHERE [DocumentId] = @documentId)
            BEGIN
                INSERT INTO [dms].[DocumentProjectionWork] (
                    [DocumentId],
                    [RequiredContentVersion],
                    [FirstEnqueuedAt],
                    [LastEnqueuedAt]
                )
                VALUES (
                    @documentId,
                    @requiredContentVersion,
                    @enqueuedAt,
                    @enqueuedAt
                );
            END
            """,
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId },
            new SqlParameter("@requiredContentVersion", SqlDbType.BigInt) { Value = 10L },
            new SqlParameter("@enqueuedAt", SqlDbType.DateTime2) { Value = ObservedAt.UtcDateTime }
        );

    private Task SetLifecycleAsync(DocumentCacheLifecycleState lifecycle, bool cacheAheadRecoveryRequired) =>
        _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[DocumentCacheState]
            SET [ProjectionLifecycleState] = @lifecycle,
                [CacheAheadRecoveryRequired] = @cacheAheadRecoveryRequired
            WHERE [StateId] = 1;
            """,
            new SqlParameter("@lifecycle", SqlDbType.VarChar, 32) { Value = lifecycle.ToString() },
            new SqlParameter("@cacheAheadRecoveryRequired", SqlDbType.Bit)
            {
                Value = cacheAheadRecoveryRequired,
            }
        );

    private async Task<DocumentCacheLifecycleObservation> ReadLifecycleAsync()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _database.QueryRowsAsync(
            """
            SELECT [ProjectionLifecycleState], [CacheAheadRecoveryRequired]
            FROM [dms].[DocumentCacheState]
            WHERE [StateId] = 1;
            """
        );

        IReadOnlyDictionary<string, object?> row = rows.Single();
        return new(
            Enum.Parse<DocumentCacheLifecycleState>((string)row["ProjectionLifecycleState"]!),
            Convert.ToBoolean(row["CacheAheadRecoveryRequired"])
        );
    }

    private Task<int> ReadCountAsync(string tableName) =>
        _database.ExecuteScalarAsync<int>($$"""SELECT COUNT(*) FROM [dms].[{{tableName}}];""");

    private static async Task SetReadCommittedSnapshotOnAsync(string databaseName)
    {
        SqlConnection.ClearAllPools();

        await MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync(
            $"""
            ALTER DATABASE {MssqlTestDatabaseHelper.QuoteIdentifier(databaseName)}
            SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
            """
        );

        SqlConnection.ClearAllPools();
    }

    private short ResourceKeyId() => _fixture.MappingSet.ResourceKeyIdByResource[PersonResource];
}
