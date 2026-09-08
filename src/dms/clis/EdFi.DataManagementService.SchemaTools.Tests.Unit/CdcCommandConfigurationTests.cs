// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.SchemaTools.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using CoreProvider = EdFi.DataManagementService.Core.DocumentCache.Cdc.CdcProvider;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[TestFixture("postgresql", CoreProvider.Postgresql)]
[TestFixture("sqlserver", CoreProvider.SqlServer)]
public class Given_Cdc_command_configuration(string providerToken, CoreProvider provider)
{
    private string _root = null!;
    private IConfigurationRoot _settings = null!;
    private CdcCommandConfiguration _config = null!;
    private CdcTargetIdentity _target = null!;
    private const string Fingerprint =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "cdc-command-" + Guid.NewGuid().ToString("N"));
        _settings = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConfigurationServiceSettings:BaseUrl"] = "http://localhost:8081",
                    ["AppSettings:Datastore"] = provider == CoreProvider.Postgresql ? "postgresql" : "mssql",
                    ["DataManagement:DocumentCache:Targets:0:DataStoreId"] = "42",
                    ["Cdc:LagThresholdMilliseconds"] = "5000",
                    ["Cdc:Provider"] = providerToken,
                    ["Cdc:DeploymentKey"] = "local",
                    ["Cdc:DataStoreId"] = "42",
                    ["Cdc:InstanceKey"] = "datastore-42",
                    ["Cdc:Generation"] = "1",
                    ["Cdc:TopicPrefix"] = "edfi",
                    ["Cdc:PartitionCount"] = "1",
                    ["Cdc:MaxRecordBytes"] = "10000000",
                    ["Cdc:KafkaBootstrapServers"] = "kafka:9092",
                    ["Cdc:KafkaAdminBootstrapServers"] = "localhost:9092",
                    ["Cdc:Compose:Project"] = "cdc-test",
                    ["Cdc:Compose:File"] = "/compose.json",
                    ["Cdc:Compose:EnvironmentFile"] = "/environment",
                    ["Cdc:Compose:BrokerSizeOverrideFile"] = "/state/broker-size.json",
                    ["Cdc:ConnectEndpoint"] = "http://localhost:8083",
                    ["Cdc:WorkerMetricsEndpoint"] = "http://localhost:9404/metrics",
                    ["Cdc:DurabilityProfile"] = "LocalSingleBroker",
                    ["Cdc:AuthorizationProfile"] = "AuthorizationDisabledLocal",
                    ["Cdc:Worker:Key"] = "worker",
                    ["Cdc:Worker:OffsetStorageTopic"] = "connect-offsets",
                    ["Cdc:Worker:HeapBytes"] = "536870912",
                    ["Cdc:Worker:Principal"] = "worker",
                    ["Cdc:Worker:ConnectorPrincipal"] = "connector",
                    ["Cdc:Worker:AdministratorPrincipal"] = "administrator",
                    ["Cdc:SetupConnectionString"] =
                        provider == CoreProvider.Postgresql
                            ? "Host=localhost;Database=secret-database;Username=setup;Password=secret-sentinel"
                            : "Server=localhost;Database=secret-database;User ID=setup;Password=secret-sentinel",
                    ["Cdc:SetupPrincipal"] = "setup",
                    ["Cdc:DatabaseConnectorPrincipal"] = "cdc-reader",
                    ["Cdc:ProviderConnectionProperties:database.hostname"] = "database",
                    ["Cdc:ProviderConnectionProperties:database.port"] =
                        provider == CoreProvider.Postgresql ? "5432" : "1433",
                    ["Cdc:ProviderConnectionProperties:database.user"] = "cdc-reader",
                    ["Cdc:ProviderConnectionProperties:database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                    [
                        provider == CoreProvider.Postgresql
                            ? "Cdc:ProviderConnectionProperties:database.dbname"
                            : "Cdc:ProviderConnectionProperties:database.names"
                    ] = "secret-database",
                    ["Cdc:Schemas:0"] = Path.Combine(
                        TestContext.CurrentContext.TestDirectory,
                        "Fixtures",
                        "minimal-api-schema.json"
                    ),
                    ["Cdc:PublicationHistory:StatePath"] = _root,
                    ["Cdc:PublicationHistory:DeploymentKey"] = "local",
                }
            )
            .Build();
        _config = new(_settings);
        _target = _config.Target;
        var managed = A.Fake<ICdcManagedDatabaseProvisioner>();
        A.CallTo(() => managed.CreateDatabase()).Returns(true);
        A.CallTo(() => managed.ReadSourceFingerprintAsync(A<CancellationToken>._)).Returns(Fingerprint);
        await new CdcManagedDatabaseProvisioning(new LocalCdcWorkflowJournalStore(_root)).ProvisionAsync(
            _target,
            managed
        );
    }

    [TestCase("verified")]
    [TestCase("missing")]
    [TestCase("incomplete")]
    [TestCase("legacy")]
    [TestCase("resume")]
    [TestCase("retire")]
    public async Task It_requires_the_latest_typed_managed_shutdown_before_worker_rest_startup(
        string evidence
    )
    {
        var request = await RequestAsync();
        if (evidence != "missing")
        {
            await using var session = await new LocalCdcWorkflowJournalStore(_root).AcquireAsync(
                request.Timing.CallTimeout,
                request.Timing.PollInterval,
                CancellationToken.None
            );
            var journal = await session.ReadAsync(_target, CancellationToken.None);
            var operation = Guid.NewGuid();
            await session.RecordIntentAsync(
                _target,
                journal.WorkflowId,
                operation,
                CdcWorkflowEffect.StopConnector,
                [],
                CancellationToken.None
            );
            if (evidence != "incomplete")
            {
                await session.ReconcileCompletionAsync(
                    _target,
                    journal.WorkflowId,
                    operation,
                    (_, _) =>
                        Task.FromResult<CdcTransportResult<CdcWorkflowCompletion>>(
                            new CdcTransportResult<CdcWorkflowCompletion>.Observed(
                                evidence == "legacy"
                                    ? new CdcWorkflowCompletion.Reconciled()
                                    : new CdcWorkflowCompletion.Shutdown()
                            )
                        ),
                    CancellationToken.None
                );
            }
            if (evidence is "resume" or "retire")
            {
                await session.RecordIntentAsync(
                    _target,
                    journal.WorkflowId,
                    Guid.NewGuid(),
                    evidence == "resume" ? CdcWorkflowEffect.ResumeConnector : CdcWorkflowEffect.Retire,
                    [],
                    CancellationToken.None
                );
            }
        }
        Func<Task> act = () =>
            CdcCommandRunner.RequireManagedShutdownAsync(_root, request, CancellationToken.None);
        if (evidence == "verified")
        {
            await act.Should().NotThrowAsync();
        }
        else
        {
            await act.Should().ThrowAsync<Exception>();
        }
    }

    [TearDown]
    public void Cleanup()
    {
        ((IDisposable)_settings).Dispose();
        Directory.Delete(_root, true);
    }

    [Test]
    public async Task It_builds_one_typed_request_from_original_source_and_ordinary_emitted_inventory()
    {
        var request = await RequestAsync();
        request.Binding.PhysicalSourceFingerprint.Should().Be(Fingerprint);
        request.Binding.Provider.Should().Be(provider);
        request.ProviderSetup.ExpectedSourceInventory.Should().HaveCount(3);
        request.WorkerPolicy.QualifiedImageDigest.Should().Be(CdcQualifiedWorkerImage.Digest);
        request.DmsSettings.Should().BeSameAs(_settings);
        JsonSerializer
            .Serialize(request)
            .Should()
            .NotContain("secret-sentinel")
            .And.NotContain("secret-database");
        Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories).Should().HaveCount(2);
    }

    [Test]
    public async Task It_never_reconstructs_missing_provenance_from_configuration()
    {
        Directory.Delete(_root, true);
        Func<Task> act = () => RequestAsync();
        await act.Should().ThrowAsync<Exception>();
        Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [TestCase("DataManagement:DocumentCache:Targets:0:DataStoreId", "99")]
    [TestCase("Cdc:Generation", "0")]
    [TestCase("Cdc:Provider", "mssql")]
    [TestCase("Cdc:AuthorizationProfile", "AuthorizationEnabled")]
    [TestCase("Cdc:DurabilityProfile", "Production")]
    [TestCase("Cdc:Timing:CallMilliseconds", "0")]
    [TestCase("Cdc:LagThresholdMilliseconds", "-1")]
    [TestCase("Cdc:LagThresholdMilliseconds", "")]
    public void It_rejects_unsupported_or_mismatched_configuration_before_runtime(string key, string value)
    {
        _settings[key] = value;
        Action act = _config.Validate;
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_does_not_accept_membership_in_an_unused_configuration_section()
    {
        _settings["DataManagement:DocumentCache:Targets:0:DataStoreId"] = "99";
        _settings["DocumentCache:Targets:0:DataStoreId"] = "42";
        Action act = _config.Validate;
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public async Task It_requires_externalized_connector_secrets()
    {
        _settings["Cdc:ProviderConnectionProperties:database.password"] = "secret-sentinel";
        Func<Task> act = () => RequestAsync();
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task It_resolves_the_provider_adapters_and_history_bridge_without_starting_a_runtime()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var services = new ServiceCollection();
        services.AddCdcCommandRuntime(_settings, logger, DocumentCacheTargetKey.Create("", 42));
        await using var scope = services.BuildServiceProvider();
        scope.GetRequiredService<ICdcProviderSetupService>().Should().NotBeNull();
        scope.GetRequiredService<ICdcConnectorTemplateService>().Should().NotBeNull();
        scope.GetServices<ICdcProviderSourcePositionAdapter>().Single().Provider.Should().Be(provider);
        scope
            .GetRequiredService<IDocumentCacheDownstreamPublicationHistoryProvider>()
            .Should()
            .BeOfType<CdcDownstreamPublicationHistoryProvider>();
    }

    [Test]
    public async Task It_reads_scoped_consumer_evidence_without_accepting_an_old_invocation_confirmation()
    {
        var request = await RequestAsync();
        var input = new CdcCommandAcknowledgement(
            Guid.NewGuid(),
            request.Binding.ToCompleteBindingIdentity(),
            request.ConnectorPolicy.MaxRecordBytes,
            20000000,
            33554432,
            "operator",
            true,
            []
        );
        string path = Path.Combine(_root, "acknowledgement-input.txt");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(input, CdcCommandHost.JsonOptions));
        var invocation = new CdcCommandInvocation(
            CdcCommandOperation.IncreaseRecordSize,
            "",
            _root,
            1,
            0,
            false,
            path,
            true
        );
        var parsed = await CdcCommandRunner.ReadAcknowledgementAsync(invocation, request, default);
        parsed.OperationId.Should().Be(input.OperationId);
        parsed.NoConsumers.Should().BeTrue();
        Func<Task> withoutConfirmation = () =>
            CdcCommandRunner.ReadAcknowledgementAsync(
                invocation with
                {
                    ConfirmConsumerCapacity = false,
                },
                request,
                default
            );
        await withoutConfirmation.Should().ThrowAsync<ArgumentException>();
    }

    [TestCase("extra")]
    [TestCase("duplicate")]
    [TestCase("different-source")]
    [TestCase("different-ceiling")]
    public async Task It_rejects_ambiguous_or_mismatched_acknowledgement_scope(string scenario)
    {
        var request = await RequestAsync();
        var input = new CdcCommandAcknowledgement(
            Guid.NewGuid(),
            request.Binding.ToCompleteBindingIdentity(),
            request.ConnectorPolicy.MaxRecordBytes,
            20000000,
            33554432,
            "operator",
            true,
            []
        );
        if (scenario == "different-source")
        {
            input = input with
            {
                BindingIdentity = input.BindingIdentity with
                {
                    PhysicalSourceFingerprint = "sha256:" + new string('b', 64),
                },
            };
        }
        if (scenario == "different-ceiling")
        {
            input = input with { PreviousMaxRecordBytes = 1 };
        }
        string json = JsonSerializer.Serialize(input, CdcCommandHost.JsonOptions);
        if (scenario == "extra")
        {
            json = json.Insert(1, "\"invocationId\":\"old-secret-sentinel\",");
        }
        if (scenario == "duplicate")
        {
            json = json.Insert(1, "\"operatorIdentity\":\"old-secret-sentinel\",");
        }
        string path = Path.Combine(_root, "acknowledgement-input.txt");
        await File.WriteAllTextAsync(path, json);
        Func<Task> act = () =>
            CdcCommandRunner.ReadAcknowledgementAsync(
                new(CdcCommandOperation.IncreaseRecordSize, "", _root, 1, 0, false, path, true),
                request,
                default
            );
        await act.Should().ThrowAsync<Exception>();
    }

    private async Task<CdcDeploymentRequest> RequestAsync()
    {
        await using var connection = _config.CreateConnection();
        return await _config.CreateRequestAsync(
            _root,
            connection,
            new ApiSchemaFileLoader(
                new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance),
                NullLogger<ApiSchemaFileLoader>.Instance
            ),
            new EffectiveSchemaSetBuilder(
                new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance),
                new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance)
            ),
            default
        );
    }
}
