// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private const string ConnectorDatabasePassword = "EdFi_Dms1!";
    private const string PostgresqlDatabaseName = "edfi_datastore";
    private const string SqlServerDatabaseName = "edfi_datastore";
    private const string DocumentStateTransformClass = "org.edfi.kafka.connect.transforms.DocumentState";
    private const string DocumentStateJsonConverterClass =
        "org.edfi.kafka.connect.converters.DocumentStateJsonConverter";
    private const string KafkaMurmur2PartitionerClass =
        "org.edfi.kafka.connect.partitioner.KafkaMurmur2V1Partitioner";

    private static readonly TimeSpan ConnectStartupTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ConnectorRunningTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ProviderHeartbeatTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OffsetCommitTimeout = TimeSpan.FromMinutes(4);

    private readonly CdcConnectorTemplateSmokeSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly DockerCli _docker;
    private readonly ServiceProvider _serviceProvider;
    private readonly string _resourcePrefix;

    private CdcConnectorTemplatePinnedImageFixture(
        CdcProvider provider,
        CdcConnectorTemplateSmokeSettings settings,
        Uri connectBaseUri,
        string resourcePrefix,
        DockerCli docker
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
                ["database.encrypt"] = "true",
                ["database.trustServerCertificate"] = "true",
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
        catch (Exception ex) when (ex is not AssertionException and not OperationCanceledException)
        {
            await fixture.DisposeAsync();
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
        IReadOnlyDictionary<string, string> effectiveConfig
    )
    {
        ICdcConnectorTemplateService service =
            _serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorProviderSetupEvidence providerSetupEvidence = BuildProviderSetupEvidence(
            request.Provider
        );

        CdcConnectorTemplateResult preflight = service.ValidateRegistrationPreflight(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                effectiveConfig,
                providerSetupEvidence
            )
        );
        CdcConnectorTemplateResult liveReadBack = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                effectiveConfig,
                providerSetupEvidence,
                BuildSourcePartitionEvidence(request, effectiveConfig)
            )
        );

        using var _ = new AssertionScope();
        preflight.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        preflight.Diagnostics.Should().BeEmpty();
        liveReadBack.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        liveReadBack.Diagnostics.Should().BeEmpty();
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
        string connectorName = rendered.ConnectorName.Value;
        using HttpResponseMessage deleteResponse = await _httpClient.DeleteAsync(
            $"/connectors/{Uri.EscapeDataString(connectorName)}",
            cancellationToken
        );
        deleteResponse
            .StatusCode.Should()
            .BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.NotFound, HttpStatusCode.Accepted);

        var payload = new CdcConnectorRegistrationDocument(connectorName, rendered.Config);
        using var content = new StringContent(
            JsonSerializer.Serialize(payload),
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

    public async Task AssertHeartbeatAndCommittedOffsetProgressAsync(
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
    }

    public async Task RestartRegisteredConnectorAndAssertTemplateStillValidAsync(
        CdcConnectorTemplateRequest request,
        CancellationToken cancellationToken
    )
    {
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
        await AssertKafkaConnectReadBackMatchesExpectedConfigAsync(request, cancellationToken);

        CdcConnectorSourceOffsetSnapshot retainedOffset = await WaitForCommittedSourceOffsetProgressAsync(
            request,
            startingOffset: null,
            cancellationToken
        );
        retainedOffset.CanonicalOffsetJson.Should().NotBeNullOrWhiteSpace();
    }

    public async Task AssertKafkaConnectReadBackMatchesExpectedConfigAsync(
        CdcConnectorTemplateRequest request,
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
        AssertRenderedTemplateCanBeValidatedFromReadBack(request, config);
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
        await _docker.RunAsync(
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
                $"{ConnectorPasswordEnvironmentVariable}={ConnectorDatabasePassword}",
                _settings.ConnectImage,
            ],
            cancellationToken
        );
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
        string[] mappedPortLines = result.StandardOutput.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        string mappedPortLine = mappedPortLines[0];
        int delimiterIndex = mappedPortLine.LastIndexOf(':');
        string port = mappedPortLine[(delimiterIndex + 1)..];

        return new Uri($"http://127.0.0.1:{port}");
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

        string starting = startingOffset?.CanonicalOffsetJson ?? "<none>";
        string observed = lastObservedOffset ?? "<none>";
        Assert.Fail(
            $"Kafka Connect committed source offset did not progress. Starting offset: {starting}. Last observed offset: {observed}."
        );
        throw new InvalidOperationException("Kafka Connect committed source offset did not progress.");
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
        const string sql = """
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
                IF NOT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = 'dms_binding_publication') THEN
                    CREATE PUBLICATION dms_binding_publication
                    FOR TABLE "dms"."DocumentCache", "dms"."Document", "dms"."CdcHeartbeat";
                END IF;
            END
            $$;
            SELECT
                CASE
                    WHEN NOT EXISTS (
                        SELECT 1 FROM pg_replication_slots WHERE slot_name = 'dms_binding_slot'
                    )
                    THEN pg_create_logical_replication_slot('dms_binding_slot', 'pgoutput')::text
                    ELSE NULL
                END;
            """;

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
    }

    private async Task CreateMinimalSqlServerObjectsAsync(CancellationToken cancellationToken)
    {
        string sql = $$"""
            IF DB_ID(N'{{SqlServerDatabaseName}}') IS NULL
                CREATE DATABASE [{{SqlServerDatabaseName}}];
            GO
            USE [{{SqlServerDatabaseName}}];
            IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'dms')
                EXEC(N'CREATE SCHEMA [dms]');
            IF OBJECT_ID(N'[dms].[DocumentCache]', N'U') IS NULL
                CREATE TABLE [dms].[DocumentCache] ([DocumentUuid] nvarchar(36) NOT NULL PRIMARY KEY);
            IF OBJECT_ID(N'[dms].[Document]', N'U') IS NULL
                CREATE TABLE [dms].[Document] ([DocumentUuid] nvarchar(36) NOT NULL PRIMARY KEY);
            IF OBJECT_ID(N'[dms].[CdcHeartbeat]', N'U') IS NULL
                CREATE TABLE [dms].[CdcHeartbeat]
                (
                    [HeartbeatId] smallint NOT NULL PRIMARY KEY,
                    [HeartbeatSequence] bigint NOT NULL,
                    [HeartbeatAt] datetimeoffset NOT NULL,
                    CONSTRAINT [CK_CdcHeartbeat_Singleton] CHECK ([HeartbeatId] = 1),
                    CONSTRAINT [CK_CdcHeartbeat_Sequence] CHECK ([HeartbeatSequence] >= 0)
                );
            IF NOT EXISTS (SELECT 1 FROM [dms].[CdcHeartbeat] WHERE [HeartbeatId] = 1)
                INSERT INTO [dms].[CdcHeartbeat] ([HeartbeatId], [HeartbeatSequence], [HeartbeatAt])
                VALUES (1, 0, SYSDATETIMEOFFSET());
            IF (SELECT is_cdc_enabled FROM sys.databases WHERE name = DB_NAME()) = 0
                EXEC sys.sp_cdc_enable_db;
            IF NOT EXISTS (
                SELECT 1 FROM cdc.change_tables WHERE capture_instance = N'dms_DocumentCache'
            )
                EXEC sys.sp_cdc_enable_table
                    @source_schema = N'dms',
                    @source_name = N'DocumentCache',
                    @role_name = NULL,
                    @supports_net_changes = 0;
            IF NOT EXISTS (
                SELECT 1 FROM cdc.change_tables WHERE capture_instance = N'dms_Document'
            )
                EXEC sys.sp_cdc_enable_table
                    @source_schema = N'dms',
                    @source_name = N'Document',
                    @role_name = NULL,
                    @supports_net_changes = 0;
            IF NOT EXISTS (
                SELECT 1 FROM cdc.change_tables WHERE capture_instance = N'dms_CdcHeartbeat'
            )
                EXEC sys.sp_cdc_enable_table
                    @source_schema = N'dms',
                    @source_name = N'CdcHeartbeat',
                    @role_name = NULL,
                    @supports_net_changes = 0;
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
            if (
                !offsetDocument.TryGetProperty("partition", out JsonElement partition)
                || !SourcePartitionMatches(request, partition)
                || !offsetDocument.TryGetProperty("offset", out JsonElement offset)
                || !ProviderOffsetIsCommittedProgress(request.Provider, offset)
            )
            {
                continue;
            }

            matchingOffsets.Add(new CdcConnectorSourceOffsetSnapshot(CanonicalizeJson(offset)));
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

    private static bool ProviderOffsetIsCommittedProgress(CdcProvider provider, JsonElement offset)
    {
        if (offset.ValueKind != JsonValueKind.Object || OffsetIsSnapshot(offset))
        {
            return false;
        }

        return provider switch
        {
            CdcProvider.Postgresql => HasNonEmptyJsonProperty(offset, "lsn_proc"),
            CdcProvider.SqlServer => HasNonEmptyJsonProperty(offset, "commit_lsn")
                && HasNonEmptyJsonProperty(offset, "change_lsn")
                && HasNonEmptyJsonProperty(offset, "event_serial_no"),
            _ => false,
        };
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

    private static bool HasNonEmptyJsonProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrWhiteSpace(property.GetString()),
            JsonValueKind.Number => true,
            JsonValueKind.True or JsonValueKind.False => true,
            _ => false,
        };
    }

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

    private static CdcConnectorTemplateBindingIdentity BuildBinding(CdcProvider provider) =>
        new(
            provider,
            new CdcSafeName("dms_binding_connector"),
            "edfi.documents",
            bindingGeneration: 7,
            partitionerAlgorithm: "kafka-murmur2-v1",
            CdcConnectorTemplatePinnedImageTestData.SourceFingerprint
        );

    private static CdcConnectorProviderSetupEvidence BuildProviderSetupEvidence(CdcProvider provider) =>
        new(bindingGeneration: 7, BuildProviderSetupResult(provider));

    private static CdcProviderSetupResult BuildProviderSetupResult(CdcProvider provider) =>
        new(
            Provider: provider,
            Mode: CdcProviderSetupMode.InitialCreateOrExactMatch,
            Outcome: CdcProviderSetupOutcome.CreatedOrMatched,
            BoundPhysicalSourceFingerprint: CdcConnectorTemplatePinnedImageTestData.SourceFingerprint,
            ObservedSourceFingerprint: CdcConnectorTemplatePinnedImageTestData.SourceFingerprint,
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
                "UPDATE [dms].[CdcHeartbeat] SET [HeartbeatSequence] = [HeartbeatSequence] + 1, [HeartbeatAt] = SYSDATETIMEOFFSET() WHERE [HeartbeatId] = 1;",
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
                    new CdcSafeName("dms_binding_publication"),
                    CdcProviderArtifactState.Matched,
                    new Dictionary<string, string>()
                ),
                new(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    new CdcSafeName("dms_binding_slot"),
                    CdcProviderArtifactState.Matched,
                    new Dictionary<string, string>()
                ),
            ],
            CdcProvider.SqlServer =>
            [
                new(
                    CdcProviderArtifactKind.SqlServerCaptureInstance,
                    new CdcSafeName("dms_binding_document_cache_capture"),
                    CdcProviderArtifactState.Matched,
                    new Dictionary<string, string>()
                ),
                new(
                    CdcProviderArtifactKind.SqlServerCaptureInstance,
                    new CdcSafeName("dms_binding_document_capture"),
                    CdcProviderArtifactState.Matched,
                    new Dictionary<string, string>()
                ),
                new(
                    CdcProviderArtifactKind.SqlServerCaptureInstance,
                    new CdcSafeName("dms_binding_cdc_heartbeat_capture"),
                    CdcProviderArtifactState.Matched,
                    new Dictionary<string, string>()
                ),
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    private static IReadOnlyList<CdcSourceTableInventory> BuildRequiredSourceTableInventory(
        CdcProvider provider
    ) =>
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
        CdcConnectorTemplateRequest request,
        IReadOnlyDictionary<string, string> effectiveConfig
    )
    {
        var properties = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["server"] = effectiveConfig["topic.prefix"],
        };

        if (request.Provider == CdcProvider.SqlServer)
        {
            properties["database"] = effectiveConfig["database.names"];
        }

        return new CdcConnectorTemplateSourcePartitionEvidence(properties);
    }

    private sealed record CdcConnectorRegistrationDocument(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("config")] IReadOnlyDictionary<string, string> Config
    );

    private sealed record CdcConnectorSourceOffsetSnapshot(string CanonicalOffsetJson);
}

internal static class CdcConnectorTemplatePinnedImageTestData
{
    public static readonly CdcSourceFingerprint SourceFingerprint = new(
        "cdc-source-fingerprint-v1",
        "physical-source-fingerprint"
    );
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

internal sealed class DockerCli
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
            await stdout,
            DockerCommandResult.Sanitize(await stderr)
        );
    }
}

internal sealed record DockerCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string ToFailureMessage()
    {
        string stderr = string.IsNullOrWhiteSpace(StandardError) ? "<empty>" : StandardError.Trim();
        string stdout = string.IsNullOrWhiteSpace(StandardOutput) ? "<empty>" : StandardOutput.Trim();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"docker exited with code {ExitCode}. stdout: {stdout}. stderr: {stderr}"
        );
    }

    public static string Sanitize(string value) =>
        value.Replace("EdFi_Dms1!", "[redacted]", StringComparison.Ordinal);
}
