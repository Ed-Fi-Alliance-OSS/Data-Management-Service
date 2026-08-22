// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed class CdcConnectorTemplatePinnedImageFixture : IAsyncDisposable
{
    private const string ConnectorPasswordEnvironmentVariable = "CDC_DATABASE_PASSWORD";
    internal const string ConnectorDatabasePassword = "EdFi_Dms1!";
    private const string EnvConfigProviderName = "env";
    private const string EnvConfigProviderClass =
        "org.apache.kafka.common.config.provider.EnvVarConfigProvider";
    private const string ConnectConfigProvidersEnvironmentVariable =
        $"CONNECT_CONFIG_PROVIDERS={EnvConfigProviderName}";
    private const string ConnectConfigProviderEnvClassEnvironmentVariable =
        $"CONNECT_CONFIG_PROVIDERS_ENV_CLASS={EnvConfigProviderClass}";
    private const string PostgresqlDatabaseName = "edfi_datastore";
    private const string PostgresqlPublicationName = "dms_binding_publication";
    private const string PostgresqlReplicationSlotName = "dms_binding_slot";
    private const string PostgresqlExpectedSourceTables = "dms.DocumentCache,dms.Document,dms.CdcHeartbeat";
    private const string PostgresqlObservedPublicationTables =
        "dms.CdcHeartbeat,dms.Document,dms.DocumentCache";
    private const string SqlServerDatabaseName = "edfi_datastore";
    private const string SqlServerGatingRoleName = "dms_binding_gate";
    private const string DocumentStateTransformClass = "org.edfi.kafka.connect.transforms.DocumentState";
    private const string DocumentStateJsonConverterClass =
        "org.edfi.kafka.connect.converters.DocumentStateJsonConverter";
    private const string KafkaMurmur2PartitionerClass =
        "org.edfi.kafka.connect.partitioner.KafkaMurmur2V1Partitioner";

    private static readonly TimeSpan ConnectStartupTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ConnectorRunningTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ProviderHeartbeatTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OffsetCommitTimeout = TimeSpan.FromMinutes(4);
    private static readonly IReadOnlyList<SqlServerCaptureInstanceDefinition> SqlServerCaptureInstances =
    [
        new(
            CdcSourceTableKind.DocumentCache,
            "DocumentCache",
            new CdcSafeName("dms_binding_document_cache"),
            "document_cache",
            "DocumentId",
            [
                new("DocumentId", "bigint"),
                new("DocumentUuid", "uniqueidentifier"),
                new("ProjectName", "nvarchar(256)"),
                new("ResourceName", "nvarchar(256)"),
                new("ResourceVersion", "nvarchar(32)"),
                new("ContentVersion", "bigint"),
                new("StreamEtag", "varchar(64)"),
                new("LastModifiedAt", "datetime2(7)"),
                new("DocumentJson", "nvarchar(max)"),
                new("ComputedAt", "datetime2(7)"),
            ]
        ),
        new(
            CdcSourceTableKind.Document,
            "Document",
            new CdcSafeName("dms_binding_document"),
            "document",
            "DocumentId",
            [
                new("DocumentId", "bigint"),
                new("DocumentUuid", "uniqueidentifier"),
                new("ResourceKeyId", "smallint"),
                new("CreatedByOwnershipTokenId", "smallint", IsNullable: true),
                new("ContentVersion", "bigint"),
                new("IdentityVersion", "bigint"),
                new("ContentLastModifiedAt", "datetime2(7)"),
                new("IdentityLastModifiedAt", "datetime2(7)"),
                new("CreatedAt", "datetime2(7)"),
            ]
        ),
        new(
            CdcSourceTableKind.CdcHeartbeat,
            "CdcHeartbeat",
            new CdcSafeName("dms_binding_cdc_heartbeat_capture"),
            "cdc_heartbeat",
            "HeartbeatId",
            [
                new("HeartbeatId", "smallint"),
                new("HeartbeatSequence", "bigint"),
                new("HeartbeatAt", "datetime2(7)"),
            ],
            [
                "CONSTRAINT [CK_CdcHeartbeat_Singleton] CHECK ([HeartbeatId] = 1)",
                "CONSTRAINT [CK_CdcHeartbeat_Sequence] CHECK ([HeartbeatSequence] >= 0)",
            ]
        ),
    ];

    private readonly CdcConnectorTemplateSmokeSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly IDockerCli _docker;
    private readonly ServiceProvider _serviceProvider;
    private readonly string _resourcePrefix;

    private CdcConnectorTemplatePinnedImageFixture(
        CdcProvider provider,
        CdcConnectorTemplateSmokeSettings settings,
        Uri connectBaseUri,
        string resourcePrefix,
        IDockerCli docker
    )
    {
        Provider = provider;
        _settings = settings;
        _resourcePrefix = resourcePrefix;
        _docker = docker;
        _httpClient = new HttpClient { BaseAddress = connectBaseUri };
        _serviceProvider = new ServiceCollection().AddCdcConnectorTemplates().BuildServiceProvider();
    }

    public CdcProvider Provider { get; }

    public string KafkaBootstrapServers => $"{BrokerContainerName}:9092";

    public IReadOnlyDictionary<string, string> ProviderConnectionProperties =>
        Provider switch
        {
            CdcProvider.Postgresql => new Dictionary<string, string>
            {
                ["database.hostname"] = ProviderContainerName,
                ["database.port"] = "5432",
                ["database.user"] = "postgres",
                ["database.password"] = $"${{env:{ConnectorPasswordEnvironmentVariable}}}",
                ["database.dbname"] = PostgresqlDatabaseName,
            },
            CdcProvider.SqlServer => new Dictionary<string, string>
            {
                ["database.hostname"] = ProviderContainerName,
                ["database.port"] = "1433",
                ["database.user"] = "sa",
                ["database.password"] = $"${{env:{ConnectorPasswordEnvironmentVariable}}}",
                ["database.names"] = SqlServerDatabaseName,
                ["driver.encrypt"] = "true",
                ["driver.trustServerCertificate"] = "true",
            },
            _ => throw new InvalidOperationException("Unsupported CDC provider."),
        };

    private string NetworkName => $"{_resourcePrefix}-network";

    private string BrokerContainerName => $"{_resourcePrefix}-broker";

    private string ConnectContainerName => $"{_resourcePrefix}-connect";

    private string ProviderContainerName => $"{_resourcePrefix}-provider";

    public static CdcConnectorTemplatePinnedImageFixture CreateOffline(CdcProvider provider) =>
        new(
            provider,
            CdcConnectorTemplateSmokeSettings.Offline,
            new Uri("http://127.0.0.1:8083"),
            "cdc-template-offline",
            DockerCli.Offline
        );

    public void AssertKafkaConnectWorkerConfigProviderStartupEnvironmentIsPinned()
    {
        IReadOnlyList<string> arguments = BuildKafkaConnectRunArguments();

        using var _ = new AssertionScope();
        HasDockerEnvironmentArgument(arguments, ConnectConfigProvidersEnvironmentVariable)
            .Should()
            .BeTrue("Kafka Connect must enable the env ConfigProvider before connector registration");
        HasDockerEnvironmentArgument(arguments, ConnectConfigProviderEnvClassEnvironmentVariable)
            .Should()
            .BeTrue("Kafka Connect must know the env ConfigProvider implementation class");
        HasDockerEnvironmentArgumentNamePrefix(arguments, "CONNECT_CONFIG_PROVIDERS_FILE")
            .Should()
            .BeFalse("the pinned-image fixture should not enable unused file ConfigProviders");
    }

    public static async Task<CdcConnectorTemplatePinnedImageFixture> StartAsync(
        CdcProvider provider,
        CancellationToken cancellationToken
    )
    {
        CdcConnectorTemplateSmokeSettings settings = CdcConnectorTemplateSmokeSettings.FromEnvironment(
            provider
        );
        settings.StopIfNotConfigured(provider);

        var docker = new DockerCli();
        await settings.StopOnPrerequisiteFailureAsync(
            docker.RequireDockerAsync(cancellationToken),
            "Docker CLI is unavailable or the Docker daemon is not reachable."
        );

        string resourcePrefix = $"dms-cdc-template-{Guid.NewGuid():N}";
        return await StartAsync(provider, settings, docker, resourcePrefix, cancellationToken);
    }

    internal static async Task<CdcConnectorTemplatePinnedImageFixture> StartAsync(
        CdcProvider provider,
        CdcConnectorTemplateSmokeSettings settings,
        IDockerCli docker,
        string resourcePrefix,
        CancellationToken cancellationToken,
        bool applyPrerequisitePolicy = true
    )
    {
        var fixture = new CdcConnectorTemplatePinnedImageFixture(
            provider,
            settings,
            new Uri("http://127.0.0.1:8083"),
            resourcePrefix,
            docker
        );

        try
        {
            await fixture.StartDockerResourcesAsync(cancellationToken);
            Uri connectBaseUri = await fixture.ReadMappedConnectBaseUriAsync(cancellationToken);

            fixture._httpClient.BaseAddress = connectBaseUri;
            await fixture.WaitForKafkaConnectAsync(cancellationToken);

            return fixture;
        }
        catch (Exception ex) when (ex is not AssertionException)
        {
            await fixture.DisposeAfterStartupFailureAsync();
            if (ex is OperationCanceledException)
            {
                throw;
            }

            if (!applyPrerequisitePolicy)
            {
                throw;
            }

            settings.StopOnPrerequisiteFailure(
                $"Pinned-image fixture prerequisites are not ready for {provider}: {ex.Message}"
            );
            throw;
        }
    }

    public CdcConnectorTemplateRequest BuildRequest() =>
        BuildRequest(Provider, KafkaBootstrapServers, ProviderConnectionProperties);

    public CdcConnectorTemplateResult Render(CdcConnectorTemplateRequest request)
    {
        ICdcConnectorTemplateService service =
            _serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();

        CdcConnectorTemplateResult rendered = service.Render(request);
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        rendered.Diagnostics.Should().BeEmpty();
        rendered.RegistrationPayload.Should().NotBeNull();
        return rendered;
    }

    public void AssertRenderedTemplateCanBeValidatedFromReadBack(
        CdcConnectorTemplateRequest request,
        IReadOnlyDictionary<string, string> effectiveConfig,
        CdcConnectorTemplateSourcePartitionEvidence sourcePartitionEvidence
    )
    {
        ICdcConnectorTemplateService service =
            _serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorProviderSetupEvidence preflightProviderSetupEvidence = BuildProviderSetupEvidence(
            request.Provider
        );
        CdcConnectorProviderSetupEvidence liveReadBackProviderSetupEvidence =
            BuildLiveReadBackProviderSetupEvidence(request.Provider);
        CdcConnectorTemplateResult rendered = service.Render(request);

        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        rendered.Diagnostics.Should().BeEmpty();
        AssertReadBackContainsOnlyRenderedProperties(rendered.Config, effectiveConfig);

        CdcConnectorTemplateResult preflight = service.ValidateRegistrationPreflight(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                effectiveConfig,
                preflightProviderSetupEvidence
            )
        );
        CdcConnectorTemplateResult liveReadBack = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                effectiveConfig,
                liveReadBackProviderSetupEvidence,
                sourcePartitionEvidence
            )
        );

        using var _ = new AssertionScope();
        preflight.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        preflight.Diagnostics.Should().BeEmpty();
        liveReadBack.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        liveReadBack.Diagnostics.Should().BeEmpty();
    }

    private static void AssertReadBackContainsOnlyRenderedProperties(
        IReadOnlyDictionary<string, string> renderedConfig,
        IReadOnlyDictionary<string, string> effectiveConfig
    )
    {
        string[] unexpectedKeys = effectiveConfig
            .Where(property =>
                !renderedConfig.ContainsKey(property.Key)
                && !(
                    string.Equals(property.Key, "topic.heartbeat.name", StringComparison.Ordinal)
                    && property.Value.Length == 0
                )
            )
            .Select(property => property.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        unexpectedKeys
            .Should()
            .BeEmpty(
                "qualified Kafka Connect read-back should not contain properties outside the rendered template except empty topic.heartbeat.name"
            );
    }

    public async Task CreateMinimalTopicsAndProviderObjectsAsync(
        CdcConnectorTemplateRequest request,
        CancellationToken cancellationToken
    )
    {
        await CreateMinimalTopicsAsync(request, cancellationToken);
        await CreateMinimalProviderObjectsAsync(cancellationToken);
    }

    public async Task AssertRuntimeLoadsRequiredClassesAsync(
        CdcConnectorTemplateResult rendered,
        CancellationToken cancellationToken
    )
    {
        string[] pluginClasses = await ReadConnectorPluginClassesAsync(cancellationToken);
        pluginClasses.Should().Contain(rendered.Config["connector.class"]);

        await RunJavaClassProbeAsync(
            [
                rendered.Config["connector.class"],
                DocumentStateTransformClass,
                DocumentStateJsonConverterClass,
                KafkaMurmur2PartitionerClass,
            ],
            cancellationToken
        );
    }

    public async Task AssertKafkaMurmur2PartitionerVectorsAsync(CancellationToken cancellationToken)
    {
        const string javaProbe = """
            set -eu
            class_path="$(find /kafka /opt/kafka /usr/share/java /usr/share/confluent-hub-components /debezium -name '*.jar' 2>/dev/null | tr '\n' ':')"
            test -n "${class_path}"
            cat >/tmp/CdcTemplatePartitionerProbe.java <<'JAVA'
            import java.nio.charset.StandardCharsets;
            import java.util.ArrayList;
            import java.util.Collections;
            import java.util.List;
            import org.apache.kafka.clients.producer.Partitioner;
            import org.apache.kafka.common.Cluster;
            import org.apache.kafka.common.Node;
            import org.apache.kafka.common.PartitionInfo;

            public class CdcTemplatePartitionerProbe {
                public static void main(String[] args) throws Exception {
                    @SuppressWarnings("unchecked")
                    Class<? extends Partitioner> partitionerType =
                        (Class<? extends Partitioner>) Class.forName(
                            "org.edfi.kafka.connect.partitioner.KafkaMurmur2V1Partitioner"
                        ).asSubclass(Partitioner.class);
                    Partitioner partitioner = partitionerType.getDeclaredConstructor().newInstance();
                    partitioner.configure(Collections.emptyMap());
                    try {
                        assertPartition(partitioner, "document-0001", 10, 6);
                        assertPartition(partitioner, "document-0002", 10, 4);
                        assertPartition(partitioner, "ed-fi/schools/255901001", 10, 8);
                        assertPartition(
                            partitioner,
                            "00000000-0000-0000-0000-000000000001",
                            10,
                            0
                        );
                    } finally {
                        partitioner.close();
                    }
                }

                private static void assertPartition(
                    Partitioner partitioner,
                    String key,
                    int partitionCount,
                    int expected
                ) {
                    byte[] keyBytes = key.getBytes(StandardCharsets.UTF_8);
                    int actual = partitioner.partition(
                        "edfi.documents",
                        key,
                        keyBytes,
                        null,
                        null,
                        cluster(partitionCount)
                    );
                    if (actual != expected) {
                        throw new IllegalStateException(
                            key + " expected partition " + expected + " but was " + actual
                        );
                    }
                }

                private static Cluster cluster(int partitionCount) {
                    List<PartitionInfo> partitions = new ArrayList<>();
                    for (int partition = 0; partition < partitionCount; partition++) {
                        partitions.add(
                            new PartitionInfo(
                                "edfi.documents",
                                partition,
                                null,
                                new Node[0],
                                new Node[0]
                            )
                        );
                    }

                    return new Cluster(
                        "cdc-template",
                        Collections.emptyList(),
                        partitions,
                        Collections.emptySet(),
                        Collections.emptySet()
                    );
                }
            }
            JAVA
            java -cp "${class_path}" /tmp/CdcTemplatePartitionerProbe.java
            """;

        await _docker.RunAsync(["exec", ConnectContainerName, "sh", "-lc", javaProbe], cancellationToken);
    }

    public async Task AssertConnectorConfigValidatesAsync(
        CdcConnectorTemplateResult rendered,
        CancellationToken cancellationToken
    )
    {
        string connectorClass = rendered.Config["connector.class"];
        using var content = new StringContent(
            JsonSerializer.Serialize(rendered.Config),
            Encoding.UTF8,
            "application/json"
        );
        using HttpResponseMessage response = await _httpClient.PutAsync(
            $"/connector-plugins/{Uri.EscapeDataString(connectorClass)}/config/validate",
            content,
            cancellationToken
        );
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        response
            .IsSuccessStatusCode.Should()
            .BeTrue($"Kafka Connect config validation failed: {SanitizeForAssertion(responseBody)}");

        ExtractValidationErrors(responseBody).Should().BeEmpty();
    }

    public async Task RegisterRenderedConnectorConfigDirectlyAsync(
        CdcConnectorTemplateResult rendered,
        CancellationToken cancellationToken
    )
    {
        await AssertKafkaConnectWorkerEnvConfigProviderEnabledAsync(cancellationToken);
        AssertRenderedProviderPasswordUsesEnvReference(rendered);

        rendered.RegistrationPayload.Should().NotBeNull();
        using var content = new StringContent(
            JsonSerializer.Serialize(rendered.RegistrationPayload),
            Encoding.UTF8,
            "application/json"
        );
        using HttpResponseMessage response = await _httpClient.PostAsync(
            "/connectors",
            content,
            cancellationToken
        );
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        response
            .StatusCode.Should()
            .BeOneOf(
                [HttpStatusCode.Created, HttpStatusCode.OK],
                $"Kafka Connect registration failed: {SanitizeForAssertion(responseBody)}"
            );
    }

    public async Task AssertRegisteredConnectorReachesRunningStateAsync(
        CdcConnectorTemplateRequest request,
        CancellationToken cancellationToken
    )
    {
        await WaitForRegisteredConnectorRunningAsync(request.ConnectorName.Value, cancellationToken);
    }

    public async Task<CdcConnectorSourceOffsetSnapshot> AssertHeartbeatAndCommittedOffsetProgressAsync(
        CdcConnectorTemplateRequest request,
        CancellationToken cancellationToken
    )
    {
        await WaitForRegisteredConnectorRunningAsync(request.ConnectorName.Value, cancellationToken);

        long startingHeartbeatSequence = await ReadProviderHeartbeatSequenceAsync(cancellationToken);
        CdcConnectorSourceOffsetSnapshot? startingOffset = await TryReadCommittedSourceOffsetAsync(
            request,
            cancellationToken
        );

        long advancedHeartbeatSequence = await WaitForProviderHeartbeatSequenceGreaterThanAsync(
            startingHeartbeatSequence,
            cancellationToken
        );
        CdcConnectorSourceOffsetSnapshot committedOffset = await WaitForCommittedSourceOffsetProgressAsync(
            request,
            startingOffset,
            cancellationToken
        );

        using var _ = new AssertionScope();
        advancedHeartbeatSequence.Should().BeGreaterThan(startingHeartbeatSequence);
        committedOffset.CanonicalOffsetJson.Should().NotBeNullOrWhiteSpace();
        return committedOffset;
    }

    public async Task RestartRegisteredConnectorAndAssertTemplateStillValidAsync(
        CdcConnectorTemplateRequest request,
        CdcConnectorSourceOffsetSnapshot preRestartCommittedOffset,
        CancellationToken cancellationToken
    )
    {
        preRestartCommittedOffset
            .Should()
            .NotBeNull("restart validation must use the committed offset observed before restart");

        string connectorName = request.ConnectorName.Value;
        using HttpResponseMessage response = await _httpClient.PostAsync(
            $"/connectors/{Uri.EscapeDataString(connectorName)}/restart?includeTasks=true&onlyFailed=false",
            content: null,
            cancellationToken
        );
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        response
            .StatusCode.Should()
            .BeOneOf(
                [HttpStatusCode.Accepted, HttpStatusCode.NoContent, HttpStatusCode.OK],
                $"Kafka Connect restart failed: {SanitizeForAssertion(responseBody)}"
            );

        await WaitForRegisteredConnectorRunningAsync(connectorName, cancellationToken);
        CdcConnectorSourceOffsetSnapshot retainedOffset = await WaitForRetainedCommittedSourceOffsetAsync(
            request,
            preRestartCommittedOffset,
            cancellationToken
        );
        CdcConnectorSourceOffsetSnapshot progressedOffset = await WaitForCommittedSourceOffsetProgressAsync(
            request,
            retainedOffset,
            cancellationToken
        );
        await AssertKafkaConnectReadBackMatchesExpectedConfigAsync(
            request,
            progressedOffset.SourcePartitionEvidence,
            cancellationToken
        );
        progressedOffset.CanonicalOffsetJson.Should().NotBeNullOrWhiteSpace();
    }

    public async Task AssertKafkaConnectReadBackMatchesExpectedConfigAsync(
        CdcConnectorTemplateRequest request,
        CancellationToken cancellationToken
    )
    {
        CdcConnectorSourceOffsetSnapshot committedOffset = await WaitForCommittedSourceOffsetProgressAsync(
            request,
            startingOffset: null,
            cancellationToken
        );

        await AssertKafkaConnectReadBackMatchesExpectedConfigAsync(
            request,
            committedOffset.SourcePartitionEvidence,
            cancellationToken
        );
    }

    private async Task AssertKafkaConnectReadBackMatchesExpectedConfigAsync(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePartitionEvidence sourcePartitionEvidence,
        CancellationToken cancellationToken
    )
    {
        string connectorName = request.ConnectorName.Value;
        using HttpResponseMessage response = await _httpClient.GetAsync(
            $"/connectors/{Uri.EscapeDataString(connectorName)}/config",
            cancellationToken
        );
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        response
            .IsSuccessStatusCode.Should()
            .BeTrue($"Kafka Connect config read-back failed: {SanitizeForAssertion(responseBody)}");

        IReadOnlyDictionary<string, string> config = ParseStringMap(responseBody);
        AssertRenderedTemplateCanBeValidatedFromReadBack(request, config, sourcePartitionEvidence);
    }

    public async ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        await _serviceProvider.DisposeAsync();

        if (_settings.KeepContainers || _docker.IsOffline)
        {
            return;
        }

        await _docker.RunAllowingFailureAsync(["rm", "-f", ConnectContainerName], CancellationToken.None);
        await _docker.RunAllowingFailureAsync(["rm", "-f", ProviderContainerName], CancellationToken.None);
        await _docker.RunAllowingFailureAsync(["rm", "-f", BrokerContainerName], CancellationToken.None);
        await _docker.RunAllowingFailureAsync(["network", "rm", NetworkName], CancellationToken.None);
    }

    private async ValueTask DisposeAfterStartupFailureAsync()
    {
        try
        {
            await DisposeAsync();
        }
        catch (Exception ex)
        {
            await TestContext.Error.WriteLineAsync(
                $"Pinned-image fixture cleanup failed after startup failure: {ex.Message}"
            );
        }
    }

    public static CdcConnectorTemplateRequest BuildRequest(
        CdcProvider provider,
        string kafkaBootstrapServers,
        IReadOnlyDictionary<string, string> providerConnectionProperties
    ) =>
        new(
            BuildBinding(provider),
            BuildProviderSetupEvidence(provider),
            new CdcConnectorTemplateDeploymentPolicy(
                kafkaBootstrapServers,
                maxRecordBytes: 67_108_864,
                heartbeatInterval: TimeSpan.FromSeconds(5),
                sqlServerPollInterval: provider == CdcProvider.SqlServer ? TimeSpan.FromSeconds(2) : null
            ),
            new CdcProviderConnectionProperties(provider, providerConnectionProperties),
            CdcKafkaClientSecurityProperties.Empty
        );

    private async Task StartDockerResourcesAsync(CancellationToken cancellationToken)
    {
        await _docker.RunAsync(["network", "create", NetworkName], cancellationToken);
        await StartBrokerAsync(cancellationToken);
        await StartProviderAsync(cancellationToken);
        await StartKafkaConnectAsync(cancellationToken);
    }

    private async Task StartBrokerAsync(CancellationToken cancellationToken)
    {
        await _docker.RunAsync(
            [
                "run",
                "--detach",
                "--name",
                BrokerContainerName,
                "--network",
                NetworkName,
                _settings.BrokerImage,
                "redpanda",
                "start",
                "--overprovisioned",
                "--smp",
                "1",
                "--memory",
                "512M",
                "--reserve-memory",
                "0M",
                "--node-id",
                "0",
                "--check=false",
                "--kafka-addr",
                $"PLAINTEXT://0.0.0.0:9092",
                "--advertise-kafka-addr",
                $"PLAINTEXT://{BrokerContainerName}:9092",
            ],
            cancellationToken
        );
    }

    private async Task StartProviderAsync(CancellationToken cancellationToken)
    {
        if (Provider == CdcProvider.Postgresql)
        {
            await _docker.RunAsync(
                [
                    "run",
                    "--detach",
                    "--name",
                    ProviderContainerName,
                    "--network",
                    NetworkName,
                    "-e",
                    $"POSTGRES_DB={PostgresqlDatabaseName}",
                    "-e",
                    "POSTGRES_USER=postgres",
                    "-e",
                    $"POSTGRES_PASSWORD={ConnectorDatabasePassword}",
                    _settings.ProviderImage,
                    "postgres",
                    "-c",
                    "wal_level=logical",
                    "-c",
                    "max_replication_slots=8",
                    "-c",
                    "max_wal_senders=8",
                ],
                cancellationToken
            );
            await WaitForPostgresqlAsync(cancellationToken);
            return;
        }

        await _docker.RunAsync(
            [
                "run",
                "--detach",
                "--name",
                ProviderContainerName,
                "--network",
                NetworkName,
                "-e",
                "ACCEPT_EULA=Y",
                "-e",
                $"MSSQL_SA_PASSWORD={ConnectorDatabasePassword}",
                "-e",
                "MSSQL_AGENT_ENABLED=true",
                _settings.ProviderImage,
            ],
            cancellationToken
        );
        await WaitForSqlServerAsync(cancellationToken);
    }

    private async Task StartKafkaConnectAsync(CancellationToken cancellationToken)
    {
        await _docker.RunAsync(BuildKafkaConnectRunArguments(), cancellationToken);
    }

    private IReadOnlyList<string> BuildKafkaConnectRunArguments() =>
        [
            "run",
            "--detach",
            "--name",
            ConnectContainerName,
            "--network",
            NetworkName,
            "-p",
            "127.0.0.1::8083",
            "-e",
            $"BOOTSTRAP_SERVERS={BrokerContainerName}:9092",
            "-e",
            $"GROUP_ID={_resourcePrefix}",
            "-e",
            $"CONFIG_STORAGE_TOPIC={_resourcePrefix}.connect.configs",
            "-e",
            $"OFFSET_STORAGE_TOPIC={_resourcePrefix}.connect.offsets",
            "-e",
            $"STATUS_STORAGE_TOPIC={_resourcePrefix}.connect.status",
            "-e",
            "CONFIG_STORAGE_REPLICATION_FACTOR=1",
            "-e",
            "OFFSET_STORAGE_REPLICATION_FACTOR=1",
            "-e",
            "STATUS_STORAGE_REPLICATION_FACTOR=1",
            "-e",
            $"CONNECT_REST_ADVERTISED_HOST_NAME={ConnectContainerName}",
            "-e",
            "OFFSET_FLUSH_INTERVAL_MS=1000",
            "-e",
            ConnectConfigProvidersEnvironmentVariable,
            "-e",
            ConnectConfigProviderEnvClassEnvironmentVariable,
            "-e",
            $"{ConnectorPasswordEnvironmentVariable}={ConnectorDatabasePassword}",
            _settings.ConnectImage,
        ];

    private async Task AssertKafkaConnectWorkerEnvConfigProviderEnabledAsync(
        CancellationToken cancellationToken
    )
    {
        const string script = $$"""
            set -eu
            if [ "$(printenv CONNECT_CONFIG_PROVIDERS)" != "{{EnvConfigProviderName}}" ]; then
              echo "CONNECT_CONFIG_PROVIDERS mismatch" >&2
              exit 1
            fi
            if [ "$(printenv CONNECT_CONFIG_PROVIDERS_ENV_CLASS)" != "{{EnvConfigProviderClass}}" ]; then
              echo "CONNECT_CONFIG_PROVIDERS_ENV_CLASS mismatch" >&2
              exit 1
            fi
            if printenv CONNECT_CONFIG_PROVIDERS_FILE_CLASS >/dev/null 2>&1; then
              echo "CONNECT_CONFIG_PROVIDERS_FILE_CLASS should not be set" >&2
              exit 1
            fi
            """;

        await _docker.RunAsync(["exec", ConnectContainerName, "sh", "-lc", script], cancellationToken);
    }

    private static void AssertRenderedProviderPasswordUsesEnvReference(CdcConnectorTemplateResult rendered)
    {
        string expectedReference = $"${{env:{ConnectorPasswordEnvironmentVariable}}}";

        using var _ = new AssertionScope();
        rendered
            .Config.TryGetValue("database.password", out string? databasePassword)
            .Should()
            .BeTrue("rendered provider connection properties must include the database password reference");
        string.Equals(databasePassword, expectedReference, StringComparison.Ordinal)
            .Should()
            .BeTrue("rendered provider connection properties must keep the externalized password reference");
        rendered
            .Config.Any(property =>
                string.Equals(property.Value, ConnectorDatabasePassword, StringComparison.Ordinal)
            )
            .Should()
            .BeFalse("rendered connector configs must not contain the raw provider password");
    }

    private static bool HasDockerEnvironmentArgument(IReadOnlyList<string> arguments, string environment)
    {
        for (int index = 1; index < arguments.Count; index++)
        {
            if (
                string.Equals(arguments[index - 1], "-e", StringComparison.Ordinal)
                && string.Equals(arguments[index], environment, StringComparison.Ordinal)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasDockerEnvironmentArgumentNamePrefix(
        IReadOnlyList<string> arguments,
        string environmentNamePrefix
    )
    {
        for (int index = 1; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index - 1], "-e", StringComparison.Ordinal))
            {
                continue;
            }

            string environment = arguments[index];
            if (
                environment.StartsWith($"{environmentNamePrefix}=", StringComparison.Ordinal)
                || environment.StartsWith($"{environmentNamePrefix}_", StringComparison.Ordinal)
            )
            {
                return true;
            }
        }

        return false;
    }

    private async Task WaitForPostgresqlAsync(CancellationToken cancellationToken)
    {
        await RetryUntilReadyAsync(
            () =>
                _docker.RunAllowingFailureAsync(
                    [
                        "exec",
                        "-e",
                        $"PGPASSWORD={ConnectorDatabasePassword}",
                        ProviderContainerName,
                        "pg_isready",
                        "-U",
                        "postgres",
                        "-d",
                        PostgresqlDatabaseName,
                    ],
                    cancellationToken
                ),
            cancellationToken
        );
    }

    private async Task WaitForSqlServerAsync(CancellationToken cancellationToken)
    {
        await RetryUntilReadyAsync(
            async () =>
            {
                DockerCommandResult result = await _docker.RunAllowingFailureAsync(
                    [
                        "exec",
                        ProviderContainerName,
                        "sh",
                        "-lc",
                        $"""
                        for sqlcmd in /opt/mssql-tools18/bin/sqlcmd /opt/mssql-tools/bin/sqlcmd sqlcmd; do
                          if command -v "$sqlcmd" >/dev/null 2>&1 || test -x "$sqlcmd"; then
                            "$sqlcmd" -C -S localhost -U sa -P '{ConnectorDatabasePassword}' -Q 'SELECT 1' >/dev/null
                            exit $?
                          fi
                        done
                        exit 127
                        """,
                    ],
                    cancellationToken
                );

                return result;
            },
            cancellationToken
        );
    }

    private async Task<Uri> ReadMappedConnectBaseUriAsync(CancellationToken cancellationToken)
    {
        DockerCommandResult result = await _docker.RunAsync(
            ["port", ConnectContainerName, "8083/tcp"],
            cancellationToken
        );
        return ParseMappedConnectBaseUri(ConnectContainerName, result.StandardOutput);
    }

    internal static Uri ParseMappedConnectBaseUri(string connectContainerName, string dockerPortOutput)
    {
        string[] mappedPortLines = dockerPortOutput.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        if (mappedPortLines.Length == 0)
        {
            throw InvalidMappedPortOutput(
                connectContainerName,
                dockerPortOutput,
                "expected one mapped host port line but Docker returned no output"
            );
        }

        string mappedPortLine = mappedPortLines[0];
        int delimiterIndex = mappedPortLine.LastIndexOf(':');
        if (delimiterIndex < 0)
        {
            throw InvalidMappedPortOutput(
                connectContainerName,
                dockerPortOutput,
                "expected mapped port line to contain a ':' delimiter"
            );
        }

        string port = mappedPortLine[(delimiterIndex + 1)..];
        if (
            string.IsNullOrWhiteSpace(port)
            || !int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int mappedPort)
            || mappedPort <= 0
            || mappedPort > ushort.MaxValue
        )
        {
            throw InvalidMappedPortOutput(
                connectContainerName,
                dockerPortOutput,
                "expected mapped port line to end with a non-empty numeric TCP port"
            );
        }

        return new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{mappedPort}"));
    }

    private static InvalidOperationException InvalidMappedPortOutput(
        string connectContainerName,
        string dockerPortOutput,
        string reason
    ) =>
        new(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Invalid docker port output for Kafka Connect container '{connectContainerName}': {reason}. Docker output: {FormatDockerOutputForDiagnostic(dockerPortOutput)}"
            )
        );

    private static string FormatDockerOutputForDiagnostic(string dockerOutput)
    {
        string sanitized = DockerCommandResult.Sanitize(dockerOutput).Trim();
        if (string.IsNullOrEmpty(sanitized))
        {
            return "<empty>";
        }

        return sanitized
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private async Task WaitForKafkaConnectAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(ConnectStartupTimeout);
        string lastError = "none";
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(
                    "/connector-plugins?connectorsOnly=false",
                    cancellationToken
                );
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new InvalidOperationException(
            $"Kafka Connect REST API did not become ready. Last error: {lastError}"
        );
    }

    private async Task WaitForRegisteredConnectorRunningAsync(
        string connectorName,
        CancellationToken cancellationToken
    )
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(ConnectorRunningTimeout);
        string lastStatus = "not requested";
        while (DateTimeOffset.UtcNow < deadline)
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(
                $"/connectors/{Uri.EscapeDataString(connectorName)}/status",
                cancellationToken
            );
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            lastStatus = SanitizeForAssertion(responseBody);

            if (response.IsSuccessStatusCode)
            {
                if (ConnectorStatusIsRunning(responseBody))
                {
                    return;
                }

                ConnectorStatusHasFailure(responseBody)
                    .Should()
                    .BeFalse($"Kafka Connect task failed before reaching RUNNING: {lastStatus}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        Assert.Fail($"Kafka Connect task did not reach RUNNING. Last status: {lastStatus}");
    }

    private async Task<long> WaitForProviderHeartbeatSequenceGreaterThanAsync(
        long startingHeartbeatSequence,
        CancellationToken cancellationToken
    )
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(ProviderHeartbeatTimeout);
        long observedHeartbeatSequence = startingHeartbeatSequence;
        while (DateTimeOffset.UtcNow < deadline)
        {
            observedHeartbeatSequence = await ReadProviderHeartbeatSequenceAsync(cancellationToken);
            if (observedHeartbeatSequence > startingHeartbeatSequence)
            {
                return observedHeartbeatSequence;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        Assert.Fail(
            $"Provider heartbeat sequence did not advance from {startingHeartbeatSequence}. Last observed value: {observedHeartbeatSequence}."
        );
        return observedHeartbeatSequence;
    }

    private async Task<CdcConnectorSourceOffsetSnapshot> WaitForCommittedSourceOffsetProgressAsync(
        CdcConnectorTemplateRequest request,
        CdcConnectorSourceOffsetSnapshot? startingOffset,
        CancellationToken cancellationToken
    )
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(OffsetCommitTimeout);
        string? lastObservedOffset = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await WaitForRegisteredConnectorRunningAsync(request.ConnectorName.Value, cancellationToken);

            CdcConnectorSourceOffsetSnapshot? observedOffset = await TryReadCommittedSourceOffsetAsync(
                request,
                cancellationToken
            );
            if (
                observedOffset is not null
                && (
                    startingOffset is null
                    || !string.Equals(
                        observedOffset.CanonicalOffsetJson,
                        startingOffset.CanonicalOffsetJson,
                        StringComparison.Ordinal
                    )
                )
            )
            {
                return observedOffset;
            }

            lastObservedOffset = observedOffset?.CanonicalOffsetJson;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        string starting = startingOffset is null
            ? "<none>"
            : SanitizeForAssertion(startingOffset.CanonicalOffsetJson);
        string observed = lastObservedOffset is null ? "<none>" : SanitizeForAssertion(lastObservedOffset);
        Assert.Fail(
            $"Kafka Connect committed source offset did not progress. Starting offset: {starting}. Last observed offset: {observed}."
        );
        throw new InvalidOperationException("Kafka Connect committed source offset did not progress.");
    }

    private async Task<CdcConnectorSourceOffsetSnapshot> WaitForRetainedCommittedSourceOffsetAsync(
        CdcConnectorTemplateRequest request,
        CdcConnectorSourceOffsetSnapshot expectedOffset,
        CancellationToken cancellationToken
    )
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(OffsetCommitTimeout);
        string? lastObservedOffset = null;
        string lastRetentionFailure =
            "no committed source offset was observed for the rendered source partition";
        while (DateTimeOffset.UtcNow < deadline)
        {
            await WaitForRegisteredConnectorRunningAsync(request.ConnectorName.Value, cancellationToken);

            CdcConnectorSourceOffsetSnapshot? observedOffset = await TryReadCommittedSourceOffsetAsync(
                request,
                cancellationToken
            );
            if (observedOffset is not null)
            {
                int comparison = observedOffset.ProviderPosition.CompareTo(expectedOffset.ProviderPosition);
                if (comparison >= 0)
                {
                    return observedOffset;
                }

                lastRetentionFailure =
                    "last observed provider position was older than the pre-restart provider position";
            }

            lastObservedOffset = observedOffset?.CanonicalOffsetJson;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        string expected = SanitizeForAssertion(expectedOffset.CanonicalOffsetJson);
        string observed = lastObservedOffset is null ? "<none>" : SanitizeForAssertion(lastObservedOffset);
        Assert.Fail(
            $"Kafka Connect did not retain or advance from the pre-restart committed source offset. Minimum offset: {expected}. Last observed offset: {observed}. Last retention check: {lastRetentionFailure}."
        );
        throw new InvalidOperationException(
            "Kafka Connect did not retain or advance from the pre-restart committed source offset."
        );
    }

    private async Task CreateMinimalTopicsAsync(
        CdcConnectorTemplateRequest request,
        CancellationToken cancellationToken
    )
    {
        List<string> topics = [request.PublicTopicName, request.ProgressTopicName];
        if (request.SchemaHistoryTopicName is not null)
        {
            topics.Add(request.SchemaHistoryTopicName);
        }

        await _docker.RunAsync(
            [
                "exec",
                BrokerContainerName,
                "rpk",
                "topic",
                "create",
                "--if-not-exists",
                .. topics,
                "--brokers",
                $"{BrokerContainerName}:9092",
            ],
            cancellationToken
        );
    }

    private async Task CreateMinimalProviderObjectsAsync(CancellationToken cancellationToken)
    {
        if (Provider == CdcProvider.Postgresql)
        {
            await CreateMinimalPostgresqlObjectsAsync(cancellationToken);
            return;
        }

        await CreateMinimalSqlServerObjectsAsync(cancellationToken);
    }

    private async Task CreateMinimalPostgresqlObjectsAsync(CancellationToken cancellationToken)
    {
        string sql = BuildMinimalPostgresqlObjectsSql();

        await _docker.RunAsync(
            [
                "exec",
                "-e",
                $"PGPASSWORD={ConnectorDatabasePassword}",
                ProviderContainerName,
                "psql",
                "-v",
                "ON_ERROR_STOP=1",
                "-U",
                "postgres",
                "-d",
                PostgresqlDatabaseName,
                "-c",
                sql,
            ],
            cancellationToken
        );

        await _docker.RunAsync(
            [
                "exec",
                "-e",
                $"PGPASSWORD={ConnectorDatabasePassword}",
                ProviderContainerName,
                "psql",
                "-v",
                "ON_ERROR_STOP=1",
                "-U",
                "postgres",
                "-d",
                PostgresqlDatabaseName,
                "-c",
                BuildPostgresqlReplicationSlotSql(),
            ],
            cancellationToken
        );

        await AssertPostgresqlPublicationMatchesProviderSetupEvidenceAsync(cancellationToken);
    }

    private static string BuildMinimalPostgresqlObjectsSql() =>
        $$"""
            CREATE SCHEMA IF NOT EXISTS "dms";
            CREATE TABLE IF NOT EXISTS "dms"."DocumentCache" ("DocumentUuid" text NOT NULL PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS "dms"."Document" ("DocumentUuid" text NOT NULL PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS "dms"."CdcHeartbeat"
            (
                "HeartbeatId" smallint NOT NULL PRIMARY KEY,
                "HeartbeatSequence" bigint NOT NULL,
                "HeartbeatAt" timestamp with time zone NOT NULL,
                CONSTRAINT "CK_CdcHeartbeat_Singleton" CHECK ("HeartbeatId" = 1),
                CONSTRAINT "CK_CdcHeartbeat_Sequence" CHECK ("HeartbeatSequence" >= 0)
            );
            INSERT INTO "dms"."CdcHeartbeat" ("HeartbeatId", "HeartbeatSequence", "HeartbeatAt")
            VALUES (1, 0, now())
            ON CONFLICT ("HeartbeatId") DO NOTHING;
            ALTER TABLE "dms"."DocumentCache" REPLICA IDENTITY FULL;
            ALTER TABLE "dms"."Document" REPLICA IDENTITY FULL;
            ALTER TABLE "dms"."CdcHeartbeat" REPLICA IDENTITY FULL;
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = '{{PostgresqlPublicationName}}') THEN
                    CREATE PUBLICATION {{PostgresqlPublicationName}}
                    FOR TABLE "dms"."DocumentCache", "dms"."Document", "dms"."CdcHeartbeat"
                    WITH (publish = 'insert, update, delete');
                END IF;
            END
            $$;
            """;

    private static string BuildPostgresqlReplicationSlotSql() =>
        $$"""
            SELECT
                CASE
                    WHEN NOT EXISTS (
                        SELECT 1 FROM pg_replication_slots WHERE slot_name = '{{PostgresqlReplicationSlotName}}'
                    )
                    THEN pg_create_logical_replication_slot('{{PostgresqlReplicationSlotName}}', 'pgoutput')::text
                    ELSE NULL
                END;
            """;

    private async Task AssertPostgresqlPublicationMatchesProviderSetupEvidenceAsync(
        CancellationToken cancellationToken
    )
    {
        string publicationPropertiesOutput = await ReadPostgresqlScalarAsync(
            $$"""
            SELECT
                publication.pubinsert::text
                || '|' || publication.pubupdate::text
                || '|' || publication.pubdelete::text
                || '|' || publication.pubtruncate::text
                || '|' || publication.puballtables::text
            FROM pg_catalog.pg_publication publication
            WHERE publication.pubname = '{{PostgresqlPublicationName}}';
            """,
            cancellationToken
        );
        string[] publicationPropertyRows = publicationPropertiesOutput.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        publicationPropertyRows.Should().HaveCount(1, "the fixture publication must exist");

        PostgresqlPublicationMetadata metadata = ParsePostgresqlPublicationMetadata(
            publicationPropertyRows[0]
        );

        string publicationTablesOutput = await ReadPostgresqlScalarAsync(
            $$"""
            SELECT namespace_info.nspname || '.' || table_info.relname
            FROM pg_catalog.pg_publication_rel publication_table
            INNER JOIN pg_catalog.pg_publication publication
                ON publication.oid = publication_table.prpubid
            INNER JOIN pg_catalog.pg_class table_info
                ON table_info.oid = publication_table.prrelid
            INNER JOIN pg_catalog.pg_namespace namespace_info
                ON namespace_info.oid = table_info.relnamespace
            WHERE publication.pubname = '{{PostgresqlPublicationName}}'
            ORDER BY namespace_info.nspname, table_info.relname;
            """,
            cancellationToken
        );
        string[] publishedTables = publicationTablesOutput.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        CdcProviderArtifactObservation publication = BuildArtifactInventory(CdcProvider.Postgresql)
            .Single(artifact => artifact.ArtifactKind == CdcProviderArtifactKind.PostgresqlPublication);

        using var _ = new AssertionScope();
        metadata.PublishesInsert.Should().BeTrue();
        metadata.PublishesUpdate.Should().BeTrue();
        metadata.PublishesDelete.Should().BeTrue();
        metadata.PublishesTruncate.Should().BeFalse();
        metadata.PublishesAllTables.Should().BeFalse();
        publishedTables.Should().Equal("dms.CdcHeartbeat", "dms.Document", "dms.DocumentCache");
        publishedTables.Should().NotContain("dms.DocumentProjectionWork");

        publication.SafeObservedValues["tables"].Should().Be(string.Join(",", publishedTables));
        publication.SafeObservedValues["expected_tables"].Should().Be(PostgresqlExpectedSourceTables);
        publication
            .SafeObservedValues["publish"]
            .Should()
            .Be($"{metadata.PublishesInsert},{metadata.PublishesUpdate},{metadata.PublishesDelete}");
        publication
            .SafeObservedValues["publishes_truncate"]
            .Should()
            .Be(metadata.PublishesTruncate.ToString());
        publication
            .SafeObservedValues["publishes_all_tables"]
            .Should()
            .Be(metadata.PublishesAllTables.ToString());
    }

    private static PostgresqlPublicationMetadata ParsePostgresqlPublicationMetadata(string value)
    {
        string[] parts = value.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length != 5)
        {
            throw new InvalidOperationException(
                $"Unexpected PostgreSQL publication metadata shape: {SanitizeForAssertion(value)}"
            );
        }

        return new PostgresqlPublicationMetadata(
            PublishesInsert: ParsePostgresqlBool(parts[0]),
            PublishesUpdate: ParsePostgresqlBool(parts[1]),
            PublishesDelete: ParsePostgresqlBool(parts[2]),
            PublishesTruncate: ParsePostgresqlBool(parts[3]),
            PublishesAllTables: ParsePostgresqlBool(parts[4])
        );
    }

    private static bool ParsePostgresqlBool(string value) =>
        value switch
        {
            "true" or "t" => true,
            "false" or "f" => false,
            _ => throw new InvalidOperationException(
                $"Unexpected PostgreSQL boolean metadata value: {SanitizeForAssertion(value)}"
            ),
        };

    private async Task CreateMinimalSqlServerObjectsAsync(CancellationToken cancellationToken)
    {
        string createSourceTablesSql = string.Join(
            Environment.NewLine,
            SqlServerCaptureInstances.Select(CreateSqlServerSourceTableSql)
        );
        string enableCaptureInstancesSql = string.Join(
            Environment.NewLine,
            SqlServerCaptureInstances.Select(EnableSqlServerCaptureInstanceSql)
        );

        string sql = $$"""
            IF DB_ID(N'{{SqlServerDatabaseName}}') IS NULL
                CREATE DATABASE [{{SqlServerDatabaseName}}];
            ALTER DATABASE [{{SqlServerDatabaseName}}] SET ALLOW_SNAPSHOT_ISOLATION ON;
            GO
            USE [{{SqlServerDatabaseName}}];
            IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'dms')
                EXEC(N'CREATE SCHEMA [dms]');
            {{createSourceTablesSql}}
            IF NOT EXISTS (SELECT 1 FROM [dms].[CdcHeartbeat] WHERE [HeartbeatId] = 1)
                INSERT INTO [dms].[CdcHeartbeat] ([HeartbeatId], [HeartbeatSequence], [HeartbeatAt])
                VALUES (1, 0, sysutcdatetime());
            IF (SELECT is_cdc_enabled FROM sys.databases WHERE name = DB_NAME()) = 0
                EXEC sys.sp_cdc_enable_db;
            IF DATABASE_PRINCIPAL_ID(N'{{SqlServerLiteralValue(SqlServerGatingRoleName)}}') IS NULL
                EXEC(N'CREATE ROLE {{SqlServerBracketIdentifier(SqlServerGatingRoleName)}}');
            {{enableCaptureInstancesSql}}
            """;

        await _docker.RunAsync(
            [
                "exec",
                ProviderContainerName,
                "sh",
                "-lc",
                $"""
                for sqlcmd in /opt/mssql-tools18/bin/sqlcmd /opt/mssql-tools/bin/sqlcmd sqlcmd; do
                  if command -v "$sqlcmd" >/dev/null 2>&1 || test -x "$sqlcmd"; then
                    cat >/tmp/cdc-template-provider-objects.sql <<'SQL'
                {sql}
                SQL
                    "$sqlcmd" -C -S localhost -U sa -P '{ConnectorDatabasePassword}' -b -i /tmp/cdc-template-provider-objects.sql
                    exit $?
                  fi
                done
                exit 127
                """,
            ],
            cancellationToken
        );

        await AssertSqlServerCaptureInstancesMatchProviderSetupEvidenceAsync(cancellationToken);
    }

    private static string CreateSqlServerSourceTableSql(SqlServerCaptureInstanceDefinition definition)
    {
        string sourceTableName = SqlServerLiteralValue(definition.SourceTableName);
        string columnDefinitions = string.Join(
            $",{Environment.NewLine}",
            definition.CapturedColumns.Select(column =>
                $"                    {SqlServerBracketIdentifier(column.ColumnName)} {column.ProviderDataType} {(column.IsNullable ? "NULL" : "NOT NULL")}"
            )
        );
        string constraintDefinitions = string.Join(
            $",{Environment.NewLine}",
            new[]
            {
                $"                    CONSTRAINT {SqlServerBracketIdentifier(definition.SourcePrimaryKeyName)} PRIMARY KEY CLUSTERED ({SqlServerBracketIdentifier(definition.PrimaryKeyColumnName)})",
            }.Concat(
                definition.AdditionalTableConstraints.Select(constraint =>
                    $"                    {constraint}"
                )
            )
        );

        return $$"""
            IF OBJECT_ID(N'[dms].[{{sourceTableName}}]', N'U') IS NULL
                CREATE TABLE [dms].[{{sourceTableName}}]
                (
            {{columnDefinitions}},
            {{constraintDefinitions}}
                );
            """;
    }

    private static string EnableSqlServerCaptureInstanceSql(SqlServerCaptureInstanceDefinition definition)
    {
        string captureInstanceName = SqlServerLiteralValue(definition.CaptureInstanceName.Value);
        string sourceTableName = SqlServerLiteralValue(definition.SourceTableName);
        string gatingRoleName = SqlServerLiteralValue(SqlServerGatingRoleName);
        string capturedColumnList = SqlServerLiteralValue(definition.CapturedColumnList);

        return $$"""
            IF NOT EXISTS (
                SELECT 1 FROM cdc.change_tables WHERE capture_instance = N'{{captureInstanceName}}'
            )
                EXEC sys.sp_cdc_enable_table
                    @source_schema = N'dms',
                    @source_name = N'{{sourceTableName}}',
                    @capture_instance = N'{{captureInstanceName}}',
                    @supports_net_changes = 0,
                    @role_name = N'{{gatingRoleName}}',
                    @index_name = NULL,
                    @captured_column_list = N'{{capturedColumnList}}',
                    @filegroup_name = NULL,
                    @allow_partition_switch = 0;
            """;
    }

    private async Task AssertSqlServerCaptureInstancesMatchProviderSetupEvidenceAsync(
        CancellationToken cancellationToken
    )
    {
        string sourceNameList = string.Join(
            ", ",
            SqlServerCaptureInstances.Select(definition =>
                $"N'{SqlServerLiteralValue(definition.SourceTableName)}'"
            )
        );
        string output = await ReadSqlServerScalarAsync(
            $$"""
            WITH captured_columns AS (
                SELECT
                    captured_column.object_id,
                    STRING_AGG(CONVERT(nvarchar(max), captured_column.column_name), N',') WITHIN GROUP (ORDER BY captured_column.column_ordinal) AS captured_columns
                FROM cdc.captured_columns AS captured_column
                GROUP BY captured_column.object_id
            )
            SELECT
                change_table.capture_instance
                + N'|' + SCHEMA_NAME(source_table.schema_id)
                + N'|' + source_table.name
                + N'|' + COALESCE(change_table.role_name, N'')
                + N'|' + CASE WHEN change_table.supports_net_changes = 1 THEN N'True' ELSE N'False' END
                + N'|' + COALESCE(change_table.index_name, N'')
                + N'|' + COALESCE(change_table.filegroup_name, N'')
                + N'|' + CASE WHEN change_table.partition_switch = 1 THEN N'True' ELSE N'False' END
                + N'|' + CASE WHEN EXISTS (
                    SELECT 1
                    FROM sys.indexes AS source_index
                    INNER JOIN sys.partition_schemes AS partition_scheme
                        ON partition_scheme.data_space_id = source_index.data_space_id
                    WHERE source_index.object_id = source_table.object_id
                    AND source_index.index_id IN (0, 1)
                ) THEN N'True' ELSE N'False' END
                + N'|' + COALESCE(captured_columns.captured_columns, N'')
            FROM cdc.change_tables AS change_table
            INNER JOIN sys.tables AS source_table
                ON source_table.object_id = change_table.source_object_id
            LEFT JOIN captured_columns
                ON captured_columns.object_id = change_table.object_id
            WHERE source_table.schema_id = SCHEMA_ID(N'dms')
                AND source_table.name IN ({{sourceNameList}})
            ORDER BY change_table.capture_instance;
            """,
            cancellationToken
        );

        Dictionary<string, SqlServerCaptureInstanceMetadata> actualCaptureInstances = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseSqlServerCaptureInstanceMetadata)
            .ToDictionary(metadata => metadata.CaptureInstanceName, StringComparer.Ordinal);
        Dictionary<string, CdcProviderArtifactObservation> advertisedCaptureInstances =
            BuildArtifactInventory(CdcProvider.SqlServer)
                .Where(artifact => artifact.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance)
                .ToDictionary(artifact => artifact.SafeArtifactName.Value, StringComparer.Ordinal);

        using var _ = new AssertionScope();
        actualCaptureInstances
            .Keys.Should()
            .BeEquivalentTo(
                SqlServerCaptureInstances.Select(definition => definition.CaptureInstanceName.Value)
            );
        advertisedCaptureInstances.Keys.Should().BeEquivalentTo(actualCaptureInstances.Keys);

        foreach (SqlServerCaptureInstanceDefinition definition in SqlServerCaptureInstances)
        {
            SqlServerCaptureInstanceMetadata metadata = actualCaptureInstances[
                definition.CaptureInstanceName.Value
            ];
            CdcProviderArtifactObservation artifact = advertisedCaptureInstances[
                definition.CaptureInstanceName.Value
            ];

            metadata.SourceSchema.Should().Be("dms");
            metadata.SourceTableName.Should().Be(definition.SourceTableName);
            metadata.RoleName.Should().Be(SqlServerGatingRoleName);
            metadata.SupportsNetChanges.Should().BeFalse();
            metadata
                .IndexName.Should()
                .BeOneOf(
                    [string.Empty, definition.SourcePrimaryKeyName],
                    "the fixture requests @index_name = NULL, so SQL Server may expose no index or the selected source primary key"
                );
            metadata.FilegroupName.Should().BeEmpty();
            metadata
                .SourceIsPartitioned.Should()
                .BeFalse("the fixture source tables are deliberately not partitioned");
            (metadata.SourceIsPartitioned && metadata.PartitionSwitch)
                .Should()
                .BeFalse("partition switching must not be enabled for partitioned fixture sources");
            metadata
                .CapturedColumns.Should()
                .Equal(definition.CapturedColumns.Select(column => column.ColumnName));

            artifact.SafeObservedValues["capture_instance"].Should().Be(metadata.CaptureInstanceName);
            artifact.SafeObservedValues["source_table_kind"].Should().Be(definition.SourceTableKindToken);
            artifact.SafeObservedValues["source_object"].Should().Be($"dms.{metadata.SourceTableName}");
            artifact.SafeObservedValues["role_name"].Should().Be(metadata.RoleName);
            artifact
                .SafeObservedValues["supports_net_changes"]
                .Should()
                .Be(metadata.SupportsNetChanges.ToString());
            artifact
                .SafeObservedValues["captured_columns"]
                .Should()
                .Be(string.Join(",", metadata.CapturedColumns));
        }
    }

    private static SqlServerCaptureInstanceMetadata ParseSqlServerCaptureInstanceMetadata(string value)
    {
        string[] parts = value.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length != 10)
        {
            throw new InvalidOperationException(
                $"Unexpected SQL Server CDC capture metadata shape: {SanitizeForAssertion(value)}"
            );
        }

        string[] capturedColumns = parts[9]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new SqlServerCaptureInstanceMetadata(
            CaptureInstanceName: parts[0],
            SourceSchema: parts[1],
            SourceTableName: parts[2],
            RoleName: parts[3],
            SupportsNetChanges: bool.Parse(parts[4]),
            IndexName: parts[5],
            FilegroupName: parts[6],
            PartitionSwitch: bool.Parse(parts[7]),
            SourceIsPartitioned: bool.Parse(parts[8]),
            CapturedColumns: capturedColumns
        );
    }

    private async Task<long> ReadProviderHeartbeatSequenceAsync(CancellationToken cancellationToken)
    {
        string output =
            Provider == CdcProvider.Postgresql
                ? await ReadPostgresqlScalarAsync(
                    """
                    SELECT "HeartbeatSequence" FROM "dms"."CdcHeartbeat" WHERE "HeartbeatId" = 1;
                    """,
                    cancellationToken
                )
                : await ReadSqlServerScalarAsync(
                    """
                    SELECT [HeartbeatSequence] FROM [dms].[CdcHeartbeat] WHERE [HeartbeatId] = 1;
                    """,
                    cancellationToken
                );

        string value =
            output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault()
            ?? string.Empty;

        value.Should().NotBeNullOrWhiteSpace("the provider heartbeat singleton should contain one row");

        return long.Parse(value, CultureInfo.InvariantCulture);
    }

    private async Task<string> ReadPostgresqlScalarAsync(string sql, CancellationToken cancellationToken)
    {
        DockerCommandResult result = await _docker.RunAsync(
            [
                "exec",
                "-e",
                $"PGPASSWORD={ConnectorDatabasePassword}",
                ProviderContainerName,
                "psql",
                "-v",
                "ON_ERROR_STOP=1",
                "-Atq",
                "-U",
                "postgres",
                "-d",
                PostgresqlDatabaseName,
                "-c",
                sql,
            ],
            cancellationToken
        );

        return result.StandardOutput;
    }

    private async Task<string> ReadSqlServerScalarAsync(string sql, CancellationToken cancellationToken)
    {
        DockerCommandResult result = await _docker.RunAsync(
            [
                "exec",
                ProviderContainerName,
                "sh",
                "-lc",
                $"""
                for sqlcmd in /opt/mssql-tools18/bin/sqlcmd /opt/mssql-tools/bin/sqlcmd sqlcmd; do
                  if command -v "$sqlcmd" >/dev/null 2>&1 || test -x "$sqlcmd"; then
                    cat >/tmp/cdc-template-scalar.sql <<'SQL'
                SET NOCOUNT ON;
                {sql}
                SQL
                    "$sqlcmd" -C -S localhost -d '{SqlServerDatabaseName}' -U sa -P '{ConnectorDatabasePassword}' -b -h -1 -W -i /tmp/cdc-template-scalar.sql
                    exit $?
                  fi
                done
                exit 127
                """,
            ],
            cancellationToken
        );

        return result.StandardOutput;
    }

    private async Task<string[]> ReadConnectorPluginClassesAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(
            "/connector-plugins?connectorsOnly=false",
            cancellationToken
        );
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        response
            .IsSuccessStatusCode.Should()
            .BeTrue($"Kafka Connect plugin discovery failed: {SanitizeForAssertion(responseBody)}");

        using JsonDocument document = JsonDocument.Parse(responseBody);
        return document
            .RootElement.EnumerateArray()
            .Select(plugin => plugin.GetProperty("class").GetString())
            .Where(pluginClass => pluginClass is not null)
            .Select(pluginClass => pluginClass!)
            .ToArray();
    }

    private async Task RunJavaClassProbeAsync(
        IReadOnlyList<string> classNames,
        CancellationToken cancellationToken
    )
    {
        string classNameArguments = string.Join(" ", classNames.Select(SingleQuote));
        string script = $$"""
            set -eu
            class_path="$(find /kafka /opt/kafka /usr/share/java /usr/share/confluent-hub-components /debezium -name '*.jar' 2>/dev/null | tr '\n' ':')"
            test -n "${class_path}"
            cat >/tmp/CdcTemplateClassProbe.java <<'JAVA'
            public class CdcTemplateClassProbe {
                public static void main(String[] args) throws Exception {
                    for (String className : args) {
                        Class.forName(className);
                    }
                }
            }
            JAVA
            java -cp "${class_path}" /tmp/CdcTemplateClassProbe.java {{classNameArguments}}
            """;

        await _docker.RunAsync(["exec", ConnectContainerName, "sh", "-lc", script], cancellationToken);
    }

    private static async Task RetryUntilReadyAsync(
        Func<Task<DockerCommandResult>> probe,
        CancellationToken cancellationToken
    )
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(90));
        while (DateTimeOffset.UtcNow < deadline)
        {
            DockerCommandResult result = await probe();
            if (result.ExitCode == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new InvalidOperationException("Container prerequisite did not become ready.");
    }

    private static IReadOnlyList<string> ExtractValidationErrors(string responseBody)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("configs", out JsonElement configs))
        {
            return ["Kafka Connect validation response did not include configs."];
        }

        List<string> errors = [];
        foreach (JsonElement config in configs.EnumerateArray())
        {
            string name = config.GetProperty("definition").GetProperty("name").GetString() ?? "unknown";
            JsonElement value = config.GetProperty("value");
            if (!value.TryGetProperty("errors", out JsonElement configErrors))
            {
                continue;
            }

            foreach (JsonElement error in configErrors.EnumerateArray())
            {
                string? message = error.GetString();
                if (!string.IsNullOrWhiteSpace(message))
                {
                    errors.Add($"{name}: {SanitizeForAssertion(message)}");
                }
            }
        }

        return errors;
    }

    private async Task<CdcConnectorSourceOffsetSnapshot?> TryReadCommittedSourceOffsetAsync(
        CdcConnectorTemplateRequest request,
        CancellationToken cancellationToken
    )
    {
        string connectorName = request.ConnectorName.Value;
        using HttpResponseMessage response = await _httpClient.GetAsync(
            $"/connectors/{Uri.EscapeDataString(connectorName)}/offsets",
            cancellationToken
        );
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response
            .IsSuccessStatusCode.Should()
            .BeTrue($"Kafka Connect offset read failed: {SanitizeForAssertion(responseBody)}");

        using JsonDocument document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("offsets", out JsonElement offsets))
        {
            Assert.Fail("Kafka Connect offset read response did not include an offsets array.");
            return null;
        }

        List<CdcConnectorSourceOffsetSnapshot> matchingOffsets = [];
        foreach (JsonElement offsetDocument in offsets.EnumerateArray())
        {
            CdcConnectorProviderOffsetPosition? providerPosition = null;
            if (
                !offsetDocument.TryGetProperty("partition", out JsonElement partition)
                || !SourcePartitionMatches(request, partition)
                || !offsetDocument.TryGetProperty("offset", out JsonElement offset)
                || (providerPosition = ReadCommittedProviderOffsetPosition(request.Provider, offset)) is null
            )
            {
                continue;
            }

            matchingOffsets.Add(
                new CdcConnectorSourceOffsetSnapshot(
                    CanonicalizeJson(offset),
                    BuildSourcePartitionEvidence(partition),
                    providerPosition
                )
            );
        }

        matchingOffsets
            .Should()
            .HaveCountLessThanOrEqualTo(
                1,
                "there should be exactly one committed source offset partition for the rendered connector"
            );

        return matchingOffsets.SingleOrDefault();
    }

    private static bool ConnectorStatusIsRunning(string responseBody)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;
        if (
            !root.TryGetProperty("connector", out JsonElement connector)
            || !HasState(connector, "RUNNING")
            || !root.TryGetProperty("tasks", out JsonElement tasks)
        )
        {
            return false;
        }

        JsonElement[] taskArray = tasks.EnumerateArray().ToArray();
        return taskArray.Length == 1 && Array.TrueForAll(taskArray, task => HasState(task, "RUNNING"));
    }

    private static bool ConnectorStatusHasFailure(string responseBody)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("connector", out JsonElement connector) && HasState(connector, "FAILED"))
        {
            return true;
        }

        if (!root.TryGetProperty("tasks", out JsonElement tasks))
        {
            return false;
        }

        return tasks.EnumerateArray().Any(task => HasState(task, "FAILED"));
    }

    private static bool HasState(JsonElement stateContainer, string expectedState) =>
        stateContainer.TryGetProperty("state", out JsonElement state)
        && state.ValueKind == JsonValueKind.String
        && string.Equals(state.GetString(), expectedState, StringComparison.Ordinal);

    private static bool SourcePartitionMatches(CdcConnectorTemplateRequest request, JsonElement partition)
    {
        if (
            partition.ValueKind != JsonValueKind.Object
            || !JsonStringPropertyEquals(partition, "server", request.ConnectorName.Value)
        )
        {
            return false;
        }

        return request.Provider != CdcProvider.SqlServer
            || JsonStringPropertyEquals(
                partition,
                "database",
                request.ProviderConnectionProperties.Properties["database.names"]
            );
    }

    internal static bool CommittedSourceOffsetRetainsOrAdvances(
        CdcProvider provider,
        string minimumOffsetJson,
        string observedOffsetJson
    )
    {
        CdcConnectorProviderOffsetPosition? minimumPosition = ReadCommittedProviderOffsetPosition(
            provider,
            minimumOffsetJson
        );
        CdcConnectorProviderOffsetPosition? observedPosition = ReadCommittedProviderOffsetPosition(
            provider,
            observedOffsetJson
        );

        return minimumPosition is not null
            && observedPosition is not null
            && observedPosition.CompareTo(minimumPosition) >= 0;
    }

    private static CdcConnectorProviderOffsetPosition? ReadCommittedProviderOffsetPosition(
        CdcProvider provider,
        string offsetJson
    )
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(offsetJson);
            return ReadCommittedProviderOffsetPosition(provider, document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CdcConnectorProviderOffsetPosition? ReadCommittedProviderOffsetPosition(
        CdcProvider provider,
        JsonElement offset
    )
    {
        if (offset.ValueKind != JsonValueKind.Object || OffsetIsSnapshot(offset))
        {
            return null;
        }

        return provider switch
        {
            CdcProvider.Postgresql => ReadPostgresqlCommittedOffsetPosition(offset),
            CdcProvider.SqlServer => ReadSqlServerCommittedOffsetPosition(offset),
            _ => null,
        };
    }

    private static CdcConnectorProviderOffsetPosition? ReadPostgresqlCommittedOffsetPosition(
        JsonElement offset
    ) =>
        TryReadUInt64JsonProperty(offset, "lsn_proc", out ulong lsnProc)
            ? new PostgresqlConnectorOffsetPosition(lsnProc)
            : null;

    private static CdcConnectorProviderOffsetPosition? ReadSqlServerCommittedOffsetPosition(
        JsonElement offset
    )
    {
        if (
            !TryReadSqlServerLsnJsonProperty(offset, "commit_lsn", out SqlServerConnectorLsn commitLsn)
            || !TryReadSqlServerLsnJsonProperty(offset, "change_lsn", out SqlServerConnectorLsn changeLsn)
            || !TryReadNonNegativeInt64JsonProperty(offset, "event_serial_no", out long eventSerialNo)
        )
        {
            return null;
        }

        return new SqlServerConnectorOffsetPosition(commitLsn, changeLsn, eventSerialNo);
    }

    private static bool OffsetIsSnapshot(JsonElement offset)
    {
        if (!offset.TryGetProperty("snapshot", out JsonElement snapshot))
        {
            return false;
        }

        return snapshot.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => !string.Equals(
                snapshot.GetString(),
                "false",
                StringComparison.OrdinalIgnoreCase
            ),
            _ => true,
        };
    }

    private static bool JsonStringPropertyEquals(
        JsonElement element,
        string propertyName,
        string expectedValue
    ) =>
        element.TryGetProperty(propertyName, out JsonElement property)
        && property.ValueKind == JsonValueKind.String
        && string.Equals(property.GetString(), expectedValue, StringComparison.Ordinal);

    private static bool TryReadUInt64JsonProperty(JsonElement element, string propertyName, out ulong value)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            value = 0;
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            return property.TryGetUInt64(out value);
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            return ulong.TryParse(
                property.GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value
            );
        }

        value = 0;
        return false;
    }

    private static bool TryReadNonNegativeInt64JsonProperty(
        JsonElement element,
        string propertyName,
        out long value
    )
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            value = 0;
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            return property.TryGetInt64(out value) && value >= 0;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            return long.TryParse(
                property.GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value
            );
        }

        value = 0;
        return false;
    }

    private static bool TryReadSqlServerLsnJsonProperty(
        JsonElement element,
        string propertyName,
        out SqlServerConnectorLsn value
    )
    {
        value = default;
        return element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && TryParseSqlServerLsn(property.GetString(), out value);
    }

    private static bool TryParseSqlServerLsn(string? lsn, out SqlServerConnectorLsn value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(lsn))
        {
            return false;
        }

        string[] parts = lsn.Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        if (
            !TryParseSqlServerLsnPart(parts[0], out ulong first)
            || !TryParseSqlServerLsnPart(parts[1], out ulong second)
            || !TryParseSqlServerLsnPart(parts[2], out ulong third)
        )
        {
            return false;
        }

        value = new SqlServerConnectorLsn(first, second, third);
        return true;
    }

    private static bool TryParseSqlServerLsnPart(string part, out ulong value) =>
        ulong.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
        && part.Length > 0;

    private static IReadOnlyDictionary<string, string> ParseStringMap(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document
            .RootElement.EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToDictionary(
                property => property.Name,
                property => property.Value.GetString() ?? string.Empty,
                StringComparer.Ordinal
            );
    }

    private static string CanonicalizeJson(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalJson(writer, element);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (
                    JsonProperty property in element
                        .EnumerateObject()
                        .OrderBy(property => property.Name, StringComparer.Ordinal)
                )
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
        }
    }

    private static string SanitizeForAssertion(string value) =>
        value.Replace(ConnectorDatabasePassword, "[redacted]", StringComparison.Ordinal);

    private static string SingleQuote(string value) => $"'{EscapeSingleQuotedShell(value)}'";

    private static string EscapeSingleQuotedShell(string value) =>
        value.Replace("'", "'\"'\"'", StringComparison.Ordinal);

    private static string SqlServerLiteralValue(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static string SqlServerBracketIdentifier(string value) =>
        $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static CdcConnectorTemplateBindingIdentity BuildBinding(CdcProvider provider) =>
        new(
            provider,
            new CdcSafeName("dms_binding_connector"),
            "edfi.documents",
            bindingGeneration: 7,
            partitionerAlgorithm: "kafka-murmur2-v1",
            BuildProviderArtifactNames(provider),
            CdcConnectorTemplatePinnedImageTestData.SourceFingerprint(provider)
        );

    private static CdcProviderArtifactNames BuildProviderArtifactNames(CdcProvider provider) =>
        provider switch
        {
            CdcProvider.Postgresql => CdcProviderArtifactNames.ForPostgresql(
                new CdcSafeName(PostgresqlPublicationName),
                new CdcSafeName(PostgresqlReplicationSlotName)
            ),
            CdcProvider.SqlServer => CdcProviderArtifactNames.ForSqlServer(
                new CdcSafeName(SqlServerGatingRoleName),
                SqlServerCaptureInstances.ToDictionary(
                    definition => definition.TableKind,
                    definition => definition.CaptureInstanceName
                )
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    private static CdcConnectorProviderSetupEvidence BuildProviderSetupEvidence(CdcProvider provider) =>
        new(bindingGeneration: 7, BuildProviderSetupResult(provider));

    private static CdcConnectorProviderSetupEvidence BuildLiveReadBackProviderSetupEvidence(
        CdcProvider provider
    ) =>
        new(
            bindingGeneration: 7,
            BuildProviderSetupResult(
                provider,
                mode: CdcProviderSetupMode.ValidateOnly,
                outcome: CdcProviderSetupOutcome.ExactMatch
            )
        );

    private static CdcProviderSetupResult BuildProviderSetupResult(
        CdcProvider provider,
        CdcProviderSetupMode mode = CdcProviderSetupMode.InitialCreateOrExactMatch,
        CdcProviderSetupOutcome outcome = CdcProviderSetupOutcome.CreatedOrMatched
    ) =>
        new(
            Provider: provider,
            Mode: mode,
            Outcome: outcome,
            BoundPhysicalSourceFingerprint: CdcConnectorTemplatePinnedImageTestData.SourceFingerprint(
                provider
            ),
            ObservedSourceFingerprint: CdcConnectorTemplatePinnedImageTestData.SourceFingerprint(provider),
            ArtifactInventory: BuildArtifactInventory(provider),
            GrantInventory: [],
            SourceTableInventory: BuildRequiredSourceTableInventory(provider),
            ExpectedMessageKeyColumns: BuildExpectedMessageKeyColumns(),
            HeartbeatActionQuery: new CdcHeartbeatActionQuery(HeartbeatActionQuery(provider), "sha256-safe"),
            ProviderHistoryObservations: [],
            ManifestPayload: null,
            Diagnostics: []
        );

    private static string HeartbeatActionQuery(CdcProvider provider) =>
        provider switch
        {
            CdcProvider.Postgresql =>
                """UPDATE "dms"."CdcHeartbeat" SET "HeartbeatSequence" = "HeartbeatSequence" + 1, "HeartbeatAt" = now() WHERE "HeartbeatId" = 1;""",
            CdcProvider.SqlServer =>
                "UPDATE [dms].[CdcHeartbeat] SET [HeartbeatSequence] = [HeartbeatSequence] + 1, [HeartbeatAt] = sysutcdatetime() WHERE [HeartbeatId] = 1",
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    private static IReadOnlyList<CdcProviderArtifactObservation> BuildArtifactInventory(
        CdcProvider provider
    ) =>
        provider switch
        {
            CdcProvider.Postgresql =>
            [
                new(
                    CdcProviderArtifactKind.PostgresqlPublication,
                    new CdcSafeName(PostgresqlPublicationName),
                    CdcProviderArtifactState.Matched,
                    new Dictionary<string, string>
                    {
                        ["tables"] = PostgresqlObservedPublicationTables,
                        ["expected_tables"] = PostgresqlExpectedSourceTables,
                        ["publish"] = $"{true},{true},{true}",
                        ["publishes_truncate"] = false.ToString(),
                        ["publishes_all_tables"] = false.ToString(),
                        ["tables_in_schema"] = string.Empty,
                        ["publish_via_partition_root"] = false.ToString(),
                        ["row_filters"] = "absent",
                        ["column_lists"] = "absent",
                    }
                ),
                new(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    new CdcSafeName(PostgresqlReplicationSlotName),
                    CdcProviderArtifactState.Matched,
                    new Dictionary<string, string>()
                ),
            ],
            CdcProvider.SqlServer =>
            [
                BuildSqlServerGatingRoleArtifact(),
                .. SqlServerCaptureInstances.Select(BuildSqlServerCaptureInstanceArtifact),
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    private static CdcProviderArtifactObservation BuildSqlServerGatingRoleArtifact() =>
        new(
            CdcProviderArtifactKind.SqlServerGatingRole,
            new CdcSafeName(SqlServerGatingRoleName),
            CdcProviderArtifactState.Matched,
            new Dictionary<string, string>()
        );

    private static CdcProviderArtifactObservation BuildSqlServerCaptureInstanceArtifact(
        SqlServerCaptureInstanceDefinition definition
    ) =>
        new(
            CdcProviderArtifactKind.SqlServerCaptureInstance,
            definition.CaptureInstanceName,
            CdcProviderArtifactState.Matched,
            new Dictionary<string, string>
            {
                ["capture_instance"] = definition.CaptureInstanceName.Value,
                ["source_table_kind"] = definition.SourceTableKindToken,
                ["source_object"] = $"dms.{definition.SourceTableName}",
                ["role_name"] = SqlServerGatingRoleName,
                ["supports_net_changes"] = false.ToString(),
                ["expected_supports_net_changes"] = false.ToString(),
                ["expected_source_index"] = $"none_or_source_primary_key.{definition.SourcePrimaryKeyName}",
                ["expected_filegroup_name"] = "none",
                ["expected_partition_switch"] = "disabled_when_source_partitioned",
                ["captured_columns"] = definition.CapturedColumnCsv,
                ["captured_column_count"] = definition.CapturedColumns.Count.ToString(
                    CultureInfo.InvariantCulture
                ),
            }
        );

    private static IReadOnlyList<CdcSourceTableInventory> BuildRequiredSourceTableInventory(
        CdcProvider provider
    ) =>
        provider == CdcProvider.SqlServer
            ? SqlServerCaptureInstances.Select(BuildSqlServerSourceTableInventory).ToArray()
            :
            [
                BuildSourceTable(
                    provider,
                    CdcSourceTableKind.DocumentCache,
                    "DocumentCache",
                    [BuildColumn(provider, "DocumentUuid")]
                ),
                BuildSourceTable(
                    provider,
                    CdcSourceTableKind.Document,
                    "Document",
                    [BuildColumn(provider, "DocumentUuid")]
                ),
                BuildSourceTable(
                    provider,
                    CdcSourceTableKind.CdcHeartbeat,
                    "CdcHeartbeat",
                    [
                        BuildColumn(provider, "HeartbeatId"),
                        BuildColumn(provider, "HeartbeatSequence", 2),
                        BuildColumn(provider, "HeartbeatAt", 3),
                    ]
                ),
            ];

    private static CdcSourceTableInventory BuildSqlServerSourceTableInventory(
        SqlServerCaptureInstanceDefinition definition
    ) =>
        new(
            definition.TableKind,
            new DbTableName(new DbSchemaName("dms"), definition.SourceTableName),
            $"[dms].[{definition.SourceTableName}]",
            definition
                .CapturedColumns.Select(
                    (column, index) =>
                        new CdcSourceColumnInventory(
                            new DbColumnName(column.ColumnName),
                            SqlServerBracketIdentifier(column.ColumnName),
                            index + 1,
                            column.ProviderDataType,
                            column.IsNullable
                        )
                )
                .ToArray()
        );

    private static CdcSourceTableInventory BuildSourceTable(
        CdcProvider provider,
        CdcSourceTableKind tableKind,
        string tableName,
        IReadOnlyList<CdcSourceColumnInventory> columns
    ) =>
        new(
            tableKind,
            new DbTableName(new DbSchemaName("dms"), tableName),
            provider == CdcProvider.Postgresql ? $"\"dms\".\"{tableName}\"" : $"[dms].[{tableName}]",
            columns
        );

    private static CdcSourceColumnInventory BuildColumn(
        CdcProvider provider,
        string columnName,
        int ordinal = 1
    ) =>
        new(
            new DbColumnName(columnName),
            provider == CdcProvider.Postgresql ? $"\"{columnName}\"" : $"[{columnName}]",
            ordinal,
            provider == CdcProvider.Postgresql ? "text" : "nvarchar(max)",
            IsNullable: false
        );

    private static IReadOnlyList<CdcExpectedMessageKeyColumns> BuildExpectedMessageKeyColumns() =>
        [
            new(CdcSourceTableKind.DocumentCache, [new DbColumnName("DocumentUuid")]),
            new(CdcSourceTableKind.Document, [new DbColumnName("DocumentUuid")]),
        ];

    private static CdcConnectorTemplateSourcePartitionEvidence BuildSourcePartitionEvidence(
        JsonElement partition
    )
    {
        IReadOnlyDictionary<string, string> properties = partition
            .EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToDictionary(
                property => property.Name,
                property =>
                    property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? string.Empty
                        : property.Value.GetRawText(),
                StringComparer.Ordinal
            );

        return new CdcConnectorTemplateSourcePartitionEvidence(properties);
    }

    private sealed record SqlServerCaptureInstanceDefinition(
        CdcSourceTableKind TableKind,
        string SourceTableName,
        CdcSafeName CaptureInstanceName,
        string SourceTableKindToken,
        string PrimaryKeyColumnName,
        IReadOnlyList<SqlServerSourceColumnDefinition> CapturedColumns,
        IReadOnlyList<string>? AdditionalTableConstraintsInput = null
    )
    {
        public IReadOnlyList<string> AdditionalTableConstraints { get; } =
            AdditionalTableConstraintsInput ?? [];

        public string SourcePrimaryKeyName => $"PK_{SourceTableName}";

        public string CapturedColumnList =>
            string.Join(
                ", ",
                CapturedColumns.Select(column => SqlServerBracketIdentifier(column.ColumnName))
            );

        public string CapturedColumnCsv =>
            string.Join(",", CapturedColumns.Select(column => column.ColumnName));
    }

    private sealed record SqlServerSourceColumnDefinition(
        string ColumnName,
        string ProviderDataType,
        bool IsNullable = false
    );

    private sealed record PostgresqlPublicationMetadata(
        bool PublishesInsert,
        bool PublishesUpdate,
        bool PublishesDelete,
        bool PublishesTruncate,
        bool PublishesAllTables
    );

    private sealed record SqlServerCaptureInstanceMetadata(
        string CaptureInstanceName,
        string SourceSchema,
        string SourceTableName,
        string RoleName,
        bool SupportsNetChanges,
        string IndexName,
        string FilegroupName,
        bool PartitionSwitch,
        bool SourceIsPartitioned,
        IReadOnlyList<string> CapturedColumns
    );

    internal sealed record CdcConnectorSourceOffsetSnapshot(
        string CanonicalOffsetJson,
        CdcConnectorTemplateSourcePartitionEvidence SourcePartitionEvidence,
        CdcConnectorProviderOffsetPosition ProviderPosition
    );

    internal abstract record CdcConnectorProviderOffsetPosition(CdcProvider Provider)
    {
        public int CompareTo(CdcConnectorProviderOffsetPosition other)
        {
            if (Provider != other.Provider)
            {
                throw new InvalidOperationException("Cannot compare CDC source offsets across providers.");
            }

            return CompareSameProvider(other);
        }

        protected abstract int CompareSameProvider(CdcConnectorProviderOffsetPosition other);
    }

    private sealed record PostgresqlConnectorOffsetPosition(ulong LsnProc)
        : CdcConnectorProviderOffsetPosition(CdcProvider.Postgresql)
    {
        protected override int CompareSameProvider(CdcConnectorProviderOffsetPosition other) =>
            LsnProc.CompareTo(((PostgresqlConnectorOffsetPosition)other).LsnProc);
    }

    private sealed record SqlServerConnectorOffsetPosition(
        SqlServerConnectorLsn CommitLsn,
        SqlServerConnectorLsn ChangeLsn,
        long EventSerialNo
    ) : CdcConnectorProviderOffsetPosition(CdcProvider.SqlServer)
    {
        protected override int CompareSameProvider(CdcConnectorProviderOffsetPosition other)
        {
            var sqlServerPosition = (SqlServerConnectorOffsetPosition)other;
            int commitLsnComparison = CommitLsn.CompareTo(sqlServerPosition.CommitLsn);
            if (commitLsnComparison != 0)
            {
                return commitLsnComparison;
            }

            int changeLsnComparison = ChangeLsn.CompareTo(sqlServerPosition.ChangeLsn);
            return changeLsnComparison != 0
                ? changeLsnComparison
                : EventSerialNo.CompareTo(sqlServerPosition.EventSerialNo);
        }
    }

    private readonly record struct SqlServerConnectorLsn(ulong First, ulong Second, ulong Third)
        : IComparable<SqlServerConnectorLsn>
    {
        public int CompareTo(SqlServerConnectorLsn other)
        {
            int firstComparison = First.CompareTo(other.First);
            if (firstComparison != 0)
            {
                return firstComparison;
            }

            int secondComparison = Second.CompareTo(other.Second);
            return secondComparison != 0 ? secondComparison : Third.CompareTo(other.Third);
        }
    }
}

internal static class CdcConnectorTemplatePinnedImageTestData
{
    private const string SourceIdentity = "f81d4fae-7dec-11d0-a765-00a0c91e6bf6";

    public static CdcSourceFingerprint SourceFingerprint(CdcProvider provider) =>
        CdcSourceFingerprintMetadata.Compute(provider, SourceIdentity);
}

internal sealed record CdcConnectorTemplateSmokeSettings(
    string ConnectImage,
    string BrokerImage,
    string ProviderImage,
    bool FailFast,
    bool KeepContainers
)
{
    private const string ConnectImageVariable = "CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE";
    private const string BrokerImageVariable = "CDC_CONNECTOR_TEMPLATE_REDPANDA_IMAGE";
    private const string PostgresqlImageVariable = "CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE";
    private const string SqlServerImageVariable = "CDC_CONNECTOR_TEMPLATE_SQLSERVER_2025_IMAGE";
    private const string FailFastVariable = "CDC_CONNECTOR_TEMPLATE_FAIL_FAST";
    private const string KeepContainersVariable = "CDC_CONNECTOR_TEMPLATE_KEEP_CONTAINERS";

    public static CdcConnectorTemplateSmokeSettings Offline { get; } =
        new(string.Empty, string.Empty, string.Empty, FailFast: false, KeepContainers: false);

    public static CdcConnectorTemplateSmokeSettings FromEnvironment(CdcProvider provider)
    {
        string providerImageVariable = ProviderImageVariable(provider);
        return new CdcConnectorTemplateSmokeSettings(
            Environment.GetEnvironmentVariable(ConnectImageVariable) ?? string.Empty,
            Environment.GetEnvironmentVariable(BrokerImageVariable) ?? string.Empty,
            Environment.GetEnvironmentVariable(providerImageVariable) ?? string.Empty,
            IsEnabled(FailFastVariable),
            IsEnabled(KeepContainersVariable)
        );
    }

    public void StopIfNotConfigured(CdcProvider provider)
    {
        string providerImageVariable = ProviderImageVariable(provider);
        List<string> missingVariables = [];

        if (string.IsNullOrWhiteSpace(ConnectImage))
        {
            missingVariables.Add(ConnectImageVariable);
        }
        else if (!ConnectImage.Contains("@sha256:", StringComparison.Ordinal))
        {
            StopOnPrerequisiteFailure(
                $"{ConnectImageVariable} must identify the qualified Ed-Fi Kafka Connect image by immutable digest."
            );
        }

        if (string.IsNullOrWhiteSpace(BrokerImage))
        {
            missingVariables.Add(BrokerImageVariable);
        }

        if (string.IsNullOrWhiteSpace(ProviderImage))
        {
            missingVariables.Add(providerImageVariable);
        }

        if (missingVariables.Count == 0)
        {
            return;
        }

        string missing = string.Join(", ", missingVariables);
        StopOnPrerequisiteFailure(
            $"CDC connector template pinned-image smoke prerequisites are not configured. Missing: {missing}. Set {FailFastVariable}=true in the qualification lane to fail instead of skipping."
        );
    }

    public async Task StopOnPrerequisiteFailureAsync(Task prerequisite, string message)
    {
        try
        {
            await prerequisite;
        }
        catch (Exception ex) when (ex is not AssertionException and not OperationCanceledException)
        {
            StopOnPrerequisiteFailure($"{message} Details: {ex.Message}");
        }
    }

    public void StopOnPrerequisiteFailure(string message)
    {
        if (FailFast)
        {
            Assert.Fail(message);
            return;
        }

        Assert.Ignore(message);
    }

    private static string ProviderImageVariable(CdcProvider provider) =>
        provider switch
        {
            CdcProvider.Postgresql => PostgresqlImageVariable,
            CdcProvider.SqlServer => SqlServerImageVariable,
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    private static bool IsEnabled(string variableName) =>
        bool.TryParse(Environment.GetEnvironmentVariable(variableName), out bool value) && value;
}

internal interface IDockerCli
{
    bool IsOffline { get; }

    Task RequireDockerAsync(CancellationToken cancellationToken);

    Task<DockerCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);

    Task<DockerCommandResult> RunAllowingFailureAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken
    );
}

internal sealed class DockerCli : IDockerCli
{
    public static DockerCli Offline { get; } = new(isOffline: true);

    private readonly bool _isOffline;

    public DockerCli()
        : this(isOffline: false) { }

    private DockerCli(bool isOffline)
    {
        _isOffline = isOffline;
    }

    public bool IsOffline => _isOffline;

    public async Task RequireDockerAsync(CancellationToken cancellationToken) =>
        _ = await RunAsync(["version", "--format", "{{.Server.Version}}"], cancellationToken);

    public async Task<DockerCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken
    )
    {
        DockerCommandResult result = await RunCoreAsync(arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.ToFailureMessage());
        }

        return result;
    }

    public async Task<DockerCommandResult> RunAllowingFailureAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken
    ) => await RunCoreAsync(arguments, cancellationToken);

    private async Task<DockerCommandResult> RunCoreAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken
    )
    {
        if (_isOffline)
        {
            return new DockerCommandResult(0, string.Empty, string.Empty);
        }

        using var process = new Process();
        process.StartInfo.FileName = "docker";
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new DockerCommandResult(
            process.ExitCode,
            DockerCommandResult.Sanitize(await stdout),
            DockerCommandResult.Sanitize(await stderr)
        );
    }
}

internal sealed record DockerCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string ToFailureMessage()
    {
        string stderr = string.IsNullOrWhiteSpace(StandardError) ? "<empty>" : Sanitize(StandardError).Trim();
        string stdout = string.IsNullOrWhiteSpace(StandardOutput)
            ? "<empty>"
            : Sanitize(StandardOutput).Trim();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"docker exited with code {ExitCode}. stdout: {stdout}. stderr: {stderr}"
        );
    }

    public static string Sanitize(string value) =>
        value.Replace(
            CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword,
            "[redacted]",
            StringComparison.Ordinal
        );
}
