// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed class CdcConnectorTemplatePinnedImageFixture : IAsyncDisposable
{
    private const string ConnectorPasswordEnvironmentVariable = "CDC_DATABASE_PASSWORD";
    internal const string ConnectorDatabasePassword = "EdFi_Dms1!";
    private const string ConnectorDatabaseUser = "dms_connector";
    private const string EnvConfigProviderName = "env";
    private const string EnvConfigProviderClass =
        "org.apache.kafka.common.config.provider.EnvVarConfigProvider";
    private const string ConnectConfigProvidersEnvironmentVariable =
        $"CONNECT_CONFIG_PROVIDERS={EnvConfigProviderName}";
    private const string ConnectConfigProviderEnvClassEnvironmentVariable =
        $"CONNECT_CONFIG_PROVIDERS_ENV_CLASS={EnvConfigProviderClass}";
    private const long BindingGeneration = 7;
    private const string DeploymentKey = "dms";
    private const string InstanceKey = "binding";
    private const string TopicPrefix = "edfi.documents";
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
    private static readonly CoreCdc.CdcArtifactInventory PostgresqlArtifactInventory =
        BuildCoreArtifactInventory(CdcProvider.Postgresql);
    private static readonly CoreCdc.CdcArtifactInventory SqlServerArtifactInventory =
        BuildCoreArtifactInventory(CdcProvider.SqlServer);
    private static readonly string PostgresqlPublicationName =
        PostgresqlArtifactInventory.PostgresqlPublicationName!;
    private static readonly string PostgresqlReplicationSlotName =
        PostgresqlArtifactInventory.PostgresqlLogicalSlotName!;
    private static readonly string SqlServerGatingRoleName =
        SqlServerArtifactInventory.SqlServerCdcGatingRoleName!;
    private static readonly IReadOnlyList<SqlServerCaptureInstanceDefinition> SqlServerCaptureInstances =
    [
        new(
            CdcSourceTableKind.DocumentCache,
            "DocumentCache",
            new CdcSafeName(SqlServerArtifactInventory.SqlServerCaptureInstanceDocumentCacheName!),
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
            new CdcSafeName(SqlServerArtifactInventory.SqlServerCaptureInstanceDocumentName!),
            "DocumentId",
            [
                new("DocumentId", "bigint"),
                new("DocumentUuid", "uniqueidentifier"),
                new("ResourceKeyId", "smallint"),
                new("CreatedByOwnershipTokenId", "smallint", IsNullable: true),
                new("ContentVersion", "bigint"),
                new("ContentLastModifiedAt", "datetime2(7)"),
                new("CreatedAt", "datetime2(7)"),
            ]
        ),
        new(
            CdcSourceTableKind.CdcHeartbeat,
            "CdcHeartbeat",
            new CdcSafeName(SqlServerArtifactInventory.SqlServerCaptureInstanceCdcHeartbeatName!),
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
        _serviceProvider = new ServiceCollection()
            .AddCdcConnectorTemplates()
            .AddCdcProviderSetup()
            .BuildServiceProvider();
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
                ["database.user"] = ConnectorDatabaseUser,
                ["database.password"] = $"${{env:{ConnectorPasswordEnvironmentVariable}}}",
                ["database.dbname"] = PostgresqlDatabaseName,
            },
            CdcProvider.SqlServer => new Dictionary<string, string>
            {
                ["database.hostname"] = ProviderContainerName,
                ["database.port"] = "1433",
                ["database.user"] = ConnectorDatabaseUser,
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
            provider,
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
                provider,
                $"Pinned-image fixture prerequisites are not ready for {provider}. Failure details are redacted."
            );
            throw;
        }
    }

    public async Task<CdcConnectorTemplateRequest> CreateRequestAsync(CancellationToken cancellationToken)
    {
        await CreateMinimalProviderObjectsAsync(cancellationToken);
        await AssertSqlServer2025Async(cancellationToken);
        CdcProviderSetupResult providerSetupResult = await RunProviderSetupAsync(
            CdcProviderSetupMode.InitialCreateOrExactMatch,
            cancellationToken
        );
        CdcConnectorTemplateRequest request = BuildRequest(providerSetupResult);
        await CreateMinimalTopicsAsync(request, cancellationToken);
        return request;
    }

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

    private async Task AssertRenderedTemplateCanBeValidatedFromReadBackAsync(
        CdcConnectorTemplateRequest request,
        IReadOnlyDictionary<string, string> effectiveConfig,
        CdcConnectorTemplateSourcePartitionEvidence sourcePartitionEvidence,
        CancellationToken cancellationToken
    )
    {
        ICdcConnectorTemplateService service =
            _serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcProviderSetupResult liveProviderSetupResult = await RunProviderSetupAsync(
            CdcProviderSetupMode.ValidateOnly,
            cancellationToken
        );
        var liveReadBackProviderSetupEvidence = new CdcConnectorProviderSetupEvidence(
            request.BindingGeneration,
            liveProviderSetupResult
        );
        CdcConnectorTemplateResult rendered = service.Render(request);

        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        rendered.Diagnostics.Should().BeEmpty();

        CdcConnectorTemplateResult preflight = service.ValidateRegistrationPreflight(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                effectiveConfig,
                request.ProviderSetupEvidence
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

        if (preflight.Outcome != CdcConnectorTemplateOutcome.Rendered || preflight.Diagnostics.Count != 0)
        {
            CdcConnectorTemplatePinnedImageSmokeDiagnostics.Fail(
                CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
                    code: CdcConnectorTemplateDiagnosticCodes.PinnedImageReadBackValidationFailure,
                    category: CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch,
                    provider: request.Provider,
                    propertyName: "template.preflight",
                    safeArtifactOrObjectName: request.ConnectorName,
                    expectedValue: CdcConnectorTemplateOutcome.Rendered.ToString(),
                    observedValue: preflight.Outcome.ToString(),
                    redactionClassification: CdcConnectorTemplateRedactionClassification.Safe
                ),
                $"Pinned-image registration preflight validation failed. Diagnostics: {CdcConnectorTemplatePinnedImageSmokeDiagnostics.FormatDiagnostics(preflight.Diagnostics)}"
            );
        }

        if (
            liveReadBack.Outcome != CdcConnectorTemplateOutcome.Rendered
            || liveReadBack.Diagnostics.Count != 0
        )
        {
            CdcConnectorTemplatePinnedImageSmokeDiagnostics.Fail(
                CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
                    code: CdcConnectorTemplateDiagnosticCodes.PinnedImageReadBackValidationFailure,
                    category: CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch,
                    provider: request.Provider,
                    propertyName: "template.liveReadBack",
                    safeArtifactOrObjectName: request.ConnectorName,
                    expectedValue: CdcConnectorTemplateOutcome.Rendered.ToString(),
                    observedValue: liveReadBack.Outcome.ToString(),
                    redactionClassification: CdcConnectorTemplateRedactionClassification.Safe
                ),
                $"Pinned-image live read-back validation failed. Diagnostics: {CdcConnectorTemplatePinnedImageSmokeDiagnostics.FormatDiagnostics(liveReadBack.Diagnostics)}"
            );
        }
    }

    public async Task AssertRuntimeLoadsRequiredClassesAsync(
        CdcConnectorTemplateResult rendered,
        CancellationToken cancellationToken
    )
    {
        string[] pluginClasses = await ReadConnectorPluginClassesAsync(cancellationToken);
        string connectorClass = rendered.Config["connector.class"];
        if (!pluginClasses.Contains(connectorClass, StringComparer.Ordinal))
        {
            CdcConnectorTemplatePinnedImageSmokeDiagnostics.Fail(
                CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
                    code: CdcConnectorTemplateDiagnosticCodes.PinnedImageRuntimeClassLoadFailure,
                    category: CdcConnectorTemplateDiagnosticCategory.TransformConfigurationViolation,
                    provider: Provider,
                    propertyName: "pinnedImage.connectorClass",
                    safeArtifactOrObjectName: new CdcSafeName(connectorClass),
                    expectedValue: connectorClass,
                    observedValue: "not found",
                    redactionClassification: CdcConnectorTemplateRedactionClassification.Safe
                ),
                "Pinned Kafka Connect image did not advertise the rendered connector class."
            );
        }

        string[] requiredClasses =
        [
            connectorClass,
            DocumentStateTransformClass,
            DocumentStateJsonConverterClass,
            KafkaMurmur2PartitionerClass,
        ];

        try
        {
            await RunJavaClassProbeAsync(requiredClasses, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CdcConnectorTemplatePinnedImageSmokeDiagnostics.Fail(
                CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
                    code: CdcConnectorTemplateDiagnosticCodes.PinnedImageRuntimeClassLoadFailure,
                    category: CdcConnectorTemplateDiagnosticCategory.TransformConfigurationViolation,
                    provider: Provider,
                    propertyName: "pinnedImage.requiredClasses",
                    safeArtifactOrObjectName: new CdcSafeName(connectorClass),
                    expectedValue: string.Join(",", requiredClasses),
                    observedValue: "[redacted]",
                    redactionClassification: CdcConnectorTemplateRedactionClassification.SecretValue
                ),
                $"Pinned Kafka Connect image failed the required class probe. Failure details are redacted. Error type: {ex.GetType().Name}."
            );
        }
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

        try
        {
            await _docker.RunAsync(["exec", ConnectContainerName, "sh", "-lc", javaProbe], cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CdcConnectorTemplatePinnedImageSmokeDiagnostics.Fail(
                CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
                    code: CdcConnectorTemplateDiagnosticCodes.PinnedImageRuntimeClassLoadFailure,
                    category: CdcConnectorTemplateDiagnosticCategory.ProducerPolicyViolation,
                    provider: Provider,
                    propertyName: "pinnedImage.partitionerVectors",
                    safeArtifactOrObjectName: new CdcSafeName(KafkaMurmur2PartitionerClass),
                    expectedValue: "fixed murmur2 partition vectors",
                    observedValue: "[redacted]",
                    redactionClassification: CdcConnectorTemplateRedactionClassification.SecretValue
                ),
                $"Pinned Kafka Connect image failed the partitioner vector probe. Failure details are redacted. Error type: {ex.GetType().Name}."
            );
        }
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

        if (!response.IsSuccessStatusCode)
        {
            CdcConnectorTemplatePinnedImageSmokeDiagnostics.Fail(
                CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
                    code: CdcConnectorTemplateDiagnosticCodes.PinnedImageConnectorConfigValidationFailure,
                    category: CdcConnectorTemplateDiagnosticCategory.ReservedKeyViolation,
                    provider: Provider,
                    propertyName: "kafkaConnect.configValidation",
                    safeArtifactOrObjectName: new CdcSafeName(connectorClass),
                    expectedValue: "successful HTTP status",
                    observedValue: response.StatusCode.ToString(),
                    redactionClassification: CdcConnectorTemplateRedactionClassification.Safe
                ),
                "Kafka Connect config validation failed. Connector validation output is redacted."
            );
        }

        IReadOnlyList<string> validationErrors = ExtractValidationErrors(responseBody);
        if (validationErrors.Count != 0)
        {
            CdcConnectorTemplatePinnedImageSmokeDiagnostics.Fail(
                CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
                    code: CdcConnectorTemplateDiagnosticCodes.PinnedImageConnectorConfigValidationFailure,
                    category: CdcConnectorTemplateDiagnosticCategory.ReservedKeyViolation,
                    provider: Provider,
                    propertyName: "kafkaConnect.configValidation",
                    safeArtifactOrObjectName: new CdcSafeName(connectorClass),
                    expectedValue: "no connector config validation errors",
                    observedValue: "[redacted]",
                    redactionClassification: CdcConnectorTemplateRedactionClassification.SecretValue
                ),
                $"Kafka Connect config validation returned {validationErrors.Count} errors. Error text is redacted."
            );
        }
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

        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK))
        {
            CdcConnectorTemplatePinnedImageSmokeDiagnostics.Fail(
                CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
                    code: CdcConnectorTemplateDiagnosticCodes.PinnedImageConnectorRegistrationFailure,
                    category: CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch,
                    provider: Provider,
                    propertyName: "kafkaConnect.registration",
                    safeArtifactOrObjectName: rendered.ConnectorName,
                    expectedValue: "Created or OK",
                    observedValue: response.StatusCode.ToString(),
                    redactionClassification: CdcConnectorTemplateRedactionClassification.Safe
                ),
                "Kafka Connect registration failed. Connector registration output is redacted."
            );
        }
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

        if (
            response.StatusCode
            is not (HttpStatusCode.Accepted or HttpStatusCode.NoContent or HttpStatusCode.OK)
        )
        {
            CdcConnectorTemplatePinnedImageSmokeDiagnostics.Fail(
                CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
                    code: CdcConnectorTemplateDiagnosticCodes.PinnedImageConnectorStatusFailure,
                    category: CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch,
                    provider: request.Provider,
                    propertyName: "kafkaConnect.connectorRestart",
                    safeArtifactOrObjectName: request.ConnectorName,
                    expectedValue: "Accepted, NoContent, or OK",
                    observedValue: response.StatusCode.ToString(),
                    redactionClassification: CdcConnectorTemplateRedactionClassification.Safe
                ),
                "Kafka Connect restart failed. Connector restart output is redacted."
            );
        }

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

        if (!response.IsSuccessStatusCode)
        {
            CdcConnectorTemplatePinnedImageSmokeDiagnostics.Fail(
                CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
                    code: CdcConnectorTemplateDiagnosticCodes.PinnedImageReadBackValidationFailure,
                    category: CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch,
                    provider: request.Provider,
                    propertyName: "kafkaConnect.readBackConfig",
                    safeArtifactOrObjectName: request.ConnectorName,
                    expectedValue: "successful HTTP status",
                    observedValue: response.StatusCode.ToString(),
                    redactionClassification: CdcConnectorTemplateRedactionClassification.Safe
                ),
                "Kafka Connect config read-back failed. Connector read-back output is redacted."
            );
        }

        IReadOnlyDictionary<string, string> config = ParseStringMap(responseBody);
        await AssertRenderedTemplateCanBeValidatedFromReadBackAsync(
            request,
            config,
            sourcePartitionEvidence,
            cancellationToken
        );
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

    private CdcConnectorTemplateRequest BuildRequest(CdcProviderSetupResult providerSetupResult) =>
        new(
            BuildBinding(Provider),
            new CdcConnectorProviderSetupEvidence(BindingGeneration, providerSetupResult),
            new CdcConnectorTemplateDeploymentPolicy(
                KafkaBootstrapServers,
                maxRecordBytes: 67_108_864,
                heartbeatInterval: TimeSpan.FromSeconds(5),
                sqlServerPollInterval: Provider == CdcProvider.SqlServer ? TimeSpan.FromSeconds(2) : null
            ),
            new CdcProviderConnectionProperties(Provider, ProviderConnectionProperties),
            CdcKafkaClientSecurityProperties.Empty
        );

    private async Task<CdcProviderSetupResult> RunProviderSetupAsync(
        CdcProviderSetupMode mode,
        CancellationToken cancellationToken
    )
    {
        int providerPort = await ReadMappedProviderPortAsync(cancellationToken);
        await using DbConnection connection = CreateProviderAdminConnection(providerPort);
        var executor = new DbConnectionCdcProviderDatabaseExecutor(connection);
        ICdcProviderSetupService providerSetupService =
            _serviceProvider.GetRequiredService<ICdcProviderSetupService>();
        CdcProviderSetupResult result = await providerSetupService.SetupAsync(
            BuildProviderSetupRequest(mode, executor),
            cancellationToken
        );
        CdcProviderSetupOutcome expectedOutcome =
            mode == CdcProviderSetupMode.ValidateOnly
                ? CdcProviderSetupOutcome.ExactMatch
                : CdcProviderSetupOutcome.CreatedOrMatched;
        string diagnosticCodes = string.Join(",", result.Diagnostics.Select(diagnostic => diagnostic.Code));

        result.Outcome.Should().Be(expectedOutcome, "provider setup diagnostics were {0}", diagnosticCodes);
        result
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        return result;
    }

    private DbConnection CreateProviderAdminConnection(int providerPort) =>
        Provider switch
        {
            CdcProvider.Postgresql => new NpgsqlConnection(
                $"Host=127.0.0.1;Port={providerPort};Username=postgres;Password={ConnectorDatabasePassword};Database={PostgresqlDatabaseName}"
            ),
            CdcProvider.SqlServer => new SqlConnection(
                $"Server=127.0.0.1,{providerPort};Database={SqlServerDatabaseName};User Id=sa;Password={ConnectorDatabasePassword};Encrypt=True;TrustServerCertificate=True"
            ),
            _ => throw new InvalidOperationException("Unsupported CDC provider."),
        };

    private CdcProviderSetupRequest BuildProviderSetupRequest(
        CdcProviderSetupMode mode,
        ICdcProviderDatabaseExecutor databaseExecutor
    ) =>
        new(
            provider: Provider,
            mode: mode,
            boundPhysicalSourceFingerprint: CdcConnectorTemplatePinnedImageTestData.SourceFingerprint(
                Provider
            ),
            setupPrincipal: new CdcSetupPrincipalContext(
                new CdcSafeName(Provider == CdcProvider.Postgresql ? "postgres" : "sa")
            ),
            connectorPrincipal: new CdcConnectorPrincipal(new CdcSafeName(ConnectorDatabaseUser)),
            artifactNames: BuildProviderArtifactNames(Provider),
            artifactOutput: new CdcProviderArtifactOutputRequest(IncludeManifestPayload: false),
            expectedSourceInventory: BuildRequiredSourceTableInventory(Provider),
            dmsManagedTableInventory: BuildDmsManagedTableInventory(Provider),
            databaseExecutor: databaseExecutor
        );

    private static IReadOnlyList<CdcDmsManagedTableInventory> BuildDmsManagedTableInventory(
        CdcProvider provider
    )
    {
        ISqlDialect dialect = SqlDialectFactory.Create(
            provider == CdcProvider.Postgresql ? SqlDialect.Pgsql : SqlDialect.Mssql
        );
        DbTableName[] tables =
        [
            DmsTableNames.DataStoreIdentity,
            DmsTableNames.CdcHeartbeat,
            DmsTableNames.Document,
            DmsTableNames.DocumentCache,
            DmsTableNames.DocumentProjectionWork,
        ];

        return tables
            .Select(table => new CdcDmsManagedTableInventory(
                CdcDmsManagedTableKind.Core,
                table,
                dialect.QualifyTable(table)
            ))
            .ToArray();
    }

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
                    "-p",
                    "127.0.0.1::5432",
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
                "-p",
                "127.0.0.1::1433",
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

    private async Task<int> ReadMappedProviderPortAsync(CancellationToken cancellationToken)
    {
        string containerPort = Provider == CdcProvider.Postgresql ? "5432/tcp" : "1433/tcp";
        DockerCommandResult result = await _docker.RunAsync(
            ["port", ProviderContainerName, containerPort],
            cancellationToken
        );
        return ParseMappedPort(ProviderContainerName, result.StandardOutput, "CDC provider");
    }

    internal static Uri ParseMappedConnectBaseUri(string connectContainerName, string dockerPortOutput) =>
        new(
            string.Create(
                CultureInfo.InvariantCulture,
                $"http://127.0.0.1:{ParseMappedPort(connectContainerName, dockerPortOutput, "Kafka Connect")}"
            )
        );

    private static int ParseMappedPort(string containerName, string dockerPortOutput, string serviceName)
    {
        string[] mappedPortLines = dockerPortOutput.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        if (mappedPortLines.Length == 0)
        {
            throw InvalidMappedPortOutput(
                containerName,
                dockerPortOutput,
                "expected one mapped host port line but Docker returned no output",
                serviceName
            );
        }

        string mappedPortLine = mappedPortLines[0];
        int delimiterIndex = mappedPortLine.LastIndexOf(':');
        if (delimiterIndex < 0)
        {
            throw InvalidMappedPortOutput(
                containerName,
                dockerPortOutput,
                "expected mapped port line to contain a ':' delimiter",
                serviceName
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
                containerName,
                dockerPortOutput,
                "expected mapped port line to end with a non-empty numeric TCP port",
                serviceName
            );
        }

        return mappedPort;
    }

    private static InvalidOperationException InvalidMappedPortOutput(
        string containerName,
        string dockerPortOutput,
        string reason,
        string serviceName
    ) =>
        new(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Invalid docker port output for {serviceName} container '{containerName}': {reason}. Docker output: {FormatDockerOutputForDiagnostic(dockerPortOutput)}"
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
        CdcSafeName safeConnectorName = new(connectorName);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(
                $"/connectors/{Uri.EscapeDataString(connectorName)}/status",
                cancellationToken
            );
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                if (ConnectorStatusIsRunning(responseBody))
                {
                    return;
                }

                if (ConnectorStatusHasFailure(responseBody))
                {
                    CdcConnectorTemplatePinnedImageSmokeDiagnostics.Fail(
                        CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
                            code: CdcConnectorTemplateDiagnosticCodes.PinnedImageConnectorStatusFailure,
                            category: CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch,
                            provider: Provider,
                            propertyName: "kafkaConnect.connectorStatus",
                            safeArtifactOrObjectName: safeConnectorName,
                            expectedValue: "RUNNING",
                            observedValue: "FAILED",
                            redactionClassification: CdcConnectorTemplateRedactionClassification.Safe
                        ),
                        "Kafka Connect task failed before reaching RUNNING. Connector status output is redacted."
                    );
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        CdcConnectorTemplatePinnedImageSmokeDiagnostics.Fail(
            CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
                code: CdcConnectorTemplateDiagnosticCodes.PinnedImageConnectorStatusFailure,
                category: CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch,
                provider: Provider,
                propertyName: "kafkaConnect.connectorStatus",
                safeArtifactOrObjectName: safeConnectorName,
                expectedValue: "RUNNING",
                observedValue: "[redacted]",
                redactionClassification: CdcConnectorTemplateRedactionClassification.SecretValue
            ),
            "Kafka Connect task did not reach RUNNING before the timeout. Last status output is redacted."
        );
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

        CdcConnectorTemplatePinnedImageSmokeDiagnostics.Fail(
            CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
                code: CdcConnectorTemplateDiagnosticCodes.PinnedImageOffsetProgressFailure,
                category: CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch,
                provider: Provider,
                propertyName: "provider.heartbeatSequence",
                safeArtifactOrObjectName: new CdcSafeName("dms.CdcHeartbeat"),
                expectedValue: "provider heartbeat sequence advancement",
                observedValue: observedHeartbeatSequence.ToString(CultureInfo.InvariantCulture),
                redactionClassification: CdcConnectorTemplateRedactionClassification.Safe
            ),
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
                && (startingOffset is null || SourceOffsetAdvances(startingOffset, observedOffset))
            )
            {
                return observedOffset;
            }

            lastObservedOffset = observedOffset?.CanonicalOffsetJson;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        CdcConnectorTemplatePinnedImageSmokeDiagnostics.Fail(
            CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
                code: CdcConnectorTemplateDiagnosticCodes.PinnedImageOffsetProgressFailure,
                category: CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch,
                provider: request.Provider,
                propertyName: "kafkaConnect.committedOffset",
                safeArtifactOrObjectName: request.ConnectorName,
                expectedValue: "committed provider position progress",
                observedValue: "[redacted]",
                redactionClassification: CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            ),
            $"Kafka Connect committed source offset did not progress. Starting offset and last observed offset are redacted. Starting present: {startingOffset is not null}. Last observed present: {lastObservedOffset is not null}."
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

        CdcConnectorTemplatePinnedImageSmokeDiagnostics.Fail(
            CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
                code: CdcConnectorTemplateDiagnosticCodes.PinnedImageOffsetProgressFailure,
                category: CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch,
                provider: request.Provider,
                propertyName: "kafkaConnect.committedOffset",
                safeArtifactOrObjectName: request.ConnectorName,
                expectedValue: "retained or advanced committed provider position",
                observedValue: "[redacted]",
                redactionClassification: CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            ),
            $"Kafka Connect did not retain or advance from the pre-restart committed source offset. Offset values are redacted. Last retention check: {lastRetentionFailure}."
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

    private async Task AssertSqlServer2025Async(CancellationToken cancellationToken)
    {
        if (Provider != CdcProvider.SqlServer)
        {
            return;
        }

        string output = await ReadSqlServerScalarAsync(
            "SELECT CONVERT(nvarchar(20), SERVERPROPERTY('ProductMajorVersion'));",
            cancellationToken
        );
        string majorVersion =
            output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault()
            ?? string.Empty;

        majorVersion
            .Should()
            .Be("17", "CDC_CONNECTOR_TEMPLATE_SQLSERVER_2025_IMAGE must run SQL Server 2025");
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
    }

    private static string BuildMinimalPostgresqlObjectsSql() =>
        $$"""
            CREATE SCHEMA IF NOT EXISTS "dms";
            CREATE TABLE IF NOT EXISTS "dms"."DataStoreIdentity"
            (
                "DataStoreIdentitySingletonId" smallint NOT NULL PRIMARY KEY,
                "SourceIdentity" uuid NOT NULL
            );
            INSERT INTO "dms"."DataStoreIdentity" ("DataStoreIdentitySingletonId", "SourceIdentity")
            VALUES (1, '{{CdcConnectorTemplatePinnedImageTestData.SourceIdentity}}')
            ON CONFLICT ("DataStoreIdentitySingletonId") DO NOTHING;
            CREATE TABLE IF NOT EXISTS "dms"."DocumentCache" ("DocumentUuid" text NOT NULL PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS "dms"."Document" ("DocumentUuid" text NOT NULL PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS "dms"."DocumentProjectionWork" ("DocumentId" bigint NOT NULL PRIMARY KEY);
            DO $role$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = '{{ConnectorDatabaseUser}}') THEN
                    EXECUTE format(
                        'CREATE ROLE %I WITH LOGIN REPLICATION NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD %L',
                        '{{ConnectorDatabaseUser}}',
                        '{{ConnectorDatabasePassword}}'
                    );
                END IF;
            END
            $role$;
            """;

    private async Task CreateMinimalSqlServerObjectsAsync(CancellationToken cancellationToken)
    {
        string createSourceTablesSql = string.Join(
            Environment.NewLine,
            SqlServerCaptureInstances
                .Where(definition => definition.TableKind != CdcSourceTableKind.CdcHeartbeat)
                .Select(CreateSqlServerSourceTableSql)
        );

        string sql = $$"""
            IF DB_ID(N'{{SqlServerDatabaseName}}') IS NULL
                CREATE DATABASE [{{SqlServerDatabaseName}}];
            GO
            USE [{{SqlServerDatabaseName}}];
            IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'dms')
                EXEC(N'CREATE SCHEMA [dms]');
            IF OBJECT_ID(N'[dms].[DataStoreIdentity]', N'U') IS NULL
                CREATE TABLE [dms].[DataStoreIdentity]
                (
                    [DataStoreIdentitySingletonId] smallint NOT NULL PRIMARY KEY,
                    [SourceIdentity] uniqueidentifier NOT NULL
                );
            IF NOT EXISTS (SELECT 1 FROM [dms].[DataStoreIdentity] WHERE [DataStoreIdentitySingletonId] = 1)
                INSERT INTO [dms].[DataStoreIdentity] ([DataStoreIdentitySingletonId], [SourceIdentity])
                VALUES (1, '{{CdcConnectorTemplatePinnedImageTestData.SourceIdentity}}');
            {{createSourceTablesSql}}
            IF OBJECT_ID(N'[dms].[DocumentProjectionWork]', N'U') IS NULL
                CREATE TABLE [dms].[DocumentProjectionWork] ([DocumentId] bigint NOT NULL PRIMARY KEY);
            IF SUSER_ID(N'{{ConnectorDatabaseUser}}') IS NULL
                CREATE LOGIN {{SqlServerBracketIdentifier(ConnectorDatabaseUser)}}
                WITH PASSWORD = '{{SqlServerLiteralValue(ConnectorDatabasePassword)}}', CHECK_POLICY = OFF;
            IF USER_ID(N'{{ConnectorDatabaseUser}}') IS NULL
                CREATE USER {{SqlServerBracketIdentifier(ConnectorDatabaseUser)}}
                FOR LOGIN {{SqlServerBracketIdentifier(ConnectorDatabaseUser)}};
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

        if (!response.IsSuccessStatusCode)
        {
            CdcConnectorTemplatePinnedImageSmokeDiagnostics.Fail(
                CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
                    code: CdcConnectorTemplateDiagnosticCodes.PinnedImageRuntimeClassLoadFailure,
                    category: CdcConnectorTemplateDiagnosticCategory.TransformConfigurationViolation,
                    provider: Provider,
                    propertyName: "pinnedImage.connectorPlugins",
                    safeArtifactOrObjectName: new CdcSafeName(ConnectContainerName),
                    expectedValue: "successful HTTP status",
                    observedValue: response.StatusCode.ToString(),
                    redactionClassification: CdcConnectorTemplateRedactionClassification.Safe
                ),
                "Kafka Connect plugin discovery failed. Plugin discovery output is redacted."
            );
        }

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

        if (!response.IsSuccessStatusCode)
        {
            CdcConnectorTemplatePinnedImageSmokeDiagnostics.Fail(
                CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
                    code: CdcConnectorTemplateDiagnosticCodes.PinnedImageOffsetProgressFailure,
                    category: CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch,
                    provider: request.Provider,
                    propertyName: "kafkaConnect.committedOffset",
                    safeArtifactOrObjectName: request.ConnectorName,
                    expectedValue: "successful HTTP status",
                    observedValue: response.StatusCode.ToString(),
                    redactionClassification: CdcConnectorTemplateRedactionClassification.Safe
                ),
                "Kafka Connect offset read failed. Offset read output is redacted."
            );
        }

        using JsonDocument document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("offsets", out JsonElement offsets))
        {
            CdcConnectorTemplatePinnedImageSmokeDiagnostics.Fail(
                CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
                    code: CdcConnectorTemplateDiagnosticCodes.PinnedImageOffsetProgressFailure,
                    category: CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch,
                    provider: request.Provider,
                    propertyName: "kafkaConnect.committedOffset",
                    safeArtifactOrObjectName: request.ConnectorName,
                    expectedValue: "offsets array",
                    observedValue: "missing",
                    redactionClassification: CdcConnectorTemplateRedactionClassification.Safe
                ),
                "Kafka Connect offset read response did not include an offsets array."
            );
        }

        return TrySelectCommittedSourceOffset(request, offsets);
    }

    internal static CdcConnectorSourceOffsetSnapshot? TrySelectCommittedSourceOffset(
        CdcConnectorTemplateRequest request,
        JsonElement offsets
    )
    {
        List<JsonElement> matchingOffsetDocuments = [];
        foreach (JsonElement offsetDocument in offsets.EnumerateArray())
        {
            if (
                offsetDocument.TryGetProperty("partition", out JsonElement partition)
                && SourcePartitionMatches(request, partition)
            )
            {
                matchingOffsetDocuments.Add(offsetDocument);
            }
        }

        if (matchingOffsetDocuments.Count > 1)
        {
            CdcConnectorTemplatePinnedImageSmokeDiagnostics.Fail(
                CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
                    code: CdcConnectorTemplateDiagnosticCodes.PinnedImageOffsetProgressFailure,
                    category: CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch,
                    provider: request.Provider,
                    propertyName: "kafkaConnect.sourcePartition",
                    safeArtifactOrObjectName: request.ConnectorName,
                    expectedValue: "single committed source offset partition",
                    observedValue: matchingOffsetDocuments.Count.ToString(CultureInfo.InvariantCulture),
                    redactionClassification: CdcConnectorTemplateRedactionClassification.Safe
                ),
                "Kafka Connect returned more than one committed source offset partition for the rendered connector."
            );
        }

        if (matchingOffsetDocuments.Count == 0)
        {
            return null;
        }

        JsonElement matchingOffsetDocument = matchingOffsetDocuments[0];
        if (
            !matchingOffsetDocument.TryGetProperty("partition", out JsonElement matchingPartition)
            || !matchingOffsetDocument.TryGetProperty("offset", out JsonElement offset)
            || ReadCommittedProviderOffsetPosition(request.Provider, offset)
                is not CdcConnectorProviderOffsetPosition providerPosition
        )
        {
            return null;
        }

        return new CdcConnectorSourceOffsetSnapshot(
            CanonicalizeJson(offset),
            BuildSourcePartitionEvidence(matchingPartition),
            providerPosition
        );
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

    internal static bool CommittedSourceOffsetAdvances(
        CdcProvider provider,
        string startingOffsetJson,
        string observedOffsetJson
    )
    {
        CdcConnectorProviderOffsetPosition? startingPosition = ReadCommittedProviderOffsetPosition(
            provider,
            startingOffsetJson
        );
        CdcConnectorProviderOffsetPosition? observedPosition = ReadCommittedProviderOffsetPosition(
            provider,
            observedOffsetJson
        );

        return startingPosition is not null
            && observedPosition is not null
            && observedPosition.CompareTo(startingPosition) > 0;
    }

    private static bool SourceOffsetAdvances(
        CdcConnectorSourceOffsetSnapshot startingOffset,
        CdcConnectorSourceOffsetSnapshot observedOffset
    ) => observedOffset.ProviderPosition.CompareTo(startingOffset.ProviderPosition) > 0;

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
        TryReadPostgresqlLsnProcJsonProperty(offset, "lsn_proc", out ulong lsnProc)
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

    private static bool TryReadPostgresqlLsnProcJsonProperty(
        JsonElement element,
        string propertyName,
        out ulong value
    )
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            value = 0;
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            if (property.TryGetUInt64(out value))
            {
                return true;
            }

            if (property.TryGetInt64(out long signedValue) && signedValue < 0)
            {
                value = unchecked((ulong)signedValue);
                return true;
            }

            value = 0;
            return false;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            string? stringValue = property.GetString();
            if (ulong.TryParse(stringValue, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            if (
                long.TryParse(
                    stringValue,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out long signedValue
                )
                && signedValue < 0
            )
            {
                value = unchecked((ulong)signedValue);
                return true;
            }

            value = 0;
            return false;
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
                )
                && value >= 0;
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
            !TryParseSqlServerLsnPart(parts[0], expectedLength: 8, out ulong first)
            || !TryParseSqlServerLsnPart(parts[1], expectedLength: 8, out ulong second)
            || !TryParseSqlServerLsnPart(parts[2], expectedLength: 4, out ulong third)
        )
        {
            return false;
        }

        value = new SqlServerConnectorLsn(first, second, third);
        return true;
    }

    private static bool TryParseSqlServerLsnPart(string part, int expectedLength, out ulong value)
    {
        if (part.Length != expectedLength || !part.All(IsAsciiHexDigit))
        {
            value = 0;
            return false;
        }

        return ulong.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsAsciiHexDigit(char value) =>
        value is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

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

    internal static string SanitizeForAssertion(string value) =>
        value.Replace(ConnectorDatabasePassword, "[redacted]", StringComparison.Ordinal);

    private static string SingleQuote(string value) => $"'{EscapeSingleQuotedShell(value)}'";

    private static string EscapeSingleQuotedShell(string value) =>
        value.Replace("'", "'\"'\"'", StringComparison.Ordinal);

    private static string SqlServerLiteralValue(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static string SqlServerBracketIdentifier(string value) =>
        $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static CoreCdc.CdcBinding BuildBinding(CdcProvider provider)
    {
        CoreCdc.CdcArtifactInventory artifactInventory = BuildCoreArtifactInventory(provider);

        return new CoreCdc.CdcBinding(
            CoreCdc.CdcJsonContract.CurrentContractVersion,
            DeploymentKey,
            CoreCdc.CdcTargetValidator.DefaultBindingTenantKey,
            "1",
            InstanceKey,
            BindingGeneration,
            ToCoreProvider(provider),
            CdcConnectorTemplatePinnedImageTestData.SourceFingerprint(provider).Value,
            artifactInventory.ConnectorName,
            artifactInventory.TopicName,
            PartitionCount: 1,
            CoreCdc.CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
            CoreCdc.CdcJsonContract.CurrentContractVersion
        );
    }

    private static CoreCdc.CdcArtifactInventory BuildCoreArtifactInventory(CdcProvider provider)
    {
        CoreCdc.CdcArtifactNameResult result = CoreCdc.CdcArtifactNameGenerator.Render(
            new CoreCdc.CdcArtifactNameInput(
                DeploymentKey,
                TopicPrefix,
                InstanceKey,
                BindingGeneration,
                ToCoreProvider(provider)
            )
        );

        return result.Inventory
            ?? throw new ArgumentException("Invalid pinned-image CDC artifact input.", nameof(provider));
    }

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

    private static CoreCdc.CdcProvider ToCoreProvider(CdcProvider provider) =>
        provider switch
        {
            CdcProvider.Postgresql => CoreCdc.CdcProvider.Postgresql,
            CdcProvider.SqlServer => CoreCdc.CdcProvider.SqlServer,
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

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
                        BuildColumn(provider, "HeartbeatId", "smallint"),
                        BuildColumn(provider, "HeartbeatSequence", "bigint", 2),
                        BuildColumn(provider, "HeartbeatAt", "timestamp with time zone", 3),
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
        string providerDataType = "text",
        int ordinal = 1
    ) =>
        new(
            new DbColumnName(columnName),
            provider == CdcProvider.Postgresql ? $"\"{columnName}\"" : $"[{columnName}]",
            ordinal,
            providerDataType,
            IsNullable: false
        );

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
        string PrimaryKeyColumnName,
        IReadOnlyList<SqlServerSourceColumnDefinition> CapturedColumns,
        IReadOnlyList<string>? AdditionalTableConstraintsInput = null
    )
    {
        public IReadOnlyList<string> AdditionalTableConstraints { get; } =
            AdditionalTableConstraintsInput ?? [];

        public string SourcePrimaryKeyName => $"PK_{SourceTableName}";
    }

    private sealed record SqlServerSourceColumnDefinition(
        string ColumnName,
        string ProviderDataType,
        bool IsNullable = false
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
    internal const string SourceIdentity = "f81d4fae-7dec-11d0-a765-00a0c91e6bf6";

    public static CdcSourceFingerprint SourceFingerprint(CdcProvider provider) =>
        CdcSourceFingerprintMetadata.Compute(provider, SourceIdentity);
}

internal static class CdcConnectorTemplatePinnedImageSmokeDiagnostics
{
    public static CdcConnectorTemplateDiagnostic Build(
        string code,
        CdcConnectorTemplateDiagnosticCategory category,
        CdcProvider provider,
        string propertyName,
        CdcSafeName? safeArtifactOrObjectName,
        string? expectedValue,
        string? observedValue,
        CdcConnectorTemplateRedactionClassification redactionClassification
    ) =>
        new(
            code,
            category,
            CdcConnectorTemplateDiagnosticSeverity.Error,
            propertyName,
            safeArtifactOrObjectName,
            SanitizeValue(expectedValue),
            SanitizeValue(observedValue),
            provider,
            CdcConnectorTemplateSourcePhase.PinnedImageSmoke,
            redactionClassification
        );

    public static string FormatDiagnostics(IReadOnlyList<CdcConnectorTemplateDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return diagnostics.Count == 0 ? "<none>" : string.Join("; ", diagnostics.Select(FormatDiagnostic));
    }

    public static string FormatFailureMessage(string message, CdcConnectorTemplateDiagnostic diagnostic) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{CdcConnectorTemplatePinnedImageFixture.SanitizeForAssertion(message)} Diagnostic: {FormatDiagnostic(diagnostic)}"
        );

    [DoesNotReturn]
    public static void Fail(CdcConnectorTemplateDiagnostic diagnostic, string message) =>
        throw new CdcConnectorTemplatePinnedImageSmokeAssertionException(message, diagnostic);

    public static string FormatDiagnostic(CdcConnectorTemplateDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        var payload = new SortedDictionary<string, string?>(StringComparer.Ordinal)
        {
            ["category"] = diagnostic.Category.ToString(),
            ["code"] = diagnostic.Code,
            ["expectedValue"] = SanitizeValue(diagnostic.ExpectedValue),
            ["observedValue"] = SanitizeValue(diagnostic.ObservedValue),
            ["propertyName"] = diagnostic.PropertyName,
            ["provider"] = diagnostic.Provider.ToString(),
            ["redactionClassification"] = diagnostic.RedactionClassification.ToString(),
            ["safeArtifactOrObjectName"] = diagnostic.SafeArtifactOrObjectName?.Value,
            ["severity"] = diagnostic.Severity.ToString(),
            ["sourcePhase"] = diagnostic.SourcePhase.ToString(),
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string? SanitizeValue(string? value) =>
        value is null ? null : CdcConnectorTemplatePinnedImageFixture.SanitizeForAssertion(value);
}

internal sealed class CdcConnectorTemplatePinnedImageSmokeAssertionException : AssertionException
{
    public CdcConnectorTemplatePinnedImageSmokeAssertionException(
        string message,
        CdcConnectorTemplateDiagnostic diagnostic
    )
        : base(CdcConnectorTemplatePinnedImageSmokeDiagnostics.FormatFailureMessage(message, diagnostic))
    {
        Diagnostic = diagnostic;
    }

    public CdcConnectorTemplateDiagnostic Diagnostic { get; }
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
                provider,
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
            provider,
            $"CDC connector template pinned-image smoke prerequisites are not configured. Missing: {missing}. Set {FailFastVariable}=true in the qualification lane to fail instead of skipping."
        );
    }

    public async Task StopOnPrerequisiteFailureAsync(CdcProvider provider, Task prerequisite, string message)
    {
        try
        {
            await prerequisite;
        }
        catch (Exception ex) when (ex is not AssertionException and not OperationCanceledException)
        {
            StopOnPrerequisiteFailure(provider, $"{message} Failure details are redacted.");
        }
    }

    public void StopOnPrerequisiteFailure(CdcProvider provider, string message)
    {
        CdcConnectorTemplateDiagnostic diagnostic = CdcConnectorTemplatePinnedImageSmokeDiagnostics.Build(
            code: CdcConnectorTemplateDiagnosticCodes.PinnedImageDockerPrerequisiteFailure,
            category: CdcConnectorTemplateDiagnosticCategory.MissingRequiredInput,
            provider: provider,
            propertyName: "pinnedImage.prerequisite",
            safeArtifactOrObjectName: null,
            expectedValue: "configured pinned-image smoke prerequisites",
            observedValue: "[redacted]",
            redactionClassification: CdcConnectorTemplateRedactionClassification.SecretValue
        );
        string formattedMessage = CdcConnectorTemplatePinnedImageSmokeDiagnostics.FormatFailureMessage(
            message,
            diagnostic
        );

        if (FailFast)
        {
            Assert.Fail(formattedMessage);
            return;
        }

        Assert.Ignore(formattedMessage);
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
