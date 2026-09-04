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
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

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
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
[Category("CdcControlReadiness")]
public class Given_A_SqlServer_CdcControlReadinessSequence
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
    private const string SetupPrincipal = "sa";
    private const string ConnectorPassword = "EdFi_Dms1!";

    /// <summary>
    /// The event serial number a heartbeat after-image carries, which is the shape the source-position
    /// contract expects of a streaming SQL Server offset.
    /// </summary>
    private const long HeartbeatAfterImageEventSerialNo = CoreCdc
        .CdcSqlServerProviderPosition
        .HeartbeatAfterImageEventSerialNo;

    /// <summary>Stands in when the capture instances report no maximum LSN yet.</summary>
    private const string UnavailableLsn = "00000000:00000000:0000";

    private static readonly TimeSpan ControlPlaneClockSkew = TimeSpan.FromMinutes(1);

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private string _connectorPrincipalName = null!;
    private string _bindingStateRoot = null!;
    private StubProjectionCorrelationCollector _projection = null!;
    private StubKafkaAdmin _kafka = null!;
    private StubConnectClient _connect = null!;
    private StubGuardedActivation _activation = null!;
    private ServiceProvider _templateServices = null!;
    private SkewedTimeProvider _clock = null!;
    private string? _committedLsn;

    [SetUp]
    public async Task SetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "CDC control readiness tests require a MssqlAdmin connection string."
        );
        AssumeSqlServerAgentRunning();

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _connectorPrincipalName = $"cdc_connector_{Guid.NewGuid():N}";
        CreateConnectorLoginAndUser(_database.DatabaseName, _connectorPrincipalName);

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
            await _database.DisposeAsync();
        }

        DropConnectorLoginIfExists();

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
        CoreCdc.CdcArtifactInventory inventory = Inventory(BindingGeneration);

        await DriveEnablementThroughProviderStepsAsync(controller);

        using AssertionScope _ = new();
        _activation
            .Executed.Should()
            .BeTrue("the guarded activation runs before any capture artifact is created");
        (await GatingRoleExistsAsync(inventory.SqlServerCdcGatingRoleName!))
            .Should()
            .BeTrue("enablement creates the CDC gating role its binding record names");
        (await CaptureInstanceExistsAsync(inventory.SqlServerCaptureInstanceDocumentName!))
            .Should()
            .BeTrue("enablement creates the capture instances its binding record names");
        (await CaptureInstanceExistsAsync(inventory.SqlServerCaptureInstanceDocumentCacheName!))
            .Should()
            .BeTrue();
        (await CaptureInstanceExistsAsync(inventory.SqlServerCaptureInstanceCdcHeartbeatName!))
            .Should()
            .BeTrue();
        (await HeartbeatSingletonExistsAsync())
            .Should()
            .BeTrue("the heartbeat singleton is the provider-side evidence the connector advances");

        // The binding record is durable before any external artifact exists, so an interrupted
        // enablement always leaves something that names what was provisioned.
        BindingRecordPaths().Should().ContainSingle();
        _connect.RegisteredConnectorName.Should().Be(inventory.ConnectorName);
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
        (await GatingRoleExistsAsync(inventory.SqlServerCdcGatingRoleName!))
            .Should()
            .BeFalse();
        (await CaptureInstanceExistsAsync(inventory.SqlServerCaptureInstanceDocumentName!))
            .Should()
            .BeFalse();
        _connect.RegisteredConnectorName.Should().BeNull();
        _activation.Executed.Should().BeFalse("an ineligible database is never activated");
    }

    [Test]
    public async Task It_retires_the_binding_and_removes_the_provider_artifacts_it_created()
    {
        ICdcSetupController controller = BuildController(BindingGeneration);
        await DriveEnablementThroughProviderStepsAsync(controller);
        CoreCdc.CdcArtifactInventory inventory = Inventory(BindingGeneration);

        CoreCdc.CdcContractReadResult<CoreCdc.CdcCleanupProof> retirement = await controller.RetireAsync(
            TargetRequest(),
            Deadline()
        );

        using AssertionScope _ = new();
        retirement
            .Succeeded.Should()
            .BeTrue("retirement reported {0}", DescribeDiagnostics(retirement.Diagnostics));

        // Enablement made the connector principal a member of the gating role, and SQL Server refuses to
        // drop a role that still has members, so the role's absence is what proves the teardown empties
        // it rather than merely attempting the drop.
        (await GatingRoleExistsAsync(inventory.SqlServerCdcGatingRoleName!))
            .Should()
            .BeFalse();
        (await CaptureInstanceExistsAsync(inventory.SqlServerCaptureInstanceDocumentName!))
            .Should()
            .BeFalse();
        (await CaptureInstanceExistsAsync(inventory.SqlServerCaptureInstanceDocumentCacheName!))
            .Should()
            .BeFalse();
        (await CaptureInstanceExistsAsync(inventory.SqlServerCaptureInstanceCdcHeartbeatName!))
            .Should()
            .BeFalse();

        // The connector principal is the deployment's, not the binding's. Retirement releases its
        // membership and governs nothing else about it.
        (await ConnectorPrincipalExistsAsync(_connectorPrincipalName))
            .Should()
            .BeTrue("the connector principal outlives the generation that granted it access");

        // The record is deleted last and only against a validated cleanup proof, so its absence is the
        // evidence that every governed artifact ahead of it was removed first.
        BindingRecordPaths().Should().BeEmpty();
    }

    [Test]
    public async Task It_leaves_both_generations_intact_when_a_source_replacement_is_refused()
    {
        ICdcSetupController controller = BuildController(BindingGeneration);
        await DriveEnablementThroughProviderStepsAsync(controller);

        ICdcSetupController replacement = BuildController(ReplacementGeneration);
        await DriveReplacementThroughProviderStepsAsync(replacement);

        CoreCdc.CdcArtifactInventory previous = Inventory(BindingGeneration);
        CoreCdc.CdcArtifactInventory replaced = Inventory(ReplacementGeneration);

        using AssertionScope _ = new();

        // A replacement of a target whose enablement never reached write admission is refused, and a
        // refused replacement is a no-op on both generations: the outgoing generation keeps every
        // artifact it owns, and the incoming one acquires none.
        replaced
            .SqlServerCdcGatingRoleName.Should()
            .NotBe(
                previous.SqlServerCdcGatingRoleName,
                "a new generation never reuses a prior generation's artifact names"
            );
        replaced
            .SqlServerCaptureInstanceDocumentName.Should()
            .NotBe(previous.SqlServerCaptureInstanceDocumentName);
        (await GatingRoleExistsAsync(replaced.SqlServerCdcGatingRoleName!)).Should().BeFalse();
        (await CaptureInstanceExistsAsync(replaced.SqlServerCaptureInstanceDocumentName!)).Should().BeFalse();
        (await GatingRoleExistsAsync(previous.SqlServerCdcGatingRoleName!))
            .Should()
            .BeTrue("the outgoing generation is retained until it is explicitly retired");
        (await CaptureInstanceExistsAsync(previous.SqlServerCaptureInstanceDocumentName!)).Should().BeTrue();
    }

    /// <summary>
    /// Runs the enablement sequence far enough to provision everything this database owns, and accepts
    /// that it does not finish.
    /// </summary>
    /// <remarks>
    /// The steps after the provider barrier cannot settle against a stubbed Kafka Connect worker. SQL
    /// Server's retained range end is <c>sys.fn_cdc_get_max_lsn()</c>, which the capture Agent job keeps
    /// advancing on its own: an offset captured once falls behind a barrier taken after the job moved,
    /// and an offset re-read on each pass moves under the steps that already consumed it. Only a
    /// connector that is really streaming reports a position consistent with both. Everything up to and
    /// including provider setup and connector registration does settle, and that is what is asserted.
    /// </remarks>
    private async Task DriveEnablementThroughProviderStepsAsync(ICdcSetupController controller)
    {
        try
        {
            await controller.EnableAsync(EnableRequest(), ProviderStepsDeadline());
        }
        catch (OperationCanceledException)
        {
            // Expected: the sequence is driven for its provider-side effects, not to completion.
        }
    }

    private async Task DriveReplacementThroughProviderStepsAsync(ICdcSetupController controller)
    {
        try
        {
            await controller.ReplaceSourceAsync(
                new CdcReplaceSourceRequest(
                    OperationId,
                    TenantKey,
                    DataStoreId,
                    _database.ConnectionString,
                    BindingGeneration,
                    ProvisioningEvidence(),
                    ProviderSetupInputs()
                ),
                ProviderStepsDeadline()
            );
        }
        catch (OperationCanceledException)
        {
            // Expected, for the same reason an enablement does not finish here.
        }
    }

    private ICdcSetupController BuildController(long generation)
    {
        CdcControlOptions controlOptions = ControlOptions(generation);
        _projection = new StubProjectionCorrelationCollector(
            _clock,
            () => ReadSourceFingerprintAsync().GetAwaiter().GetResult()
        );
        _kafka = new StubKafkaAdmin(controlOptions, _clock);
        _connect = new StubConnectClient(
            _database.DatabaseName,
            () => ReadCommittedLsnAsync().GetAwaiter().GetResult()
        );
        _activation = new StubGuardedActivation(() => SetLifecycleTrackingAsync().GetAwaiter().GetResult());

        return new CdcSetupController(
            Options.Create(controlOptions),
            new CdcExplicitProjectionTargetProof(TargetConfiguration()),
            _projection,
            new CdcEligibilityProbe(
                CoreCdc.CdcProvider.SqlServer,
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
            new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]),
            new CdcInstanceDatabaseConnectionFactory(),
            _kafka,
            _templateServices.GetRequiredService<ICdcConnectorTemplateService>(),
            _connect,
            new CdcConnectorObservationMapper(
                _templateServices.GetRequiredService<ICdcConnectorTemplateService>(),
                _clock
            ),
            new StubLagReader(),
            new MssqlCdcSourcePositionAdapter(
                new MssqlDocumentCacheProviderCommandTimeoutClassifier(),
                _clock,
                NullLogger<MssqlCdcSourcePositionAdapter>.Instance
            ),
            new CdcProviderArtifactTeardown(
                CoreCdc.CdcProvider.SqlServer,
                NullLogger<CdcProviderArtifactTeardown>.Instance
            ),
            _clock,
            NullLogger<CdcSetupController>.Instance
        );
    }

    private CdcControlOptions ControlOptions(long generation)
    {
        SqlConnectionStringBuilder connection = new(_database.ConnectionString);

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
            SqlServerPollInterval = TimeSpan.FromSeconds(2),
            AclsEnabled = false,
            SetupPrincipal = SetupPrincipal,
            ProviderConnectionProperties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["database.hostname"] = connection.DataSource,
                ["database.port"] = "1433",
                ["database.user"] = _connectorPrincipalName,
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.names"] = _database.DatabaseName,
                ["driver.encrypt"] = "true",
                ["driver.trustServerCertificate"] = "true",
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
            _connectorPrincipalName,
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
                    CoreCdc.CdcProvider.SqlServer
                )
            )
            .Inventory!;

    /// <summary>
    /// A wall-clock bound on each operation. The control plane's own clock is a single instant, so it
    /// cannot expire a step's budget; this keeps a step that never produces its evidence from hanging
    /// the suite instead.
    /// </summary>
    /// <summary>A wall-clock bound on an operation that is expected to complete.</summary>
    private static CancellationToken Deadline() => new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token;

    /// <summary>
    /// A wall-clock bound on an enablement driven only for its provider-side effects. It has to outlast
    /// SQL Server's CDC enablement, which is slow, without leaving the suite waiting on a sequence that
    /// cannot finish here.
    /// </summary>
    private static CancellationToken ProviderStepsDeadline() =>
        new CancellationTokenSource(TimeSpan.FromSeconds(90)).Token;

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
            """
            SELECT LOWER(CONVERT(varchar(36), [SourceIdentity]))
            FROM [dms].[DataStoreIdentity]
            WHERE [DataStoreIdentitySingletonId] = 1;
            """
        );

        return CdcSourceFingerprintMetadata.Compute(CdcProvider.SqlServer, sourceIdentity).Value;
    }

    /// <summary>
    /// The position a streaming connector would have committed: the capture instances' current maximum
    /// LSN, formatted as the triplet Debezium reports.
    /// </summary>
    /// <remarks>
    /// Unlike a PostgreSQL replication slot, SQL Server's retained range is bounded by the capture
    /// instances' own minimum and maximum LSNs rather than by a position the consumer moves, so the
    /// maximum satisfies the provider barrier and sits inside the retained range at the same time. It is
    /// captured once and held, so the offset reported to an earlier step stays valid for every check
    /// that follows it. Re-reading it on each pass moves the offset under the steps that already
    /// consumed it, and the sequence then never settles.
    /// </remarks>
    private async Task<string> ReadCommittedLsnAsync()
    {
        _committedLsn ??= await _database.ExecuteScalarOrDefaultAsync<string>(
            """
            DECLARE @lsn binary(10) = sys.fn_cdc_get_max_lsn();
            DECLARE @hex varchar(30) = CONVERT(varchar(30), @lsn, 2);
            SELECT LOWER(
                SUBSTRING(@hex, 1, 8) + ':' + SUBSTRING(@hex, 9, 8) + ':' + SUBSTRING(@hex, 17, 4)
            );
            """
        );

        return _committedLsn ?? UnavailableLsn;
    }

    /// <summary>
    /// What the guarded activation leaves behind: the instance database tracking, which the sequence's
    /// own re-read of the real database then observes.
    /// </summary>
    private Task SetLifecycleTrackingAsync() =>
        _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[DocumentCacheState]
            SET [ProjectionLifecycleState] = @lifecycle
            WHERE [StateId] = 1;
            """,
            new SqlParameter("lifecycle", DocumentCacheLifecycleState.Tracking.ToString())
        );

    private Task InsertCanonicalDocumentAsync() =>
        _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [dms].[Document]
                ([DocumentUuid], [ResourceKeyId], [ContentVersion], [ContentLastModifiedAt], [CreatedAt])
            VALUES (NEWID(), 1, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            """
        );

    private async Task<bool> GatingRoleExistsAsync(string roleName) =>
        await _database.ExecuteScalarAsync<int>(
            "SELECT CASE WHEN DATABASE_PRINCIPAL_ID(@roleName) IS NULL THEN 0 ELSE 1 END;",
            new SqlParameter("roleName", roleName)
        ) == 1;

    /// <summary>
    /// Whether the named capture instance exists. The <c>cdc</c> catalog views are created by enabling
    /// CDC on the database, so their own absence is the answer for a database CDC was never enabled on.
    /// </summary>
    private async Task<bool> CaptureInstanceExistsAsync(string captureInstanceName) =>
        await _database.ExecuteScalarAsync<int>(
            """
            IF OBJECT_ID('cdc.change_tables', 'U') IS NULL
                SELECT 0;
            ELSE
                SELECT COUNT(*) FROM cdc.change_tables WHERE capture_instance = @captureInstance;
            """,
            new SqlParameter("captureInstance", captureInstanceName)
        ) > 0;

    private async Task<bool> ConnectorPrincipalExistsAsync(string principalName) =>
        await _database.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM sys.database_principals
            WHERE name = @principalName AND type IN ('S', 'U');
            """,
            new SqlParameter("principalName", principalName)
        ) > 0;

    private async Task<bool> HeartbeatSingletonExistsAsync() =>
        await _database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM [dms].[CdcHeartbeat] WHERE [HeartbeatId] = 1;"
        ) > 0;

    /// <summary>
    /// SQL Server CDC is captured by an Agent job, so without a running Agent the capture instances
    /// report no maximum LSN, the provider barrier is never reached, and every step that depends on a
    /// committed offset stalls rather than failing on its own terms.
    /// </summary>
    private static void AssumeSqlServerAgentRunning()
    {
        using SqlConnection connection = new(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();
        using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP 1 status_desc
            FROM sys.dm_server_services
            WHERE servicename LIKE '%Agent%';
            """;
        string status = (string?)command.ExecuteScalar() ?? "unknown";

        if (!status.StartsWith("Running", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Ignore(
                $"CDC control readiness tests require a running SQL Server Agent; observed '{status}'."
            );
        }
    }

    private static void CreateConnectorLoginAndUser(string databaseName, string principalName)
    {
        using SqlConnection connection = new(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();
        using SqlCommand command = connection.CreateCommand();
        command.CommandText = $"""
            IF SUSER_ID(N'{principalName}') IS NULL
            BEGIN
                CREATE LOGIN [{principalName}] WITH PASSWORD = '{ConnectorPassword}', CHECK_POLICY = OFF;
            END;

            USE [{databaseName}];

            IF USER_ID(N'{principalName}') IS NULL
            BEGIN
                CREATE USER [{principalName}] FOR LOGIN [{principalName}];
            END;
            """;
        command.ExecuteNonQuery();
    }

    private void DropConnectorLoginIfExists()
    {
        if (string.IsNullOrWhiteSpace(_connectorPrincipalName))
        {
            return;
        }

        SqlConnection.ClearAllPools();

        using SqlConnection connection = new(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();
        using SqlCommand command = connection.CreateCommand();
        command.CommandText = $"DROP LOGIN [{_connectorPrincipalName}];";

        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqlException)
        {
            // A login the test never created, or one already removed with its database, is not a
            // cleanup failure.
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
                            RelationalProviderToken.SqlServerValue,
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

        public Task<CdcKafkaRecordSizeEvidence> VerifyRecordSizeAsync(
            CoreCdc.CdcArtifactInventory inventory,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("The sequence provisions Kafka through the policy pass.");

        public Task<CoreCdc.CdcKafkaPolicyObservation> EnsureBindingKafkaPolicyAsync(
            CdcObservationContext context,
            CoreCdc.CdcArtifactInventory inventory,
            int bindingPartitionCount,
            CancellationToken cancellationToken
        ) => Task.FromResult(SatisfiedPolicy(context, inventory));

        public Task<CoreCdc.CdcKafkaPolicyObservation> DescribeBindingKafkaPolicyAsync(
            CdcObservationContext context,
            CoreCdc.CdcArtifactInventory inventory,
            int bindingPartitionCount,
            CancellationToken cancellationToken
        ) => Task.FromResult(SatisfiedPolicy(context, inventory));

        /// <summary>
        /// The sequence under test enables a target nothing has provisioned before, so the broker
        /// holds none of its governed topics.
        /// </summary>
        public Task<CdcKafkaGovernedTopicPresence> FindExistingGovernedTopicsAsync(
            CoreCdc.CdcArtifactInventory inventory,
            CancellationToken cancellationToken
        ) => Task.FromResult(new CdcKafkaGovernedTopicPresence(true, []));

        /// <summary>
        /// The sequence under test enables and then reads back a target that publishes: the connector
        /// the readiness steps observe has committed, so the stream is established.
        /// </summary>
        public Task<CoreCdc.CdcPublicTopicPublicationEvidence> ReadPublicTopicPublicationAsync(
            CoreCdc.CdcArtifactInventory inventory,
            CancellationToken cancellationToken
        ) => Task.FromResult(new CoreCdc.CdcPublicTopicPublicationEvidence(true, true));

        public Task<CoreCdc.CdcSqlServerSchemaHistoryEvidence?> ReadSqlServerSchemaHistoryAsync(
            CoreCdc.CdcArtifactInventory inventory,
            CoreCdc.CdcSqlServerSchemaHistoryEnablementPhase enablementPhase,
            bool connectorCommittedStreamingOffset,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<CoreCdc.CdcSqlServerSchemaHistoryEvidence?>(
                new(enablementPhase, CoreCdc.CdcSqlServerSchemaHistoryState.Valid)
            );

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
                new(
                    CoreCdc.CdcGovernedArtifactKind.SchemaHistoryTopic,
                    inventory.SchemaHistoryTopicName!,
                    CoreCdc.CdcCleanupState.Deleted,
                    "CDC schema history topic removed."
                ),
                new(
                    CoreCdc.CdcGovernedArtifactKind.SchemaHistoryTopicAcls,
                    inventory.SchemaHistoryTopicName!,
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
                new(
                    inventory.SchemaHistoryTopicName!,
                    CoreCdc.CdcKafkaPolicyItemState.Satisfied,
                    1,
                    "delete",
                    1,
                    1
                ),
                new(inventory.TopicName, CoreCdc.CdcKafkaPolicyItemState.NotApplicable),
                new(inventory.ProgressTopicName, CoreCdc.CdcKafkaPolicyItemState.NotApplicable),
                new(inventory.SchemaHistoryTopicName!, CoreCdc.CdcKafkaPolicyItemState.NotApplicable),
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
    private sealed class StubConnectClient(string catalogName, Func<string> currentLsn) : ICdcConnectClient
    {
        private IReadOnlyDictionary<string, string>? _registeredConfig;

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
            RegisteredConnectorName = connectorName;
            _registeredConfig = new Dictionary<string, string>(config, StringComparer.Ordinal);

            return Task.FromResult(new CdcConnectResult(CdcConnectOutcome.Succeeded, null));
        }

        /// <summary>
        /// The worker's own answer, which is <c>NotFound</c> until something registers a connector
        /// under the name.
        /// </summary>
        /// <remarks>
        /// Modelled rather than always answering successfully, because an unbound enablement asks this
        /// to establish that it is the first to provision the name and treats every answer but the
        /// worker's 404 as an artifact that already exists. A stub that reported a connector before
        /// any registration refused the enablement at that guard, so nothing downstream in this
        /// sequence ran at all.
        /// </remarks>
        public Task<CdcConnectResult<IReadOnlyDictionary<string, string>>> GetConnectorConfigAsync(
            string connectorName,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                _registeredConfig is { } registeredConfig
                    ? new CdcConnectResult<IReadOnlyDictionary<string, string>>(
                        CdcConnectOutcome.Succeeded,
                        registeredConfig,
                        null
                    )
                    : new CdcConnectResult<IReadOnlyDictionary<string, string>>(
                        CdcConnectOutcome.NotFound,
                        null,
                        new(404, "the worker holds no connector under this name", false)
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
                            Json($$"""{"database":"{{catalogName}}","server":"{{connectorName}}"}"""),
                            Json(
                                $$"""{"commit_lsn":"{{currentLsn()}}","change_lsn":"{{currentLsn()}}","event_serial_no":{{HeartbeatAfterImageEventSerialNo}},"snapshot":false}"""
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
