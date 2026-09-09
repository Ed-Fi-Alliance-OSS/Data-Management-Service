// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Confluent.Kafka;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.DocumentCacheRuntime;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.SchemaTools.Provisioning;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

/// <summary>Owns an actual new database; only schema-file and CMS delivery are fixture substitutes.</summary>
internal sealed class CdcProviderAdmissionFixture : IAsyncDisposable
{
    public CdcControllerFixture Infrastructure { get; private set; } = null!;
    public CdcControllerFixtureHooks Hooks => Infrastructure.Hooks;
    public CdcDeploymentRequest Request { get; private set; } = null!;
    public ICdcProjectionRuntime Runtime { get; private set; } = null!;
    public CdcControllerFixtureControllers Controllers { get; private set; } = null!;
    public CdcKafkaAdminAdapter Kafka { get; private set; } = null!;
    public ICdcProviderSetupService Provider { get; private set; } = null!;
    public ICdcConnectorTemplateService Templates { get; private set; } = null!;
    public List<CdcProviderSetupMode> ProviderModes { get; } = [];
    public List<CdcProviderSetupResult> ProviderResults { get; } = [];
    public List<CoreCdc.CdcConnectorLagObservation> Lag { get; } = [];
    public Action BeforeMetricsCall { get; set; } = () => { };
    public delegate CdcTransportResult<CdcConnectorTelemetryObservation> MetricsTransform(
        CdcTransportResult<CdcConnectorTelemetryObservation> result
    );
    public MetricsTransform TransformMetrics { get; set; } = result => result;
    public DateTimeOffset RuntimeStoppedAt { get; private set; }
    public List<CdcKafkaTopicEvidence> PreRegistrationHistoryTopics { get; } = [];
    public List<CdcDeploymentDiagnostic> RegistrationRetries { get; } = [];
    public List<CoreCdc.CdcProviderBarrierCaptureResult> Barriers { get; } = [];
    public List<CoreCdc.CdcProviderBarrierObservation> BarrierObservations { get; } = [];
    public List<CoreCdc.CdcSourceHistoryObservation> SourceHistory { get; } = [];
    public List<ContinuitySample> ContinuitySamples { get; } = [];
    public List<DocumentCacheStatusResponse> ProjectionObservations { get; } = [];
    public Action<string> BeforeRuntimeCall { get; set; } = _ => { };
    public Action<string> AfterRuntimeCall { get; set; } = _ => { };
    public string ConnectionString { get; private set; } = null!;
    public string Database { get; } = "admission_" + Guid.NewGuid().ToString("N");
    private ServiceProvider _services = null!;
    private bool _disposed;
    private DbConnection _providerConnection = null!;
    private IConfigurationRoot _configuration = null!;
    private ApiSchemaFileLoadResult.SuccessResult _schema = null!;
    private DdlPipelineEmission _emission = null!;
    private readonly CdcProvider _provider;
    private EffectiveSchemaInfo _effectiveSchema = null!;
    public List<string> Preparation { get; } = [];
    private readonly ConcurrentQueue<ValidationFailure> _validationFailures = new();
    private readonly ConcurrentQueue<DatabaseFailure> _databaseFailures = new();
    private readonly EventHandler<FirstChanceExceptionEventArgs> _observeValidationFailure;

    private CdcProviderAdmissionFixture(CdcProvider provider)
    {
        _provider = provider;
        _observeValidationFailure = (sender, args) =>
        {
            if (args.Exception is not (CdcWorkflowStateException or DbException))
            {
                return;
            }
            // Transport results deliberately redact assertion details. Retain bounded source locations
            // for qualification diagnosis, never exception messages, arguments, paths or locals.
            string[] locations = new StackTrace(true)
                .GetFrames()
                .Where(frame => frame.GetMethod()?.DeclaringType != typeof(CdcProviderAdmissionFixture))
                .Where(frame =>
                    frame
                        .GetMethod()
                        ?.DeclaringType?.Namespace?.StartsWith(
                            "EdFi.DataManagementService.",
                            StringComparison.Ordinal
                        ) == true
                )
                .Take(8)
                .Select(frame =>
                    $"{frame.GetMethod()!.DeclaringType!.FullName}.{frame.GetMethod()!.Name}:{frame.GetFileLineNumber()}"
                )
                .ToArray();
            if (args.Exception is CdcWorkflowStateException failure)
            {
                _validationFailures.Enqueue(new(DateTimeOffset.UtcNow, failure.Failure, locations));
                while (_validationFailures.Count > 128)
                {
                    _validationFailures.TryDequeue(out _);
                }
            }
            if (args.Exception is DbException database)
            {
                _databaseFailures.Enqueue(
                    new(
                        DateTimeOffset.UtcNow,
                        database.SqlState ?? string.Empty,
                        database is SqlException sql ? sql.Number : database.ErrorCode,
                        database.IsTransient,
                        locations
                    )
                );
                while (_databaseFailures.Count > 128)
                {
                    _databaseFailures.TryDequeue(out _);
                }
            }
        };
        AppDomain.CurrentDomain.FirstChanceException += _observeValidationFailure;
    }

    private sealed record ValidationFailure(
        DateTimeOffset ObservedAt,
        CdcWorkflowStateFailure Failure,
        string[] Locations
    );

    private sealed record DatabaseFailure(
        DateTimeOffset ObservedAt,
        string SqlState,
        int ErrorCode,
        bool IsTransient,
        string[] Locations
    );

