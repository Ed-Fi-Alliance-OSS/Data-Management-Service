// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Confluent.Kafka;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.DocumentCacheRuntime;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Startup;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

/// <summary>Owns an actual new database; only schema-file and CMS delivery are fixture substitutes.</summary>
internal sealed class CdcPostgresqlAdmissionFixture : IAsyncDisposable
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
    public List<CoreCdc.CdcConnectorLagObservation> Lag { get; } = [];
    public DateTimeOffset RuntimeStoppedAt { get; private set; }
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
    private NpgsqlConnection _providerConnection = null!;
    private IConfigurationRoot _configuration = null!;
    private ApiSchemaFileLoadResult.SuccessResult _schema = null!;
    private DdlPipelineEmission _emission = null!;
    private static readonly DocumentCacheTargetKey Target = DocumentCacheTargetKey.Create("", 1);

    public static async Task<CdcPostgresqlAdmissionFixture> StartAsync(CancellationToken cancellationToken)
    {
        var suite = new CdcPostgresqlAdmissionFixture();
        try
        {
            suite.Infrastructure = await CdcControllerFixture.StartAsync(
                CdcProvider.Postgresql,
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
                nativeKafka: true
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
        ConnectionString = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
        {
            Database = Database,
            Pooling = false,
            Password = CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword,
        }.ConnectionString;
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
        var emission = DdlPipelineHelpers.BuildDdlEmissionForDialect(schemaSet, SqlDialect.Pgsql);
        _emission = emission;
        var provisioner = new ManagedSource(
            new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres" }.ConnectionString,
            ConnectionString,
            Database,
            emission
        );
        var provisioned = await new CdcManagedDatabaseProvisioning(
            Infrastructure.CreateJournalStore()
        ).ProvisionAsync(
            new("dms", "default", "1", "admission", 1, CoreCdc.CdcProvider.Postgresql),
            provisioner,
            cancellationToken
        );
        provisioned.CreationReceipt.Outcome.Should().Be(CdcDatabaseCreationOutcome.Created);
        await ExecuteAsync(
            "CREATE ROLE dms_connector LOGIN REPLICATION PASSWORD 'EdFi_Dms1!'",
            cancellationToken
        );
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppSettings:Datastore"] = "postgresql",
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
            .CdcArtifactNameGenerator.Render(
                new("dms", "edfi.documents", "admission", 1, CoreCdc.CdcProvider.Postgresql)
            )
            .Inventory!;
        var binding = new CoreCdc.CdcBinding(
            1,
            "dms",
            "default",
            "1",
            "admission",
            1,
            CoreCdc.CdcProvider.Postgresql,
            provisioned.PhysicalSourceFingerprint,
            artifacts.ConnectorName,
            artifacts.TopicName,
            1,
            CoreCdc.CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
            1
        );
        _providerConnection = new NpgsqlConnection(ConnectionString);
        await _providerConnection.OpenAsync(cancellationToken);
        var connectionProperties = new Dictionary<string, string>(
            Infrastructure.Resources.ProviderConnectionProperties
        )
        {
            ["database.dbname"] = Database,
        };
        Request = new(
            binding,
            _configuration,
            new(
                CdcProvider.Postgresql,
                CdcProviderSetupMode.InitialCreateOrExactMatch,
                new(CdcSourceFingerprintMetadata.Version, provisioned.PhysicalSourceFingerprint),
                new(new("postgres")),
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
                heartbeatInterval: TimeSpan.FromSeconds(1)
            ),
            Tests.Unit.CdcDeploymentRequestTestData.Worker(
                heapBytes: 536_870_912,
                digest: CdcQualifiedWorkerImage.Digest,
                offsetTopic: Infrastructure.Resources.ControllerProject + ".connect.offsets",
                workerKey: Infrastructure.Resources.ControllerProject
            ),
            new(CdcProvider.Postgresql, connectionProperties),
            CdcKafkaClientSecurityProperties.Empty,
            new(
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(3),
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromSeconds(30)
            )
        );
        var registrations = new ServiceCollection()
            .AddLogging()
            .AddSingleton<Serilog.ILogger>(_ => new Serilog.LoggerConfiguration().CreateLogger())
            .AddCdcProviderSetup()
            .AddCdcConnectorTemplates()
            .AddPostgresqlDmsCdcControlPlane();
        _services = registrations.BuildServiceProvider();
        Provider = new RecordingProvider(
            _services.GetRequiredService<ICdcProviderSetupService>(),
            ProviderModes
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
        Controllers = new(
            Infrastructure,
            Provider,
            Templates,
            Kafka,
            new RecordingPositions(
                _services
                    .GetServices<CoreCdc.ICdcProviderSourcePositionAdapter>()
                    .Single(p => p.Provider == binding.Provider),
                this
            ),
            new RecordingMetrics(Infrastructure.Metrics, Lag)
        );
        await ReopenRuntimeAsync(cancellationToken);
        // This is the same controller operation used by worker startup, before Docker launches it.
        var offset = Observed(
            await KafkaProvisioning().ProvisionOffsetStoreAsync(Request, cancellationToken)
        );
        offset.PolicyState.Should().Be(CoreCdc.CdcConnectOffsetStorePolicyState.Satisfied);
    }

    public ICdcManagedDatabaseProvisioner CreateManagedSource(string database) =>
        new ManagedSource(
            new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres" }.ConnectionString,
            new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString,
            database,
            _emission
        );

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
            "postgresql",
            "test",
            ConnectionString,
            [],
            RelationalProviderToken.Postgresql,
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
        Runtime = Controllers.HookRuntime(new RecordingRuntime(runtime, this));
    }

    public async Task RegisterAsync(CancellationToken cancellationToken)
    {
        Observed(await Controllers.Activation.ActivateAsync(Request, Runtime, cancellationToken));
        Observed(await Controllers.ProviderSetup.SetupAsync(Request, Runtime, cancellationToken));
        Observed(await KafkaProvisioning().ProvisionBindingAsync(Request, cancellationToken));
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
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<T> ScalarAsync<T>(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
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
                        ProviderModes,
                        RegistrationRetries,
                        ContinuitySamples,
                        SourceHistory = SourceHistory.Select(h => new
                        {
                            h.ObservedAt,
                            h.Continuity,
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
        CdcPostgresqlAdmissionFixture owner
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

    private sealed class RecordingMetrics(
        ICdcWorkerMetricsTransport inner,
        List<CoreCdc.CdcConnectorLagObservation> observations
    ) : ICdcWorkerMetricsTransport
    {
        public async Task<CdcTransportResult<CdcConnectorTelemetryObservation>> CollectAsync(
            CdcDeploymentRequest request,
            CdcTelemetryObservationPass pass,
            CancellationToken cancellationToken
        )
        {
            var result = await inner.CollectAsync(request, pass, cancellationToken);
            if (result is CdcTransportResult<CdcConnectorTelemetryObservation>.Observed observed)
            {
                observations.Add(observed.Value.ReadForEvaluation(pass));
            }
            return result;
        }
    }

    private sealed class RecordingProvider(ICdcProviderSetupService inner, List<CdcProviderSetupMode> modes)
        : ICdcProviderSetupService
    {
        public Task<CdcProviderSetupResult> SetupAsync(
            CdcProviderSetupRequest request,
            CancellationToken cancellationToken = default
        )
        {
            modes.Add(request.Mode);
            return inner.SetupAsync(request, cancellationToken);
        }
    }

    private sealed class RecordingRuntime(ICdcProjectionRuntime inner, CdcPostgresqlAdmissionFixture owner)
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
            var result = await inner.ActivateAsync(request, cancellationToken);
            owner.AfterRuntimeCall(nameof(ActivateAsync));
            return result;
        }

        public Task<CdcInitialDatabaseObservation> ObserveInitialDatabaseAsync(
            CancellationToken cancellationToken
        ) => inner.ObserveInitialDatabaseAsync(cancellationToken);

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
