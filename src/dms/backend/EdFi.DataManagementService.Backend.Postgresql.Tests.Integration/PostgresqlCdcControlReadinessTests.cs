// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Cdc.Control;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// The CDC control plane's enablement sequence and post-enablement lifecycle against a live PostgreSQL
/// instance database.
/// </summary>
/// <remarks>
/// <para>
/// Every provider-facing participant is the real one: the eligibility probe reads this database, the
/// provider setup creates this database's publication and logical replication slot, the source-position
/// adapter captures and observes a real WAL barrier, and the teardown removes what setup created. The
/// binding state store is the real on-disk store under a per-test root. What the sibling suites already
/// prove in isolation, this suite proves in sequence: that the controller composes those steps in the
/// order the design requires and that each one's real evidence satisfies the next.
/// </para>
/// <para>
/// Kafka, Kafka Connect, the Debezium metrics bridge, the projector's status endpoint, and the guarded
/// activation are stubbed. The first four are broker, worker, and HTTP boundaries with no PostgreSQL
/// behavior of their own, and they are exercised against real infrastructure by the broker-backed
/// control suite; the last is a DocumentCache administrative command with its own coverage. The stubs
/// are not inert, though: the projector's fingerprint is computed from this database's own
/// <c>DataStoreIdentity</c>, the connector's committed offset is read from this database's live WAL
/// position, and the activation performs the lifecycle transition it reports — so the real probe,
/// the real barrier, and the real correlation check all decide on this database's own facts.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("CdcControlReadiness")]
public class Given_A_Postgresql_CdcControlReadinessSequence
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

    private const string OperationId = "operation-1";
    private const string SetupControllerRunId = "run-1";
    private const string TenantKey = CoreCdc.CdcTargetValidator.DefaultBindingTenantKey;
    private const long DataStoreId = 1;
    private const string DeploymentKey = "dms";
    private const string InstanceKey = "instance";
    private const string TopicPrefix = "edfi.documents";
    private const long BindingGeneration = 1;
    private const long ReplacementGeneration = 2;
    private const int PartitionCount = 1;
    private const int MaxRecordBytes = 67_108_864;
    private const string SetupPrincipal = "postgres";

    private static readonly TimeSpan ControlPlaneClockSkew = TimeSpan.FromMinutes(1);

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private string _connectorRoleName = null!;
    private string _bindingStateRoot = null!;
    private StubProjectionCorrelationCollector _projection = null!;
    private StubKafkaAdmin _kafka = null!;
    private StubConnectClient _connect = null!;
    private StubGuardedActivation _activation = null!;
    private ServiceProvider _templateServices = null!;
    private SkewedTimeProvider _clock = null!;
    private long? _committedLsn;

    [SetUp]
    public async Task SetUp()
    {
        AssumePostgresqlLogicalReplicationAvailable();

        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _connectorRoleName = $"cdc_connector_{_database.DatabaseName}";
        CreateConnectorRole(_connectorRoleName);

        _bindingStateRoot = Directory
            .CreateTempSubdirectory($"cdc-binding-state-{Guid.NewGuid():N}")
            .FullName;
        _templateServices = new ServiceCollection().AddCdcConnectorTemplates().BuildServiceProvider();

        _clock = new SkewedTimeProvider(DateTimeOffset.UtcNow.Add(ControlPlaneClockSkew));
        _committedLsn = null;
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await DropReplicationSlotsIfExistAsync();
            await _database.DisposeAsync();
        }

        DropConnectorRoleIfExists();

        if (_templateServices is not null)
        {
            await _templateServices.DisposeAsync();
        }

        if (_bindingStateRoot is not null && Directory.Exists(_bindingStateRoot))
        {
            Directory.Delete(_bindingStateRoot, recursive: true);
        }
    }

    [Test]
    public async Task It_runs_the_initial_readiness_sequence_through_every_real_provider_step()
    {
        ICdcSetupController controller = BuildController(BindingGeneration);

        CoreCdc.CdcAdmission admission = await controller.EnableAsync(EnableRequest(), Deadline());

        using AssertionScope _ = new();

        // Every step whose evidence comes from this database, in the order the sequence runs them.
        admission.Steps.Binding.State.Should().Be(CoreCdc.CdcComponentState.Satisfied);
        admission.Steps.GuardedTrackingActivation.State.Should().Be(CoreCdc.CdcComponentState.Satisfied);
        admission.Steps.ProviderSetup.State.Should().Be(CoreCdc.CdcComponentState.Satisfied);
        admission.Steps.ConnectorAndTopicValidation.State.Should().Be(CoreCdc.CdcComponentState.Satisfied);
        admission.Steps.FirstProjectionCaughtUp.State.Should().Be(CoreCdc.CdcComponentState.Satisfied);
        admission
            .Steps.ProviderBarrier.State.Should()
            .Be(
                CoreCdc.CdcComponentState.Satisfied,
                "the barrier is reached by comparing a real captured WAL position against a real "
                    + "committed offset"
            );
        admission.TargetIdentity.Generation.Should().Be(BindingGeneration);
        admission.TargetIdentity.Provider.Should().Be(CoreCdc.CdcProvider.Postgresql);

        // Write admission itself is not asserted here, and cannot be: the step after the barrier
        // requires the connector's committed offset to sit inside the retained range the provider
        // evidence recorded, and only a connector that is actually streaming advances the replication
        // slot in a way that stays consistent with the sequence's own exact-match re-verification. A
        // stubbed worker can satisfy the barrier or the retained range, never both. Reaching
        // CdcAdmissionState.Admitted end to end is therefore evidence a real Debezium connector has to
        // produce, and the unit sequence suite is where the composed nine-step verdict is pinned.
        admission
            .Steps.SourceHistory.State.Should()
            .NotBe(
                CoreCdc.CdcComponentState.Satisfied,
                "a stubbed connector cannot produce affirmative continuity evidence"
            );
    }

    [Test]
    public async Task It_creates_the_provider_capture_artifacts_the_binding_names()
    {
        ICdcSetupController controller = BuildController(BindingGeneration);
        CoreCdc.CdcArtifactInventory inventory = Inventory(BindingGeneration);

        await controller.EnableAsync(EnableRequest(), Deadline());

        using AssertionScope _ = new();
        (await PublicationExistsAsync(inventory.PostgresqlPublicationName!))
            .Should()
            .BeTrue("enablement creates the publication its binding record names");
        (await ReplicationSlotExistsAsync(inventory.PostgresqlLogicalSlotName!))
            .Should()
            .BeTrue("enablement creates the logical slot its binding record names");
        (await HeartbeatSingletonExistsAsync())
            .Should()
            .BeTrue("the heartbeat singleton is the provider-side evidence the connector advances");
    }

    [Test]
    public async Task It_makes_the_binding_record_durable_before_it_registers_the_connector()
    {
        ICdcSetupController controller = BuildController(BindingGeneration);
        _connect.OnRegister = () =>
            BindingRecordPaths().Should().NotBeEmpty("the binding record is durable before any connector");

        await controller.EnableAsync(EnableRequest(), Deadline());

        using AssertionScope _ = new();
        _connect.RegisteredConnectorName.Should().Be(Inventory(BindingGeneration).ConnectorName);
        BindingRecordPaths().Should().ContainSingle();
    }

    [Test]
    public async Task It_refuses_enablement_when_the_instance_database_already_holds_canonical_rows()
    {
        await InsertCanonicalDocumentAsync();
        ICdcSetupController controller = BuildController(BindingGeneration);
        CoreCdc.CdcArtifactInventory inventory = Inventory(BindingGeneration);

        CoreCdc.CdcAdmission admission = await controller.EnableAsync(EnableRequest(), Deadline());

        using AssertionScope _ = new();
        admission.AdmissionState.Should().NotBe(CoreCdc.CdcAdmissionState.Admitted);

        // Fail-closed means nothing was provisioned, not merely that the admission was refused: an
        // ineligible database must be left exactly as it was found.
        (await PublicationExistsAsync(inventory.PostgresqlPublicationName!))
            .Should()
            .BeFalse();
        (await ReplicationSlotExistsAsync(inventory.PostgresqlLogicalSlotName!)).Should().BeFalse();
        _connect.RegisteredConnectorName.Should().BeNull();
        _activation.Executed.Should().BeFalse("an ineligible database is never activated");
    }

    [Test]
    public async Task It_reports_the_enabled_binding_as_ready_from_the_same_live_database()
    {
        ICdcSetupController controller = BuildController(BindingGeneration);
        await controller.EnableAsync(EnableRequest(), Deadline());

        CoreCdc.CdcStatus status = await controller.StatusAsync(TargetRequest(), Deadline());

        CoreCdc.CdcTargetStatus target = status.Targets.Should().ContainSingle().Subject;

        using AssertionScope _ = new();
        target.TargetIdentity.Generation.Should().Be(BindingGeneration);
        target.TargetIdentity.Provider.Should().Be(CoreCdc.CdcProvider.Postgresql);
        status
            .Readiness.Should()
            .Be(
                CoreCdc.CdcReadiness.Ready,
                "the status reported {0}",
                DescribeDiagnostics(target.Diagnostics)
            );
        target
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CoreCdc.CdcDiagnosticSeverity.Error);
    }

    [Test]
    public async Task It_restarts_the_connector_of_an_enabled_binding_against_live_continuity_evidence()
    {
        ICdcSetupController controller = BuildController(BindingGeneration);
        await controller.EnableAsync(EnableRequest(), Deadline());

        CoreCdc.CdcStatus status = await controller.RestartAsync(TargetRequest(), Deadline());

        using AssertionScope _ = new();
        _connect
            .RestartedConnectorName.Should()
            .Be(
                Inventory(BindingGeneration).ConnectorName,
                "affirmative continuity is what permits a restart"
            );
        status
            .Targets.Should()
            .ContainSingle()
            .Which.TargetIdentity.Generation.Should()
            .Be(BindingGeneration);
    }

    [Test]
    public async Task It_leaves_both_generations_intact_when_a_source_replacement_is_refused()
    {
        ICdcSetupController controller = BuildController(BindingGeneration);
        await controller.EnableAsync(EnableRequest(), Deadline());

        ICdcSetupController replacement = BuildController(ReplacementGeneration);
        CoreCdc.CdcAdmission admission = await replacement.ReplaceSourceAsync(
            new CdcReplaceSourceRequest(
                OperationId,
                TenantKey,
                DataStoreId,
                _database.ConnectionString,
                BindingGeneration,
                ProvisioningEvidence(),
                ProviderSetupInputs()
            ),
            Deadline()
        );

        CoreCdc.CdcArtifactInventory previous = Inventory(BindingGeneration);
        CoreCdc.CdcArtifactInventory replaced = Inventory(ReplacementGeneration);

        using AssertionScope _ = new();

        // A replacement of a target whose enablement never reached write admission is refused, and a
        // refused replacement is a no-op on both generations: the outgoing generation keeps every
        // artifact it owns, and the incoming one acquires none. The success path needs a preceding
        // admitted enablement, which a stubbed connector cannot produce.
        admission.AdmissionState.Should().NotBe(CoreCdc.CdcAdmissionState.Admitted);
        admission.TargetIdentity.Generation.Should().Be(ReplacementGeneration);

        replaced
            .PostgresqlPublicationName.Should()
            .NotBe(
                previous.PostgresqlPublicationName,
                "a new generation never reuses a prior generation's artifact names"
            );
        replaced.PostgresqlLogicalSlotName.Should().NotBe(previous.PostgresqlLogicalSlotName);
        (await PublicationExistsAsync(replaced.PostgresqlPublicationName!)).Should().BeFalse();
        (await ReplicationSlotExistsAsync(replaced.PostgresqlLogicalSlotName!)).Should().BeFalse();
        (await PublicationExistsAsync(previous.PostgresqlPublicationName!))
            .Should()
            .BeTrue("the outgoing generation is retained until it is explicitly retired");
        (await ReplicationSlotExistsAsync(previous.PostgresqlLogicalSlotName!)).Should().BeTrue();
    }

    [Test]
    public async Task It_retires_the_binding_and_removes_the_provider_artifacts_it_created()
    {
        ICdcSetupController controller = BuildController(BindingGeneration);
        await controller.EnableAsync(EnableRequest(), Deadline());
        CoreCdc.CdcArtifactInventory inventory = Inventory(BindingGeneration);

        CoreCdc.CdcContractReadResult<CoreCdc.CdcCleanupProof> retirement = await controller.RetireAsync(
            TargetRequest(),
            Deadline()
        );

        using AssertionScope _ = new();
        retirement
            .Succeeded.Should()
            .BeTrue("retirement reported {0}", DescribeDiagnostics(retirement.Diagnostics));
        (await PublicationExistsAsync(inventory.PostgresqlPublicationName!)).Should().BeFalse();
        (await ReplicationSlotExistsAsync(inventory.PostgresqlLogicalSlotName!)).Should().BeFalse();

        // The record is deleted last and only against a validated cleanup proof, so its absence is the
        // evidence that every governed artifact ahead of it was removed first.
        BindingRecordPaths().Should().BeEmpty();
    }

    private ICdcSetupController BuildController(long generation)
    {
        CdcControlOptions controlOptions = ControlOptions(generation);
        _projection = new StubProjectionCorrelationCollector(
            _clock,
            () => ReadSourceFingerprintAsync().GetAwaiter().GetResult()
        );
        _kafka = new StubKafkaAdmin(controlOptions, _clock);
        _connect = new StubConnectClient(() => ReadCommittedLsnAsync(generation).GetAwaiter().GetResult());
        _activation = new StubGuardedActivation(() => SetLifecycleTrackingAsync().GetAwaiter().GetResult());

        var dataSourceCache = new NpgsqlDataSourceCache(NullLogger<NpgsqlDataSourceCache>.Instance);

        return new CdcSetupController(
            Options.Create(controlOptions),
            new CdcExplicitProjectionTargetProof(TargetConfiguration()),
            _projection,
            new CdcEligibilityProbe(
                CoreCdc.CdcProvider.Postgresql,
                _clock,
                NullLogger<CdcEligibilityProbe>.Instance
            ),
            new CoreCdc.CdcBindingLifecycleService(
                new CoreCdc.LocalCdcBindingStateStore(
                    _bindingStateRoot,
                    CoreCdc.CdcLocalStateStorePermissions.Current,
                    CoreCdc.CdcLocalStateStoreFileSystem.Current,
                    _clock
                ),
                _clock
            ),
            _activation,
            new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]),
            new CdcInstanceDatabaseConnectionFactory(),
            _kafka,
            _templateServices.GetRequiredService<ICdcConnectorTemplateService>(),
            _connect,
            new CdcConnectorObservationMapper(
                _templateServices.GetRequiredService<ICdcConnectorTemplateService>(),
                _clock
            ),
            new StubLagReader(),
            new PostgresqlCdcSourcePositionAdapter(
                dataSourceCache,
                new PostgresqlDocumentCacheProviderCommandTimeoutClassifier(),
                _clock,
                NullLogger<PostgresqlCdcSourcePositionAdapter>.Instance
            ),
            new CdcProviderArtifactTeardown(
                CoreCdc.CdcProvider.Postgresql,
                NullLogger<CdcProviderArtifactTeardown>.Instance
            ),
            _clock,
            NullLogger<CdcSetupController>.Instance
        );
    }

    private CdcControlOptions ControlOptions(long generation)
    {
        NpgsqlConnectionStringBuilder connection = new(_database.ConnectionString);

        return new()
        {
            DeploymentKey = DeploymentKey,
            InstanceKey = InstanceKey,
            TopicPrefix = TopicPrefix,
            Generation = generation,
            PartitionCount = PartitionCount,
            KafkaBootstrapServers = "localhost:9092",
            ConnectBaseUri = "http://localhost:8083",
            ConnectWorkerKey = "worker-1",
            ConnectOffsetStorageTopic = "connect-offsets",
            DurabilityProfile = CdcControlOptions.LocalDurabilityProfile,
            MaxRecordBytes = MaxRecordBytes,
            HeartbeatInterval = TimeSpan.FromSeconds(5),
            AclsEnabled = false,
            SetupPrincipal = SetupPrincipal,
            ProviderConnectionProperties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["database.hostname"] = connection.Host ?? "localhost",
                ["database.port"] = connection.Port.ToString(CultureInfo.InvariantCulture),
                ["database.user"] = _connectorRoleName,
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.dbname"] = _database.DatabaseName,
            },
            DmsBaseUrl = "http://localhost:8080",
            DmsBearerToken = "readiness-suite",
            Timeouts = new()
            {
                ProjectionCaughtUp = TimeSpan.FromSeconds(30),
                ProviderBarrier = TimeSpan.FromSeconds(30),
                PollInterval = TimeSpan.FromMilliseconds(50),
            },
        };
    }

    /// <summary>
    /// The raw configuration the explicit projection-target proof reads. The proof deliberately consults
    /// the operator's own configuration rather than the bound options, so the test supplies the same
    /// section an operator would.
    /// </summary>
    private static IConfiguration TargetConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DataManagement:DocumentCache:Targets:0:TenantKey"] = TenantKey,
                    ["DataManagement:DocumentCache:Targets:0:DataStoreId"] = DataStoreId.ToString(
                        CultureInfo.InvariantCulture
                    ),
                }
            )
            .Build();

    private CdcEnableRequest EnableRequest() =>
        new(
            OperationId,
            TenantKey,
            DataStoreId,
            _database.ConnectionString,
            ProvisioningEvidence(),
            ProviderSetupInputs()
        );

    private CdcTargetOperationRequest TargetRequest() =>
        new(OperationId, TenantKey, DataStoreId, _database.ConnectionString, ProviderSetupInputs());

    private static CdcProvisioningProofEvidence ProvisioningEvidence() =>
        new(
            SetupControllerRunId,
            CdcProvisioningProofFactory.CreatedForInitialCdcProvisioningToken,
            CdcProvisioningProofFactory.ClosedNeverOpenedToken
        );

    private CdcProviderSetupInputs ProviderSetupInputs() =>
        new(
            SetupPrincipal,
            _connectorRoleName,
            _fixture.CdcSourceInventory,
            _fixture.CdcDmsManagedTableInventory
        );

    private static CoreCdc.CdcArtifactInventory Inventory(long generation) =>
        CoreCdc
            .CdcArtifactNameGenerator.Render(
                new CoreCdc.CdcArtifactNameInput(
                    DeploymentKey,
                    TopicPrefix,
                    InstanceKey,
                    generation,
                    CoreCdc.CdcProvider.Postgresql
                )
            )
            .Inventory!;

    /// <summary>
    /// A wall-clock bound on each operation. The control plane's own clock is a single instant, so it
    /// cannot expire a step's budget; this keeps a step that never produces its evidence from hanging
    /// the suite instead.
    /// </summary>
    private static CancellationToken Deadline() => new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token;

    private static string DescribeDiagnostics(IReadOnlyList<CoreCdc.CdcDiagnostic> diagnostics) =>
        diagnostics.Count == 0
            ? "no diagnostics"
            : string.Join(
                ", ",
                diagnostics.Select(diagnostic =>
                    $"{diagnostic.Component}|{diagnostic.Path}|{diagnostic.Code}|{diagnostic.Message}"
                )
            );

    private string[] BindingRecordPaths() =>
        Directory.Exists(Path.Combine(_bindingStateRoot, "bindings"))
            ? Directory.GetFiles(
                Path.Combine(_bindingStateRoot, "bindings"),
                "*.json",
                SearchOption.AllDirectories
            )
            : [];

    private async Task<string> ReadSourceFingerprintAsync()
    {
        string sourceIdentity = await _database.ExecuteScalarAsync<string>(
            """SELECT "SourceIdentity"::text FROM "dms"."DataStoreIdentity" WHERE "DataStoreIdentitySingletonId" = 1;"""
        );

        return CdcSourceFingerprintMetadata.Compute(CdcProvider.Postgresql, sourceIdentity).Value;
    }

    /// <summary>
    /// The position a streaming connector would have committed: the replication slot is advanced to the
    /// database's current WAL position and its confirmed flush position is reported back.
    /// </summary>
    /// <remarks>
    /// Both halves matter. The provider barrier is only reached by an offset at or beyond the WAL
    /// position the real adapter captured, and source-history continuity is only affirmative for an
    /// offset the slot has actually confirmed — a raw WAL position ahead of the slot reads as a
    /// retained-range loss. Advancing the slot is what a consuming connector does, so emulating it is
    /// what lets both real checks decide on real slot state. Before the slot exists, the current WAL
    /// position stands in. It advances once and then holds: a connector that kept moving would leave
    /// the position it reported to an earlier step behind the slot's own retained range, which the
    /// continuity classifier reads — correctly — as a lost record.
    /// </remarks>
    private async Task<long> ReadCommittedLsnAsync(long generation)
    {
        string slotName = Inventory(generation).PostgresqlLogicalSlotName!;

        if (!await ReplicationSlotExistsAsync(slotName))
        {
            return ParseLsn(await _database.ExecuteScalarAsync<string>("SELECT pg_current_wal_lsn()::text;"));
        }

        if (_committedLsn is { } committed)
        {
            return committed;
        }

        // Advance the slot once, as a consuming connector would, and hold the position it reached. The
        // barrier is only reached by an offset at or beyond the WAL position the real adapter captured,
        // and advancing again would move the slot under the sequence's own exact-match re-verification.
        await _database.ExecuteNonQueryAsync(
            $"SELECT pg_replication_slot_advance('{slotName}', pg_current_wal_lsn());"
        );

        _committedLsn = ParseLsn(
            await _database.ExecuteScalarAsync<string>(
                $"SELECT confirmed_flush_lsn::text FROM pg_catalog.pg_replication_slots WHERE slot_name = '{slotName}';"
            )
        );

        return _committedLsn.Value;
    }

    private static long ParseLsn(string lsn)
    {
        string[] parts = lsn.Split('/');

        return unchecked(
            (long)(
                (ulong.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture) << 32)
                | ulong.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            )
        );
    }

    /// <summary>
    /// What the guarded activation leaves behind: the instance database tracking, which the sequence's
    /// own re-read of the real database then observes.
    /// </summary>
    private Task SetLifecycleTrackingAsync() =>
        _database.ExecuteNonQueryAsync(
            """
            UPDATE "dms"."DocumentCacheState"
            SET "ProjectionLifecycleState" = @lifecycle
            WHERE "StateId" = 1;
            """,
            new NpgsqlParameter("lifecycle", NpgsqlDbType.Varchar)
            {
                Value = DocumentCacheLifecycleState.Tracking.ToString(),
            }
        );

    private Task InsertCanonicalDocumentAsync() =>
        _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."Document"
                ("DocumentUuid", "ResourceKeyId", "ContentVersion", "ContentLastModifiedAt", "CreatedAt")
            VALUES (gen_random_uuid(), 1, 1, now(), now());
            """
        );

    private async Task<bool> PublicationExistsAsync(string publicationName) =>
        await _database.ExecuteScalarAsync<long>(
            $"SELECT count(*) FROM pg_catalog.pg_publication WHERE pubname = '{publicationName}';"
        ) > 0;

    private async Task<bool> ReplicationSlotExistsAsync(string slotName) =>
        await _database.ExecuteScalarAsync<long>(
            $"SELECT count(*) FROM pg_catalog.pg_replication_slots WHERE slot_name = '{slotName}';"
        ) > 0;

    private async Task<bool> HeartbeatSingletonExistsAsync() =>
        await _database.ExecuteScalarAsync<long>(
            """SELECT count(*) FROM "dms"."CdcHeartbeat" WHERE "HeartbeatId" = 1;"""
        ) > 0;

    private async Task DropReplicationSlotsIfExistAsync()
    {
        foreach (long generation in new[] { BindingGeneration, ReplacementGeneration })
        {
            string slotName = Inventory(generation).PostgresqlLogicalSlotName!;
            try
            {
                await _database.ExecuteNonQueryAsync(
                    $"SELECT pg_drop_replication_slot('{slotName}') WHERE EXISTS (SELECT 1 FROM pg_catalog.pg_replication_slots WHERE slot_name = '{slotName}');"
                );
            }
            catch (NpgsqlException)
            {
                // A slot the test never created, or one the database has already dropped with its
                // owning database, is not a cleanup failure.
            }
        }
    }

    private static void CreateConnectorRole(string roleName)
    {
        using NpgsqlConnection connection = new(Configuration.PostgresqlAdminConnectionString);
        connection.Open();
        ExecuteNonQuery(
            connection,
            $"""
            DO $role$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = '{roleName}') THEN
                    EXECUTE format('CREATE ROLE %I WITH LOGIN REPLICATION', '{roleName}');
                END IF;
            END
            $role$;
            """
        );
    }

    private void DropConnectorRoleIfExists()
    {
        if (_connectorRoleName is null)
        {
            return;
        }

        using NpgsqlConnection connection = new(Configuration.PostgresqlAdminConnectionString);
        connection.Open();
        ExecuteNonQuery(connection, $"DROP ROLE IF EXISTS \"{_connectorRoleName}\";");
    }

    private static void ExecuteNonQuery(NpgsqlConnection connection, string sql)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void AssumePostgresqlLogicalReplicationAvailable()
    {
        using NpgsqlConnection connection = new(Configuration.PostgresqlAdminConnectionString);
        connection.Open();

        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "SHOW wal_level;";
        string walLevel = (string?)command.ExecuteScalar() ?? string.Empty;

        if (!string.Equals(walLevel, "logical", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Ignore(
                $"CDC control readiness tests require wal_level=logical; observed wal_level={walLevel}."
            );
        }
    }

    /// <summary>
    /// The projector's report for this target, carrying the fingerprint of the database under test so
    /// the real correlation check decides on a real identity rather than on a constant.
    /// </summary>
    private sealed class StubProjectionCorrelationCollector(TimeProvider clock, Func<string> fingerprint)
        : ICdcProjectionCorrelationCollector
    {
        public Task<CoreCdc.CdcProjectionCorrelationObservation> CollectAsync(
            CdcObservationContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            DateTimeOffset observedAt = clock.GetUtcNow();
            CdcProjectionStatusReadResult reading = new(
                CdcProjectionStatusReadOutcome.Succeeded,
                new(
                    observedAt,
                    [
                        new CdcProjectionTargetReading(
                            DocumentCacheStatusTargetKey.FromTargetKey(
                                DocumentCacheTargetKey.Create(TenantKey, DataStoreId)
                            ),
                            observedAt,
                            RelationalProviderToken.PostgresqlValue,
                            fingerprint(),
                            DocumentCacheOperationalHealthStatus.Operational,
                            DocumentCacheStatusReason.None,
                            DocumentCacheCaughtUpStatus.CaughtUp,
                            DocumentCacheStatusReason.None,
                            DocumentCacheStatusQueuePresence.Empty,
                            []
                        ),
                    ]
                ),
                null
            );

            return Task.FromResult(
                CdcProjectionCorrelationObservationMapper.Map(context, reading, observedAt)
            );
        }
    }

    /// <summary>
    /// A conforming local broker's answers. The Kafka contract itself is exercised against a real
    /// authorizer-enabled broker by the broker-backed control suite; here it only has to be satisfied so
    /// the sequence proceeds to the provider-facing steps this suite is about.
    /// </summary>
    private sealed class StubKafkaAdmin(CdcControlOptions options, TimeProvider clock) : ICdcKafkaAdmin
    {
        public Task<CoreCdc.CdcConnectOffsetStorePolicyObservation> EnsureConnectOffsetStoreAsync(
            CdcObservationContext context,
            CancellationToken cancellationToken
        ) => Task.FromResult(SatisfiedOffsetStore(context));

        public Task<CoreCdc.CdcConnectOffsetStorePolicyObservation> DescribeConnectOffsetStoreAsync(
            CdcObservationContext context,
            CancellationToken cancellationToken
        ) => Task.FromResult(SatisfiedOffsetStore(context));

        private CoreCdc.CdcConnectOffsetStorePolicyObservation SatisfiedOffsetStore(
            CdcObservationContext context
        ) =>
            new(
                CoreCdc.CdcJsonContract.CurrentContractVersion,
                context.OperationId,
                clock.GetUtcNow(),
                context.TargetIdentity,
                context.TargetIdentity.Provider,
                context.PhysicalSourceFingerprint,
                options.ConnectWorkerKey,
                options.ConnectOffsetStorageTopic,
                CoreCdc.CdcConnectOffsetStorePolicyState.Satisfied,
                "compact",
                1,
                1,
                CoreCdc.CdcConnectOffsetStoreItemState.Satisfied,
                []
            );

        public Task<CdcKafkaBindingTopicPolicies> EnsureBindingTopicsAsync(
            CoreCdc.CdcArtifactInventory inventory,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("The sequence provisions Kafka through the policy pass.");

        public Task<CdcKafkaRecordSizeEvidence> VerifyRecordSizeAsync(
            CoreCdc.CdcArtifactInventory inventory,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("The sequence provisions Kafka through the policy pass.");

        public Task<CoreCdc.CdcKafkaPolicyObservation> EnsureBindingKafkaPolicyAsync(
            CdcObservationContext context,
            CoreCdc.CdcArtifactInventory inventory,
            CancellationToken cancellationToken
        ) => Task.FromResult(SatisfiedPolicy(context, inventory));

        public Task<CoreCdc.CdcKafkaPolicyObservation> DescribeBindingKafkaPolicyAsync(
            CdcObservationContext context,
            CoreCdc.CdcArtifactInventory inventory,
            CancellationToken cancellationToken
        ) => Task.FromResult(SatisfiedPolicy(context, inventory));

        public Task<CoreCdc.CdcSqlServerSchemaHistoryEvidence?> ReadSqlServerSchemaHistoryAsync(
            CoreCdc.CdcArtifactInventory inventory,
            CoreCdc.CdcSqlServerSchemaHistoryEnablementPhase enablementPhase,
            bool connectorCommittedStreamingOffset,
            CancellationToken cancellationToken
        ) => Task.FromResult<CoreCdc.CdcSqlServerSchemaHistoryEvidence?>(null);

        public Task<IReadOnlyList<CoreCdc.CdcGovernedArtifact>> DeleteBindingArtifactsAsync(
            CoreCdc.CdcArtifactInventory inventory,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<CoreCdc.CdcGovernedArtifact>>([
                new(
                    CoreCdc.CdcGovernedArtifactKind.PublicTopic,
                    inventory.TopicName,
                    CoreCdc.CdcCleanupState.Deleted,
                    "CDC public topic removed."
                ),
                new(
                    CoreCdc.CdcGovernedArtifactKind.PublicTopicAcls,
                    inventory.TopicName,
                    CoreCdc.CdcCleanupState.NotFound,
                    "No governed grant existed because the deployment has no authorizer."
                ),
                new(
                    CoreCdc.CdcGovernedArtifactKind.ProgressTopic,
                    inventory.ProgressTopicName,
                    CoreCdc.CdcCleanupState.Deleted,
                    "CDC progress topic removed."
                ),
                new(
                    CoreCdc.CdcGovernedArtifactKind.ProgressTopicAcls,
                    inventory.ProgressTopicName,
                    CoreCdc.CdcCleanupState.NotFound,
                    "No governed grant existed because the deployment has no authorizer."
                ),
            ]);

        private CoreCdc.CdcKafkaPolicyObservation SatisfiedPolicy(
            CdcObservationContext context,
            CoreCdc.CdcArtifactInventory inventory
        ) =>
            new(
                CoreCdc.CdcJsonContract.CurrentContractVersion,
                context.OperationId,
                clock.GetUtcNow(),
                context.TargetIdentity,
                context.TargetIdentity.Provider,
                context.PhysicalSourceFingerprint,
                CoreCdc.CdcKafkaPolicyState.Satisfied,
                options.DurabilityProfile,
                new(
                    inventory.TopicName,
                    CoreCdc.CdcKafkaPolicyItemState.Satisfied,
                    PartitionCount,
                    "compact",
                    1,
                    1
                ),
                new(
                    inventory.ProgressTopicName,
                    CoreCdc.CdcKafkaPolicyItemState.Satisfied,
                    1,
                    "compact",
                    1,
                    1
                ),
                null,
                new(inventory.TopicName, CoreCdc.CdcKafkaPolicyItemState.NotApplicable),
                new(inventory.ProgressTopicName, CoreCdc.CdcKafkaPolicyItemState.NotApplicable),
                null,
                new(CoreCdc.CdcKafkaPolicyItemState.Satisfied, MaxRecordBytes, MaxRecordBytes),
                []
            );
    }

    /// <summary>
    /// A conforming Connect worker. It echoes back the configuration it was registered with, so the
    /// live read-back validation runs against the configuration the real template rendered rather than
    /// against a second copy of the template rules, and it reports the instance database's own live WAL
    /// position as the connector's committed streaming offset.
    /// </summary>
    private sealed class StubConnectClient(Func<long> currentLsn) : ICdcConnectClient
    {
        private IReadOnlyDictionary<string, string> _registeredConfig = new Dictionary<string, string>(
            StringComparer.Ordinal
        );

        public Action? OnRegister { get; set; }

        public string? RegisteredConnectorName { get; private set; }

        public string? RestartedConnectorName { get; private set; }

        public string? ResumedConnectorName { get; private set; }

        public List<string> StoppedConnectorNames { get; } = [];

        public Task<CdcConnectResult<CdcConnectConfigValidation>> ValidateConnectorPluginConfigAsync(
            string connectorClass,
            IReadOnlyDictionary<string, string> config,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new CdcConnectResult<CdcConnectConfigValidation>(
                    CdcConnectOutcome.Succeeded,
                    new(0, []),
                    null
                )
            );

        public Task<CdcConnectResult> PutConnectorConfigAsync(
            string connectorName,
            IReadOnlyDictionary<string, string> config,
            CancellationToken cancellationToken
        )
        {
            OnRegister?.Invoke();
            RegisteredConnectorName = connectorName;
            _registeredConfig = new Dictionary<string, string>(config, StringComparer.Ordinal);

            return Task.FromResult(new CdcConnectResult(CdcConnectOutcome.Succeeded, null));
        }

        public Task<CdcConnectResult<IReadOnlyDictionary<string, string>>> GetConnectorConfigAsync(
            string connectorName,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new CdcConnectResult<IReadOnlyDictionary<string, string>>(
                    CdcConnectOutcome.Succeeded,
                    _registeredConfig,
                    null
                )
            );

        public Task<CdcConnectResult<CdcConnectorStatus>> GetConnectorStatusAsync(
            string connectorName,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new CdcConnectResult<CdcConnectorStatus>(
                    CdcConnectOutcome.Succeeded,
                    new("RUNNING", [new(0, "RUNNING", null)]),
                    null
                )
            );

        public Task<CdcConnectResult> RestartConnectorAsync(
            string connectorName,
            CancellationToken cancellationToken
        )
        {
            RestartedConnectorName = connectorName;

            return Task.FromResult(new CdcConnectResult(CdcConnectOutcome.Succeeded, null));
        }

        public Task<CdcConnectResult> ResumeConnectorAsync(
            string connectorName,
            CancellationToken cancellationToken
        )
        {
            ResumedConnectorName = connectorName;

            return Task.FromResult(new CdcConnectResult(CdcConnectOutcome.Succeeded, null));
        }

        public Task<CdcConnectResult> StopConnectorAsync(
            string connectorName,
            CancellationToken cancellationToken
        )
        {
            StoppedConnectorNames.Add(connectorName);

            return Task.FromResult(new CdcConnectResult(CdcConnectOutcome.Succeeded, null));
        }

        public Task<CdcConnectResult<CdcConnectorOffsets>> GetConnectorOffsetsAsync(
            string connectorName,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new CdcConnectResult<CdcConnectorOffsets>(
                    CdcConnectOutcome.Succeeded,
                    new([
                        new CdcConnectorOffsetEntry(
                            Json($$"""{"server":"{{connectorName}}"}"""),
                            Json(
                                $$"""{"lsn_proc":{{currentLsn().ToString(CultureInfo.InvariantCulture)}},"snapshot":false}"""
                            )
                        ),
                    ]),
                    null
                )
            );

        public Task<CdcConnectResult> DeleteConnectorOffsetsAsync(
            string connectorName,
            CancellationToken cancellationToken
        ) => Task.FromResult(new CdcConnectResult(CdcConnectOutcome.Succeeded, null));

        public Task<CdcConnectResult> DeleteConnectorAsync(
            string connectorName,
            CancellationToken cancellationToken
        ) => Task.FromResult(new CdcConnectResult(CdcConnectOutcome.Succeeded, null));

        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>A connector reporting no measurable source lag.</summary>
    private sealed class StubLagReader : ICdcConnectorLagReader
    {
        public Task<CdcConnectorLagReadResult> ReadAsync(
            CoreCdc.CdcProvider provider,
            string topicPrefix,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new CdcConnectorLagReadResult(CdcConnectorLagReadOutcome.Succeeded, new(0, 0, 0, 0), null)
            );
    }

    /// <summary>
    /// The guarded activation, which actually performs the lifecycle transition it reports so the
    /// sequence's own re-read of the instance database observes a tracking database.
    /// </summary>
    private sealed class StubGuardedActivation(Action activate)
        : IDocumentCacheGuardedNewEmptyActivationCommand
    {
        public bool Executed { get; private set; }

        public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
            DocumentCacheGuardedNewEmptyActivationRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Executed = true;
            activate();

            return Task.FromResult(
                new DocumentCacheAdministrativeCommandResult(
                    DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
                    new DocumentCacheAdministrativeTargetKey(TenantKey, DataStoreId),
                    DocumentCacheAdministrativeCommandStatus.Completed,
                    DocumentCacheAdministrativeCommandClassification.Succeeded,
                    mutated: true
                )
            );
        }
    }

    /// <summary>
    /// The control plane's clock: one instant, held a fixed interval ahead of the wall clock, shared by
    /// the controller, the real provider adapters, and the stubs.
    /// </summary>
    /// <remarks>
    /// The sequence stamps the instant an observation is validated against before it collects that
    /// observation, so a clock that moves at all reports its own evidence as arriving in the future. A
    /// single instant makes those comparisons equal. It has to be ahead of the wall clock as well,
    /// because some evidence is stamped by neither this clock nor the controller's — the eligibility
    /// read carries the instance database's own transaction time, and provider setup stamps its result
    /// itself — and every one of those must land at or before the instant it is checked against.
    /// </remarks>
    private sealed class SkewedTimeProvider(DateTimeOffset anchor) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => anchor;
    }
}