    private CoreCdc.CdcProvider CoreProvider =>
        _provider == CdcProvider.Postgresql ? CoreCdc.CdcProvider.Postgresql : CoreCdc.CdcProvider.SqlServer;
    private string ProviderToken => _provider == CdcProvider.Postgresql ? "postgresql" : "mssql";
    private static readonly DocumentCacheTargetKey Target = DocumentCacheTargetKey.Create("", 1);

    public static async Task<CdcProviderAdmissionFixture> StartAsync(
        CdcProvider provider,
        CancellationToken cancellationToken,
        bool composeKafka = false
    )
    {
        var suite = new CdcProviderAdmissionFixture(provider);
        try
        {
            suite.Infrastructure = await CdcControllerFixture.StartAsync(
                provider,
                cancellationToken,
                async (infrastructure, ct) =>
                {
                    suite.Infrastructure = infrastructure;
                    try
                    {
                        await suite.PrepareAsync(ct);
                    }
                    catch (AssertionException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        Assert.Fail(
                            $"Owned admission preparation failed ({exception.GetType().Name}). Stack: {exception.StackTrace}"
                        );
                    }
                },
                nativeKafka: true,
                composeKafka: composeKafka
            );
            return suite;
        }
        catch
        {
            await suite.DisposeAsync();
            throw;
        }
    }

    private async Task PrepareAsync(CancellationToken cancellationToken)
    {
        await using var admin = await Infrastructure.OpenAdminConnectionAsync(cancellationToken);
        ConnectionString =
            _provider == CdcProvider.Postgresql
                ? new NpgsqlConnectionStringBuilder(admin.ConnectionString)
                {
                    Database = Database,
                    Pooling = false,
                    Password = CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword,
                }.ConnectionString
                : new SqlConnectionStringBuilder(admin.ConnectionString)
                {
                    InitialCatalog = Database,
                    Pooling = false,
                    Password = CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword,
                }.ConnectionString;
        if (_provider == CdcProvider.SqlServer)
        {
            await using var command = admin.CreateCommand();
            command.CommandText = "EXEC sys.sp_configure N'nested triggers', 0; RECONFIGURE;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            Preparation.Add("nested-triggers-disabled");
        }
        var loader = new ApiSchemaFileLoader(
            new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance),
            NullLogger<ApiSchemaFileLoader>.Instance
        );
        _schema = (ApiSchemaFileLoadResult.SuccessResult)
            loader.Load(
                Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "minimal-api-schema.json"),
                []
            );
        var schemaSet = new EffectiveSchemaSetBuilder(
            new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance),
            new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance)
        ).Build(_schema.NormalizedNodes);
        var emission = DdlPipelineHelpers.BuildDdlEmissionForDialect(
            schemaSet,
            _provider == CdcProvider.Postgresql ? SqlDialect.Pgsql : SqlDialect.Mssql
        );
        _emission = emission;
        _effectiveSchema = schemaSet.EffectiveSchema;
        var provisioner = CreateManagedSource(Database);
        var provisioned = await new CdcManagedDatabaseProvisioning(
            Infrastructure.CreateJournalStore()
        ).ProvisionAsync(
            new("dms", "default", "1", "admission", 1, CoreProvider),
            provisioner,
            cancellationToken,
            purpose: CdcWorkflowPurpose.InitialCdcProvisioning
        );
        provisioned.CreationReceipt.Outcome.Should().Be(CdcDatabaseCreationOutcome.Created);
        await ExecuteAsync(
            _provider == CdcProvider.Postgresql
                ? "CREATE ROLE dms_connector LOGIN REPLICATION PASSWORD 'EdFi_Dms1!'"
                : "CREATE LOGIN dms_connector WITH PASSWORD = 'EdFi_Dms1!', CHECK_POLICY = OFF; CREATE USER dms_connector FOR LOGIN dms_connector;",
            cancellationToken
        );
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppSettings:Datastore"] = ProviderToken,
                    ["AppSettings:DefaultPartitionCount"] = "10",
                    ["ConfigurationServiceSettings:BaseUrl"] = "https://cms.example.org",
                    ["ConfigurationServiceSettings:ClientId"] = "test",
                    ["ConfigurationServiceSettings:ClientSecret"] = "test",
                    ["ConfigurationServiceSettings:Scope"] = "test",
                    ["ConfigurationServiceSettings:EncryptionKey"] =
                        "TestEncryptionKey123456789012345678901234567890",
                    ["DataManagement:DocumentCache:Targets:0:TenantKey"] = "",
                    ["DataManagement:DocumentCache:Targets:0:DataStoreId"] = "1",
                }
            )
            .Build();
        var artifacts = CoreCdc
            .CdcArtifactNameGenerator.Render(new("dms", "edfi.documents", "admission", 1, CoreProvider))
            .Inventory!;
        var binding = new CoreCdc.CdcBinding(
            1,
            "dms",
            "default",
            "1",
            "admission",
            1,
            CoreProvider,
            provisioned.PhysicalSourceFingerprint,
            artifacts.ConnectorName,
            artifacts.TopicName,
            1,
            CoreCdc.CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
            1
        );
        var providerCallTimeout =
            _provider == CdcProvider.SqlServer ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(30);
        // Full emitted-schema permission inspection can exceed SqlClient's 30-second default.
        // Align the setup command budget with the bounded controller call that owns it.
        string setupConnection =
            _provider == CdcProvider.SqlServer
                ? new SqlConnectionStringBuilder(ConnectionString)
                {
                    CommandTimeout = (int)providerCallTimeout.TotalSeconds,
                }.ConnectionString
                : ConnectionString;
        _providerConnection = CreateConnection(setupConnection);
        await _providerConnection.OpenAsync(cancellationToken);
        var connectionProperties = new Dictionary<string, string>(
            Infrastructure.Resources.ProviderConnectionProperties
        )
        {
            [_provider == CdcProvider.Postgresql ? "database.dbname" : "database.names"] = Database,
        };
        Request = new(
            binding,
            _configuration,
            new(
                _provider,
                CdcProviderSetupMode.InitialCreateOrExactMatch,
                new(CdcSourceFingerprintMetadata.Version, provisioned.PhysicalSourceFingerprint),
                new(new(_provider == CdcProvider.Postgresql ? "postgres" : "sa")),
                new(new("dms_connector")),
                CdcDeploymentRequest.GetProviderArtifactNames(binding),
                new(false),
                emission.CdcSourceInventory,
                emission.CdcDmsManagedTableInventory,
                databaseExecutor: new DbConnectionCdcProviderDatabaseExecutor(_providerConnection)
            ),
            Infrastructure.ConnectEndpoint,
            await Infrastructure.MetricsEndpointAsync(cancellationToken),
            new(
                Infrastructure.Resources.KafkaBootstrapServers,
                1_000_000,
                heartbeatInterval: TimeSpan.FromSeconds(1),
                sqlServerPollInterval: _provider == CdcProvider.SqlServer ? TimeSpan.FromSeconds(1) : null
            ),
            Tests.Unit.CdcDeploymentRequestTestData.Worker(
                heapBytes: 1_073_741_824,
                digest: CdcQualifiedWorkerImage.Digest,
                offsetTopic: Infrastructure.Resources.ControllerProject + ".connect.offsets",
                workerKey: Infrastructure.Resources.ControllerProject
            ),
            new(_provider, connectionProperties),
            CdcKafkaClientSecurityProperties.Empty,
            new(
                providerCallTimeout,
                _provider == CdcProvider.SqlServer ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(3),
                TimeSpan.FromMilliseconds(250),
                // Provider/worker read-back and offline runtime shutdown are part of this window.
                // Expiry-specific cases override it with their own shorter, asserted deadline.
                TimeSpan.FromMinutes(1)
            )
        );
        var registrations = new ServiceCollection()
            .AddLogging()
            .AddSingleton<Serilog.ILogger>(_ => new Serilog.LoggerConfiguration().CreateLogger())
            .AddCdcProviderSetup()
            .AddCdcConnectorTemplates();
        if (_provider == CdcProvider.Postgresql)
        {
            registrations.AddPostgresqlDmsCdcControlPlane();
        }
        else
        {
            registrations.AddMssqlDmsCdcControlPlane();
        }
        _services = registrations.BuildServiceProvider();
        Provider = new RecordingProvider(
            _services.GetRequiredService<ICdcProviderSetupService>(),
            ProviderModes,
            ProviderResults
        );
        Templates = _services.GetRequiredService<ICdcConnectorTemplateService>();
        var kafkaConfig = new AdminClientConfig
        {
            BootstrapServers = Infrastructure.Resources.ControllerKafkaBootstrapServers,
        };
        Kafka = Observed(
            CdcKafkaAdminAdapter.Create(
                kafkaConfig,
                new CdcComposeKafkaAuthorizationInspection(
                    Infrastructure.Resources.ControllerProject,
                    kafkaConfig.BootstrapServers
                )
            )
        );
        Controllers = CreateControllers();
        await ReopenRuntimeAsync(cancellationToken);
        // This is the same controller operation used by worker startup, before Docker launches it.
        var offset = Observed(
            await KafkaProvisioning().ProvisionOffsetStoreAsync(Request, cancellationToken)
        );
        offset.PolicyState.Should().Be(CoreCdc.CdcConnectOffsetStorePolicyState.Satisfied);
    }

    public CdcControllerFixtureControllers CreateControllers() =>
        new(
            Infrastructure,
            Provider,
            Templates,
            Kafka,
            new RecordingPositions(
                _services
                    .GetServices<CoreCdc.ICdcProviderSourcePositionAdapter>()
                    .Single(p => p.Provider == Request.Binding.Provider),
                this
            ),
            new RecordingMetrics(Infrastructure.Metrics, this)
        );

    public ICdcManagedDatabaseProvisioner CreateManagedSource(
        string database,
        CdcProjectionPrerequisiteMode mode = CdcProjectionPrerequisiteMode.OwnedLocalSqlServer,
        string setupUser = "sa"
    )
    {
        if (_provider == CdcProvider.Postgresql)
        {
            return new ManagedSource(
                new NpgsqlConnectionStringBuilder(ConnectionString)
                {
                    Database = "postgres",
                }.ConnectionString,
                new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString,
                database,
                _emission
            );
        }
        string connection = new SqlConnectionStringBuilder(ConnectionString)
        {
            UserID = setupUser,
            InitialCatalog = database,
        }.ConnectionString;
        return new ManagedDatabaseProvisioner(
            new RecordingMssqlProvisioner(Preparation),
            new MssqlDocumentCachePhysicalSourceFingerprintReader(
                NullLogger<MssqlDocumentCachePhysicalSourceFingerprintReader>.Instance
            ),
            connection,
            _effectiveSchema,
            _emission.CombinedSql,
            60,
            mode
        );
    }

    public async Task SelectDifferentSourceAsync(string database, CancellationToken token)
    {
        var source = CreateManagedSource(database);
        source.CreateDatabase().Should().BeTrue();
        source.ProvisionSchema(true);
        ConnectionString =
            _provider == CdcProvider.Postgresql
                ? new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString
                : new SqlConnectionStringBuilder(ConnectionString)
                {
                    InitialCatalog = database,
                }.ConnectionString;
        // CMS delivery changes to an independent emitted-schema database. The existing binding,
        // provider executor, connector settings and original source remain untouched.
        await ReopenRuntimeAsync(token);
    }

    private sealed class RecordingMssqlProvisioner(List<string> preparation)
        : MssqlDatabaseProvisioner(NullLogger.Instance)
    {
        public override void CheckOrConfigureMvcc(string connectionString, bool databaseWasCreated)
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()";
            if (databaseWasCreated)
            {
                Convert.ToBoolean(command.ExecuteScalar()).Should().BeFalse();
            }
            connection.Close();
            base.CheckOrConfigureMvcc(connectionString, databaseWasCreated);
            preparation.Add("mvcc-prepared");
        }

        public override void CheckCdcProjectionPrerequisites(
            string connectionString,
            bool configureOwnedLocalServer
        )
        {
            base.CheckCdcProjectionPrerequisites(connectionString, configureOwnedLocalServer);
            preparation.Add("projection-prerequisites-validated");
        }
    }

    private DbConnection CreateConnection(string connectionString) =>
        _provider == CdcProvider.Postgresql
            ? new NpgsqlConnection(connectionString)
            : new SqlConnection(connectionString);

    public CdcKafkaProvisioning KafkaProvisioning() =>
        new(
            Infrastructure.StateRoot,
            Kafka,
            Runtime,
            new CdcKafkaProducerInspection(Infrastructure.Connect, Infrastructure.Worker)
        );

    public CdcConnectorRegistration Registration() =>
        new(
            Infrastructure.CreateJournalStore(),
            Infrastructure.Bindings,
            Hooks.Decorate(Provider, _ => CdcControllerBoundary.ProviderProof),
            Templates,
            Kafka,
            Infrastructure.Connect,
            Infrastructure.Worker,
            TimeProvider.System
        );

    public async Task ReopenRuntimeAsync(CancellationToken cancellationToken)
    {
        if (Runtime is not null)
        {
            await Runtime.DisposeAsync();
        }
        var services = new ServiceCollection().AddLogging();
        services.AddDocumentCacheRuntimeServices(
            _configuration,
            new Serilog.LoggerConfiguration().CreateLogger(),
            Target,
            DocumentCacheRuntimeTargetSelection.RequireConfiguredMembership
        );
        var schema = A.Fake<IApiSchemaProvider>();
        A.CallTo(() => schema.GetApiSchemaNodes()).Returns(_schema.NormalizedNodes);
        A.CallTo(() => schema.SchemaLoadId).Returns(Guid.NewGuid());
        A.CallTo(() => schema.IsSchemaValid).Returns(true);
        A.CallTo(() => schema.ApiSchemaFailures).Returns([]);
        services.Replace(ServiceDescriptor.Singleton(schema));
        var stores = A.Fake<IDataStoreProvider>();
        var store = new DataStore(
            1,
            ProviderToken,
            "test",
            ConnectionString,
            [],
            _provider == CdcProvider.Postgresql
                ? RelationalProviderToken.Postgresql
                : RelationalProviderToken.SqlServer,
            RelationalProviderMetadataStatus.Supported
        );
        A.CallTo(() => stores.GetById(1, A<string>._)).Returns(store);
        A.CallTo(() => stores.GetAll(A<string>._)).Returns([store]);
        A.CallTo(() => stores.LoadDataStores(A<string>._, A<CancellationToken>._))
            .Returns(new List<DataStore> { store });
        services.RemoveAll<ConfigurationServiceDataStoreProvider>();
        services.Replace(ServiceDescriptor.Singleton(stores));
        var runtime = Observed(
            await CdcProjectionRuntimeFactory.OpenAsync(
                services.BuildServiceProvider(),
                Target,
                cancellationToken
            )
        );
        Preparation.Add("runtime-initialized");
        Runtime = Controllers.HookRuntime(new RecordingRuntime(runtime, this));
    }

    public async Task RegisterAsync(CancellationToken cancellationToken, bool resumeProvider = false)
    {
        if (!resumeProvider)
        {
            Observed(await Controllers.Activation.ActivateAsync(Request, Runtime, cancellationToken));
        }
        Observed(await Controllers.ProviderSetup.SetupAsync(Request, Runtime, cancellationToken));
        Observed(await KafkaProvisioning().ProvisionBindingAsync(Request, cancellationToken));
        if (_provider == CdcProvider.SqlServer)
        {
            var artifacts = CdcConnectorTemplateBindingArtifacts
                .From(Request.Binding, nameof(Request))
                .ArtifactInventory;
            PreRegistrationHistoryTopics.Add(
                Observed(
                    await Kafka.InspectTopicAsync(
                        Request,
                        artifacts.SchemaHistoryTopicName!,
                        cancellationToken
                    )
                )
            );
        }
        await CdcControllerFixture.WaitAsync(
            async ct =>
            {
                var result = await Registration().RegisterAsync(Request, Runtime, ct);
                if (
                    result is CdcTransportResult<CdcConnectorRegistrationReceipt>.Unavailable unavailable
                    && unavailable.Diagnostic.Component == CdcDeploymentComponent.Connect
                    && unavailable.Diagnostic.Failure == CdcDeploymentFailure.Unavailable
                )
                {
                    // Connect can temporarily lack REST offset evidence during initial task assignment.
                    // Repeat the production reconciliation with the same journal; never repeat activation,
                    // recreate the connector, supply default evidence or infer a streaming offset.
                    RegistrationRetries.Add(unavailable.Diagnostic);
                    return false;
                }
                if (result is CdcTransportResult<CdcConnectorRegistrationReceipt>.Unavailable failed)
                {
                    var status = await Infrastructure.Connect.ReadStatusAsync(Request, ct);
                    var offsets = await Infrastructure.Connect.ReadOffsetEvidenceAsync(Request, ct);
                    await TestContext.Progress.WriteLineAsync(
                        JsonSerializer.Serialize(
                            new
                            {
                                RegistrationFailure = failed.Diagnostic,
                                Runtime = status is CdcTransportResult<CdcConnectStatus>.Observed found
                                    ? new
                                    {
                                        found.Value.Runtime.ConnectorState,
                                        TaskStates = found.Value.Tasks.Select(t => t.State).ToArray(),
                                    }
                                    : null,
                                OffsetState = offsets
                                    is CdcTransportResult<CdcConnectOffsetEvidence>.Observed value
                                    ? value.Value.State.ToString()
                                    : "Unavailable",
                            }
                        )
                    );
                }
                Observed(result);
                return true;
            },
            Request.Timing.WaitTimeout,
            TimeSpan.FromMilliseconds(250),
            cancellationToken
        );
    }

    public async Task<CoreCdc.CdcSourceHistoryClassificationResult> ProbeContinuityAsync(
        CancellationToken cancellationToken
    )
    {
        string operation = Guid.NewGuid().ToString("D");
        async Task<CoreCdc.CdcConnectorOffsetObservation> ReadOffsetAsync()
        {
            var offset = Observed(
                await Infrastructure.Connect.ReadOffsetEvidenceAsync(Request, cancellationToken)
            );
            return new CoreCdc.CdcConnectorOffsetObservation(
                1,
                operation,
                DateTimeOffset.UtcNow,
                Request.TargetIdentity,
                Request.Binding.Provider,
                Request.Binding.PhysicalSourceFingerprint,
                Request.Binding.ConnectorName,
                Request.Binding.ConnectorName,
                CoreCdc.CdcConnectorOffsetMatchResult.Exact,
                offset.SourcePartitionHash,
                false,
                false,
                offset.Postgresql.LsnProc,
                null,
                null,
                null,
                []
            );
        }
        var positions = new RecordingPositions(
            _services
                .GetServices<CoreCdc.ICdcProviderSourcePositionAdapter>()
                .Single(p => p.Provider == Request.Binding.Provider),
            this
        );
        CoreCdc.CdcSourceHistoryClassificationResult result = null!;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var offset = await ReadOffsetAsync();
            var handoff = Observed(
                await Controllers.ProviderSetup.SetupAsync(Request, Runtime, cancellationToken)
            );
            var mapped = CdcProviderSetupResultMapper.MapValidateOnlyResult(
                operation,
                handoff.ObservedAt,
                Request.Binding,
                handoff.TemplateRequest.ProviderSetupEvidence.Result
            );
            Task<CoreCdc.CdcSourceHistoryClassificationResult> ObserveAsync() =>
                positions.ObserveSourceHistoryAsync(
                    new(operation, Request.Binding, mapped.ProviderSetup, offset, mapped.ProviderHistory)
                    {
                        ExpectedConnectSourcePartitionHash = offset.ConnectSourcePartitionHash,
                    },
                    cancellationToken
                );
            result = await ObserveAsync();
            if (result.Observation.Continuity == CoreCdc.CdcSourceHistoryContinuity.Unknown)
            {
                // As in admission, refresh Connect after sampling the slot.
                // Preserve every raw result and return immediately on history loss.
                offset = await ReadOffsetAsync();
                result = await ObserveAsync();
            }
            if (result.Observation.Continuity != CoreCdc.CdcSourceHistoryContinuity.Unknown)
            {
                return result;
            }
        }
        return result;
    }

    public Task<CdcProviderSetupResult> InspectProviderAsync(CancellationToken token)
    {
        var source = Request.ProviderSetup;
        return Provider.SetupAsync(
            new(
                source.Provider,
                CdcProviderSetupMode.ValidateOnly,
                source.BoundPhysicalSourceFingerprint,
                source.SetupPrincipal,
                source.ConnectorPrincipal,
                source.ArtifactNames,
                source.ArtifactOutput,
                source.ExpectedSourceInventory,
                source.DmsManagedTableInventory,
                databaseExecutor: source.DatabaseExecutor
            ),
            token
        );
    }

    public CdcInitialReadiness AdmissionWithKafka(ICdcKafkaAdminAdapter kafka) =>
        new(
            Infrastructure.StateRoot,
            Provider,
            Templates,
            kafka,
            Infrastructure.Connect,
            Infrastructure.Worker,
            Infrastructure.Metrics,
            _services
                .GetServices<CoreCdc.ICdcProviderSourcePositionAdapter>()
                .Single(p => p.Provider == Request.Binding.Provider)
        );

    public CdcDeploymentRequest WithTiming(CdcDeploymentTiming timing) =>
        new(
            Request.Binding,
            Request.DmsSettings,
            Request.ProviderSetup,
            Request.ConnectEndpoint,
            Request.WorkerMetricsEndpoint,
            Request.ConnectorPolicy,
            Request.WorkerPolicy,
            Request.ProviderConnectionProperties,
            Request.KafkaClientSecurityProperties,
            timing
        );

    public CdcInitialReadiness AdmissionWithMetrics(
        Func<
            CdcTransportResult<CdcConnectorTelemetryObservation>,
            CancellationToken,
            Task<CdcTransportResult<CdcConnectorTelemetryObservation>>
        > afterCollection
    ) =>
        new(
            Infrastructure.StateRoot,
            Provider,
            Templates,
            Kafka,
            Infrastructure.Connect,
            Infrastructure.Worker,
            new InterceptedMetrics(Infrastructure.Metrics, afterCollection),
            _services
                .GetServices<CoreCdc.ICdcProviderSourcePositionAdapter>()
                .Single(p => p.Provider == Request.Binding.Provider)
        );

    private sealed class InterceptedMetrics(
        ICdcWorkerMetricsTransport inner,
        Func<
            CdcTransportResult<CdcConnectorTelemetryObservation>,
            CancellationToken,
            Task<CdcTransportResult<CdcConnectorTelemetryObservation>>
        > afterCollection
    ) : ICdcWorkerMetricsTransport
    {
        public async Task<CdcTransportResult<CdcConnectorTelemetryObservation>> CollectAsync(
            CdcDeploymentRequest request,
            CdcTelemetryObservationPass pass,
            CancellationToken cancellationToken
        ) =>
            await afterCollection(
                await inner.CollectAsync(request, pass, cancellationToken),
                cancellationToken
            );
    }

    public async Task<CdcWorkflowJournal> JournalAsync(CancellationToken cancellationToken)
    {
        await using var session = await new LocalCdcWorkflowJournalStore(
            Infrastructure.StateRoot
        ).AcquireAsync(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50), cancellationToken);
        return await session.ReadAsync(Request.TargetIdentity, cancellationToken);
    }

    public async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<T> ScalarAsync<T>(string sql, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync(cancellationToken))!, typeof(T));
    }

    public static T Observed<T>(CdcTransportResult<T> result)
        where T : notnull
    {
        result
            .State.Should()
            .Be(CdcTransportEvidenceState.Observed, "{0}", string.Join(", ", result.Diagnostics));
        return ((CdcTransportResult<T>.Observed)result).Value;
    }

    private async Task WriteEvidenceAsync()
    {
        if (Infrastructure is not null && Request is not null)
        {
            string artifact = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "admission-evidence-" + Guid.NewGuid().ToString("N") + ".json"
            );
            var journal = await JournalAsync(CancellationToken.None);
            await File.WriteAllTextAsync(
                artifact,
                System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        Test = TestContext.CurrentContext.Test.Name,
                        journal.WriterPublicationAuthorized,
                        Operations = journal.Operations.Select(o => new
                        {
                            o.Effect,
                            o.IntendedAt,
                            Completions = o.Completions.Length,
                        }),
                        Preparation,
                        ValidationFailures = _validationFailures.ToArray(),
                        DatabaseFailures = _databaseFailures.ToArray(),
                        ConnectObservations = Hooks.ConnectObservations,
                        ProviderModes,
                        ProviderResults = ProviderResults.Select(r => new
                        {
                            r.Mode,
                            r.Outcome,
                            Diagnostics = r.Diagnostics.Select(d => new
                            {
                                d.Code,
                                d.Severity,
                                d.ProviderErrorClass,
                                d.ProviderErrorCode,
                                d.ProviderErrorState,
                            }),
                        }),
                        PreRegistrationHistoryTopics = PreRegistrationHistoryTopics.Select(t => new
                        {
                            PartitionCount = t.PartitionReplicas.Count,
                            t.Configuration,
                        }),
                        RegistrationRetries,
                        ContinuitySamples,
                        SourceHistory = SourceHistory.Select(h => new
                        {
                            h.ObservedAt,
                            h.Continuity,
                            h.SqlServerJobs,
                            Diagnostics = h.Diagnostics.Select(d => new { d.Category, d.Path }),
                        }),
                        BarrierObservations = BarrierObservations.Select(b => new
                        {
                            b.ObservedAt,
                            b.BarrierState,
                            Diagnostics = b.Diagnostics.Select(d => new { d.Category, d.Path }),
                            ContractDiagnostics = CoreCdc
                                .CdcProviderBarrierObservationValidator.Validate(
                                    b,
                                    new(
                                        b.OperationId,
                                        b.TargetIdentity,
                                        b.PhysicalSourceFingerprint,
                                        DateTimeOffset.UtcNow
                                    )
                                )
                                .Diagnostics.Select(d => new { d.Category, d.Path }),
                        }),
                        Lag = Lag.Select(l => new
                        {
                            l.ObservedAt,
                            l.LagState,
                            l.CurrentLagMilliseconds,
                        }),
                        Barriers = Barriers.Select(b => new
                        {
                            b.Provider,
                            b.Succeeded,
                            b.BarrierCapturedAt,
                            b.SqlServerCommitLsn,
                            b.SqlServerChangeLsn,
                            b.SqlServerEventSerialNo,
                        }),
                        Projection = ProjectionObservations.SelectMany(o =>
                            o.Targets.Select(t => new
                            {
                                o.ObservedAt,
                                Health = t.OperationalHealth.Status,
                                CaughtUp = t.CaughtUp.Status,
                                HealthReasons = t.OperationalHealth.Reason,
                                CaughtUpReasons = t.CaughtUp.Reason,
                            })
                        ),
                    }
                )
            );
            TestContext.AddTestAttachment(
                artifact,
                "Sanitized admission journal and live observation evidence"
            );
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        AppDomain.CurrentDomain.FirstChanceException -= _observeValidationFailure;
        List<Exception> failures = [];
        async Task CleanupAsync(Func<Task> cleanup)
        {
            try
            {
                await cleanup();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        // Startup can already have removed an incomplete root; never replace its original failure.
        if (Infrastructure is not null && Directory.Exists(Infrastructure.StateRoot))
        {
            await CleanupAsync(WriteEvidenceAsync);
        }
        BeforeRuntimeCall = _ => { };
        AfterRuntimeCall = _ => { };
        if (Infrastructure is not null)
        {
            Hooks.OnBoundary = _ => { };
        }
        if (Runtime is not null)
        {
            await CleanupAsync(() => Runtime.DisposeAsync().AsTask());
        }
        if (_providerConnection is not null)
        {
            await CleanupAsync(() => _providerConnection.DisposeAsync().AsTask());
        }
        Kafka?.Dispose();
        if (_services is not null)
        {
            await CleanupAsync(() => _services.DisposeAsync().AsTask());
        }
        (_configuration as IDisposable)?.Dispose();
        if (Infrastructure is not null)
        {
            await CleanupAsync(() => Infrastructure.DisposeAsync().AsTask());
        }
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Admission fixture evidence or cleanup failed. Details redacted."
            );
        }
    }

    private sealed class ManagedSource(
        string admin,
        string connection,
        string database,
        DdlPipelineEmission emission
    ) : ICdcManagedDatabaseProvisioner
    {
        public bool CreateDatabase()
        {
            using var db = new NpgsqlConnection(admin);
            db.Open();
            using var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", db);
            try
            {
                command.ExecuteNonQuery();
                return true; // Only the successful CREATE statement grants this receipt.
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.DuplicateDatabase)
            {
                return false;
            }
        }

        public void ProvisionSchema(bool databaseWasCreated)
        {
            if (!databaseWasCreated)
            {
                ValidateSchema();
                return;
            }
            using var db = new NpgsqlConnection(connection);
            db.Open();
            using var command = new NpgsqlCommand(emission.CombinedSql, db);
            command.ExecuteNonQuery();
        }

        public void ValidateSchema()
        {
            using var db = new NpgsqlConnection(connection);
            db.Open();
            using var command = new NpgsqlCommand("SELECT COUNT(*) FROM dms.\"EffectiveSchema\"", db);
            Convert.ToInt64(command.ExecuteScalar()).Should().Be(1);
        }

        public async Task<string> ReadSourceFingerprintAsync(CancellationToken cancellationToken) =>
            (
                await new PostgresqlDocumentCachePhysicalSourceFingerprintReader(
                    NullLogger<PostgresqlDocumentCachePhysicalSourceFingerprintReader>.Instance
                ).ReadFingerprintAsync(connection, cancellationToken)
            )
                .Fingerprint!
                .Value;
    }

    public sealed record ContinuitySample(
        CoreCdc.CdcSourceHistoryContinuity Continuity,
        bool OffsetAtOrAfterRestart,
        bool OffsetAheadOfConfirmedFlush,
        bool OffsetAtOrBeforeCurrentWal,
        bool SlotActiveWithRetainedWalAndNoInvalidation
    );

    private sealed class RecordingPositions(
        CoreCdc.ICdcProviderSourcePositionAdapter inner,
        CdcProviderAdmissionFixture owner
    ) : CoreCdc.ICdcProviderSourcePositionAdapter
    {
        public CoreCdc.CdcProvider Provider => inner.Provider;

        public Task<CoreCdc.CdcProviderBarrierCaptureResult> CaptureBarrierAsync(
            CoreCdc.CdcProviderBarrierCaptureRequest request,
            CancellationToken cancellationToken = default
        ) => inner.CaptureBarrierAsync(request, cancellationToken);

        public CoreCdc.CdcProviderBarrierObservation ObserveProviderBarrier(
            CoreCdc.CdcProviderBarrierObservationRequest request
        )
        {
            var result = inner.ObserveProviderBarrier(request);
            owner.BarrierObservations.Add(result);
            return result;
        }

        public async Task<CoreCdc.CdcSourceHistoryClassificationResult> ObserveSourceHistoryAsync(
            CoreCdc.CdcSourceHistoryObservationRequest request,
            CancellationToken cancellationToken = default
        )
        {
            var result = await inner.ObserveSourceHistoryAsync(request, cancellationToken);
            owner.SourceHistory.Add(result.Observation);
            if (request.ConnectorOffset is { LsnProc: { } lsn } && request.ProviderHistory is { } range)
            {
                var position = new CoreCdc.CdcPostgresqlWalPosition(unchecked((ulong)lsn));
                var start = CoreCdc.CdcPostgresqlProviderPosition.ParseWalLsn(range.RetainedRangeStart);
                var end = CoreCdc.CdcPostgresqlProviderPosition.ParseWalLsn(
                    range.PostgresqlSlot?.ConfirmedFlushLsn
                );
                owner.ContinuitySamples.Add(
                    new(
                        result.Observation.Continuity,
                        start.Position is { } low && position.CompareTo(low) >= 0,
                        end.Position is { } high && position.CompareTo(high) > 0,
                        await owner.ScalarAsync<bool>(
                            $"SELECT pg_current_wal_lsn() >= '{position}'::pg_lsn",
                            cancellationToken
                        ),
                        await owner.ScalarAsync<bool>(
                            "SELECT active AND wal_status IN ('reserved', 'extended') AND invalidation_reason IS NULL FROM pg_replication_slots WHERE database = current_database()",
                            cancellationToken
                        )
                    )
                );
            }
            return result;
        }
    }

    private sealed class RecordingMetrics(ICdcWorkerMetricsTransport inner, CdcProviderAdmissionFixture owner)
        : ICdcWorkerMetricsTransport
    {
        public async Task<CdcTransportResult<CdcConnectorTelemetryObservation>> CollectAsync(
            CdcDeploymentRequest request,
            CdcTelemetryObservationPass pass,
            CancellationToken cancellationToken
        )
        {
            owner.BeforeMetricsCall();
            var result = owner.TransformMetrics(await inner.CollectAsync(request, pass, cancellationToken));
            if (result is CdcTransportResult<CdcConnectorTelemetryObservation>.Observed observed)
            {
                owner.Lag.Add(observed.Value.ReadForEvaluation(pass));
            }
            return result;
        }
    }

    private sealed class RecordingProvider(
        ICdcProviderSetupService inner,
        List<CdcProviderSetupMode> modes,
        List<CdcProviderSetupResult> results
    ) : ICdcProviderSetupService
    {
        public async Task<CdcProviderSetupResult> SetupAsync(
            CdcProviderSetupRequest request,
            CancellationToken cancellationToken = default
        )
        {
            modes.Add(request.Mode);
            var result = await inner.SetupAsync(request, cancellationToken);
            results.Add(result);
            return result;
        }
    }

    private sealed class RecordingRuntime(ICdcProjectionRuntime inner, CdcProviderAdmissionFixture owner)
        : ICdcProjectionRuntime
    {
        public async Task<DocumentCacheAdministrativeCommandResult> ActivateAsync(
            DocumentCacheGuardedNewEmptyActivationRequest request,
            CancellationToken cancellationToken
        )
        {
            owner.BeforeRuntimeCall(nameof(ActivateAsync));
            // Verify durability at the actual E18 entry, before its provider mutex/mutation.
            (
                await owner.Infrastructure.Bindings.ExactMatchBindingAsync(
                    owner.Request.Binding,
                    cancellationToken
                )
            )
                .Status.Should()
                .Be(CoreCdc.CdcControlPlaneOperationStatus.Succeeded);
            owner.Preparation.Add("guarded-activation");
            var result = await inner.ActivateAsync(request, cancellationToken);
            owner.AfterRuntimeCall(nameof(ActivateAsync));
            return result;
        }

        public Task<CdcInitialDatabaseObservation> ObserveInitialDatabaseAsync(
            CancellationToken cancellationToken
        ) => inner.ObserveInitialDatabaseAsync(cancellationToken);

        public Task<CdcInitialDatabaseObservation> ObserveEstablishedDatabaseAsync(
            CancellationToken cancellationToken
        ) => inner.ObserveEstablishedDatabaseAsync(cancellationToken);

        public async Task<CoreCdc.CdcProviderBarrierCaptureResult> CaptureBarrierAsync(
            CdcDeploymentRequest request,
            CoreCdc.ICdcProviderSourcePositionAdapter adapter,
            CancellationToken cancellationToken
        )
        {
            owner.BeforeRuntimeCall(nameof(CaptureBarrierAsync));
            var result = await inner.CaptureBarrierAsync(request, adapter, cancellationToken);
            owner.Barriers.Add(result);
            owner.AfterRuntimeCall(nameof(CaptureBarrierAsync));
            return result;
        }

        public async Task StartProcessingAsync(CancellationToken cancellationToken)
        {
            owner.BeforeRuntimeCall(nameof(StartProcessingAsync));
            await inner.StartProcessingAsync(cancellationToken);
            owner.AfterRuntimeCall(nameof(StartProcessingAsync));
        }

        public async Task<DocumentCacheStatusResponse> ObserveAsync(CancellationToken cancellationToken)
        {
            owner.BeforeRuntimeCall(nameof(ObserveAsync));
            var result = await inner.ObserveAsync(cancellationToken);
            owner.ProjectionObservations.Add(result);
            owner.AfterRuntimeCall(nameof(ObserveAsync));
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            owner.BeforeRuntimeCall(nameof(DisposeAsync));
            await inner.DisposeAsync();
            owner.RuntimeStoppedAt = DateTimeOffset.UtcNow;
            owner.AfterRuntimeCall(nameof(DisposeAsync));
        }
    }
}
