// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using EdFi.DataManagementService.Backend.Cdc.Tests.Integration;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;
using PinnedImage = EdFi.DataManagementService.Backend.Cdc.Tests.Integration.CdcConnectorTemplatePinnedImageFixture;
using TestData = EdFi.DataManagementService.Backend.Cdc.Tests.Integration.CdcConnectorTemplatePinnedImageTestData;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Integration;

/// <summary>
/// Broker-backed harness for the CDC control plane: an authorizer-enabled Kafka broker, the pinned
/// Kafka Connect worker with its Jolokia metrics bridge, and a PostgreSQL provider, wired to the
/// production adapters rather than to fakes.
/// </summary>
/// <remarks>
/// <para>
/// The authorizer is the reason this fixture exists. A local PLAINTEXT broker has none, so every ACL
/// item reports <c>NotApplicable</c> and the fail-closed grants are never exercised; the unit suites
/// can only prove what the adapter does with an admin client's answers, never that a real broker
/// answers that way. Topic configuration, effective grants, offset JSON shape, and Debezium's metric
/// object names are all broker and worker behavior, so they are proven here or nowhere.
/// </para>
/// <para>
/// Docker orchestration, mapped-port parsing, and output redaction are reused from the pinned-image
/// connector-template fixture rather than reimplemented, as are the minimal PostgreSQL source objects
/// and the source-table inventory that provider setup runs against. Only the topology differs, and it
/// differs for reasons the template lane does not share: the authorizer, the Jolokia bridge, and a
/// broker listener the test process itself can reach.
/// </para>
/// <para>
/// PostgreSQL is the only provider here. Every claim this fixture makes is about Kafka, Connect, and
/// Debezium rather than about a provider's capture artifacts, and the per-provider readiness and
/// lifecycle sequences are proven in their own suites against their own databases.
/// </para>
/// </remarks>
internal sealed class CdcControlBrokerFixture : IAsyncDisposable
{
    /// <summary>
    /// The authorizer-enabled broker image. It is deliberately not the connector-template lane's
    /// Redpanda broker: the deployment runs Apache Kafka (<c>eng/docker-compose/kafka.yml</c>), and
    /// the grants and patterns asserted here are that authorizer's behavior.
    /// </summary>
    private const string BrokerImageVariable = "CDC_CONTROL_BROKER_KAFKA_IMAGE";

    private const string DeploymentKey = "dms-control";
    private const string InstanceKey = "binding";
    private const string TopicPrefix = "edfi.control";
    private const long BindingGeneration = 3;

    /// <summary>
    /// The public topic's partition count. Exposed because it is the binding record's value, which the
    /// policy passes now take from their caller rather than from configuration; the fixture configures
    /// the same number, so a binding created here records it.
    /// </summary>
    internal const int BindingPartitionCount = 3;

    private const int PartitionCount = BindingPartitionCount;

    /// <summary>
    /// The record-size budget. Apache Kafka's default <c>replica.fetch.max.bytes</c> is one mebibyte,
    /// so this is the largest budget a stock broker verifiably accepts — which is the point: the
    /// broker's own defaults decide the answer, and the suite proves both sides of that boundary.
    /// </summary>
    internal const int MaxRecordBytes = 1_048_576;

    /// <summary>A budget no stock broker's replica-fetch limits accept, used for the fail-closed case.</summary>
    internal const int OversizedRecordBytes = 4_194_304;

    /// <summary>
    /// Generation of the throwaway binding the oversized record-size case provisions. A separate
    /// generation names separate topics, so proving that case never mutates the binding the rest of
    /// the suite asserts on.
    /// </summary>
    internal const long OversizedGeneration = BindingGeneration + 1;

    /// <summary>
    /// The connector's Kafka identity, in the broker's typed form. Deliberately unlike the connector's
    /// DATABASE identity, which this fixture takes from
    /// <see cref="CdcControlBrokerPinnedImage.ConnectorDatabaseUser"/>: the two authorities name the
    /// same component differently, and the fixture proves the control plane can be configured for both
    /// rather than forcing one value to satisfy each.
    /// </summary>
    internal const string ConnectorKafkaPrincipal = "User:dms-cdc-connector";

    internal const string ConnectWorkerPrincipal = "User:dms-cdc-connect-worker";
    internal const string ConsumerPrincipal = "User:dms-cdc-consumer";
    internal const string ConsumerGroup = "dms-cdc-consumer-group";

    /// <summary>
    /// The broker authenticates nobody on a PLAINTEXT listener, so every client — this test process,
    /// the Connect worker, and the connector — arrives as this principal. It is a superuser so the
    /// pipeline runs, which leaves the authorizer free to be exactly what is under test: the grants
    /// the adapter creates and reads back are real ACLs stored and returned by a real authorizer.
    /// </summary>
    private const string BrokerSuperUser = "User:ANONYMOUS";

    private static readonly TimeSpan BrokerStartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProviderStartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ConnectStartupTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ConnectorRunningTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan HeartbeatProgressTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan TopicDeletionTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AclVisibilityTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TopicConfigVisibilityTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly CdcConnectorTemplateSmokeSettings _settings;
    private readonly IDockerCli _docker;
    private readonly string _resourcePrefix;
    private readonly string _brokerImage;
    private readonly int _brokerHostPort;
    private readonly ServiceProvider _serviceProvider;

    private IAdminClient? _adminClient;
    private int _providerHostPort;

    private CdcControlBrokerFixture(
        CdcConnectorTemplateSmokeSettings settings,
        string brokerImage,
        IDockerCli docker,
        string resourcePrefix,
        int brokerHostPort
    )
    {
        _settings = settings;
        _brokerImage = brokerImage;
        _docker = docker;
        _resourcePrefix = resourcePrefix;
        _brokerHostPort = brokerHostPort;
        _serviceProvider = new ServiceCollection()
            .AddHttpClient()
            .AddCdcConnectorTemplates()
            .AddCdcProviderSetup()
            .BuildServiceProvider();

        Inventory = BuildInventory();
        ControlOptions = BuildControlOptions();
        ObservationContext = new(
            OperationId,
            new(
                Inventory.DeploymentKey,
                CoreCdc.CdcTargetValidator.DefaultBindingTenantKey,
                "1",
                Inventory.InstanceKey,
                Inventory.Generation,
                Inventory.Provider
            ),
            TestData.SourceFingerprint(CdcProvider.Postgresql).Value
        );
    }

    internal const string OperationId = "cdc-control-broker-backed";

    /// <summary>The binding's governed artifact names, which every adapter call is scoped to.</summary>
    public CoreCdc.CdcArtifactInventory Inventory { get; }

    /// <summary>
    /// The deployment policy the adapters read. It is mutated in place only by
    /// <see cref="WithControlOptions"/>, which hands each variant adapter its own copy.
    /// </summary>
    public CdcControlOptions ControlOptions { get; }

    public CdcObservationContext ObservationContext { get; }

    public string ConnectorName => Inventory.ConnectorName;

    public string OffsetStoreTopicName => ControlOptions.ConnectOffsetStorageTopic;

    /// <summary>The raw admin client, for seeding grants the deployment owns and for direct read-back.</summary>
    public IAdminClient AdminClient =>
        _adminClient ?? throw new InvalidOperationException("The broker fixture has not been started.");

    // The concrete adapter rather than ICdcKafkaAdmin: topic and ACL resolution are not on the
    // production interface, because no control-plane path calls either on its own, and a broker-backed
    // test that verifies one of them in isolation needs to reach it.
    public CdcKafkaAdminAdapter KafkaAdmin => BuildKafkaAdmin(ControlOptions);

    public ICdcConnectClient Connect => BuildConnectClient(ControlOptions);

    public ICdcConnectorLagReader LagReader =>
        new CdcConnectorJolokiaLagReader(
            _serviceProvider.GetRequiredService<IHttpClientFactory>(),
            Options.Create(ControlOptions),
            NullLogger<CdcConnectorJolokiaLagReader>.Instance
        );

    private string NetworkName => $"{_resourcePrefix}-network";

    private string BrokerContainerName => $"{_resourcePrefix}-broker";

    private string ConnectContainerName => $"{_resourcePrefix}-connect";

    private string ProviderContainerName => $"{_resourcePrefix}-provider";

    /// <summary>The bootstrap address the containers use; the test process uses the mapped host port.</summary>
    private string InternalBootstrapServers => $"{BrokerContainerName}:9092";

    public static async Task<CdcControlBrokerFixture> StartAsync(CancellationToken cancellationToken)
    {
        CdcConnectorTemplateSmokeSettings settings = CdcConnectorTemplateSmokeSettings.FromEnvironment(
            CdcProvider.Postgresql
        );
        string brokerImage = Environment.GetEnvironmentVariable(BrokerImageVariable) ?? string.Empty;
        StopIfNotConfigured(settings, brokerImage);

        var docker = new DockerCli();
        await settings.StopOnPrerequisiteFailureAsync(
            CdcProvider.Postgresql,
            docker.RequireDockerAsync(cancellationToken),
            "Docker CLI is unavailable or the Docker daemon is not reachable."
        );

        CdcControlBrokerFixture fixture = new(
            settings,
            brokerImage,
            docker,
            $"dms-cdc-control-{Guid.NewGuid():N}",
            ReserveHostPort()
        );

        try
        {
            await fixture.StartDockerResourcesAsync(cancellationToken);
            return fixture;
        }
        catch (Exception ex) when (ex is not AssertionException)
        {
            await fixture.DisposeAfterStartupFailureAsync();
            if (ex is OperationCanceledException)
            {
                throw;
            }

            settings.StopOnPrerequisiteFailure(
                CdcProvider.Postgresql,
                "Broker-backed CDC control fixture prerequisites are not ready. Failure details are redacted."
            );
            throw;
        }
    }

    /// <summary>
    /// A Kafka admin adapter over the same broker with one policy value changed, so a fail-closed case
    /// is proven against the deployment's real evidence rather than against a second broker.
    /// </summary>
    public CdcKafkaAdminAdapter KafkaAdminWith(Action<CdcControlOptions> configure) =>
        BuildKafkaAdmin(WithControlOptions(configure));

    /// <summary>
    /// Grants the Connect worker principal the offset store's read, write, and describe. Provisioning
    /// this is the deployment's job, not the control plane's: <c>EnsureConnectOffsetStoreAsync</c>
    /// reports the shared store's grants and never repairs them, because the store is worker state
    /// for every binding rather than one binding's artifact.
    /// </summary>
    public Task GrantConnectWorkerOffsetStoreAclsAsync(CancellationToken cancellationToken) =>
        CreateTopicAclsAsync(
            OffsetStoreTopicName,
            ConnectWorkerPrincipal,
            [AclOperation.Read, AclOperation.Write, AclOperation.Describe],
            ResourcePatternType.Literal,
            cancellationToken
        );

    public async Task CreateTopicAclsAsync(
        string topicName,
        string principal,
        IReadOnlyList<AclOperation> operations,
        ResourcePatternType patternType,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        await AdminClient.CreateAclsAsync([
            .. operations.Select(operation => new AclBinding
            {
                Pattern = new ResourcePattern
                {
                    Type = ResourceType.Topic,
                    Name = topicName,
                    ResourcePatternType = patternType,
                },
                Entry = new AccessControlEntry
                {
                    Principal = principal,
                    Host = "*",
                    Operation = operation,
                    PermissionType = AclPermissionType.Allow,
                },
            }),
        ]);

        await WaitForAclsAsync(
            ResourceType.Topic,
            topicName,
            principal,
            patternType,
            operations,
            cancellationToken
        );
    }

    public async Task DeleteTopicAclsAsync(
        string topicName,
        string principal,
        ResourcePatternType patternType,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        await AdminClient.DeleteAclsAsync([
            new AclBindingFilter
            {
                PatternFilter = new ResourcePatternFilter
                {
                    Type = ResourceType.Topic,
                    Name = topicName,
                    ResourcePatternType = patternType,
                },
                EntryFilter = new AccessControlEntryFilter
                {
                    Principal = principal,
                    Operation = AclOperation.Any,
                    PermissionType = AclPermissionType.Any,
                },
            },
        ]);

        await WaitForAclsAsync(ResourceType.Topic, topicName, principal, patternType, [], cancellationToken);
    }

    public async Task CreateConsumerGroupAclAsync(
        string consumerGroup,
        string principal,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        await AdminClient.CreateAclsAsync([
            new AclBinding
            {
                Pattern = new ResourcePattern
                {
                    Type = ResourceType.Group,
                    Name = consumerGroup,
                    ResourcePatternType = ResourcePatternType.Literal,
                },
                Entry = new AccessControlEntry
                {
                    Principal = principal,
                    Host = "*",
                    Operation = AclOperation.Read,
                    PermissionType = AclPermissionType.Allow,
                },
            },
        ]);

        await WaitForAclsAsync(
            ResourceType.Group,
            consumerGroup,
            principal,
            ResourcePatternType.Literal,
            [AclOperation.Read],
            cancellationToken
        );
    }

    /// <summary>
    /// Waits for a principal's grants on one resource pattern to match what was just written. The
    /// authorizer acknowledges a create or delete before every broker has applied the metadata
    /// record, so an immediate describe can miss a grant that was accepted - the same
    /// acknowledged-before-applied window <see cref="WaitForTopicAbsentAsync"/> covers for topic
    /// deletion. Without this wait a positive case reads as a missing grant, and a fail-closed case
    /// passes vacuously because the grant it was meant to observe had not landed yet.
    /// </summary>
    private async Task WaitForAclsAsync(
        ResourceType resourceType,
        string resourceName,
        string principal,
        ResourcePatternType patternType,
        IReadOnlyList<AclOperation> expectedOperations,
        CancellationToken cancellationToken
    )
    {
        AclBindingFilter filter = new()
        {
            PatternFilter = new ResourcePatternFilter
            {
                Type = resourceType,
                Name = resourceName,
                ResourcePatternType = patternType,
            },
            EntryFilter = new AccessControlEntryFilter
            {
                Principal = principal,
                Operation = AclOperation.Any,
                PermissionType = AclPermissionType.Any,
            },
        };
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(AclVisibilityTimeout);

        while (true)
        {
            DescribeAclsResult described = await AdminClient.DescribeAclsAsync(filter);
            HashSet<AclOperation> observed =
            [
                .. (described?.AclBindings ?? []).Select(binding => binding.Entry.Operation),
            ];

            if (observed.SetEquals(expectedOperations))
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Grants for {principal} on {resourceType} '{resourceName}' did not settle to "
                        + $"[{string.Join(", ", expectedOperations)}] within {AclVisibilityTimeout}; "
                        + $"the authorizer reports [{string.Join(", ", observed)}]."
                );
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// Sets an explicit topic-level configuration override, which is how a deployment supplies the
    /// governed values the Connect worker does not set on the internal topics it creates for itself.
    /// The write is acknowledged before every broker has applied it, so the override is read back
    /// until it is the value a describe reports - otherwise the observation under test still sees the
    /// unset value and reports the nonconformance the override was supplied to clear.
    /// </summary>
    public async Task SetTopicConfigAsync(string topicName, string configName, string value)
    {
        await AdminClient.IncrementalAlterConfigsAsync(
            new Dictionary<ConfigResource, List<ConfigEntry>>
            {
                [new ConfigResource { Type = ResourceType.Topic, Name = topicName }] =
                [
                    new ConfigEntry
                    {
                        Name = configName,
                        Value = value,
                        IncrementalOperation = AlterConfigOpType.Set,
                    },
                ],
            }
        );

        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(TopicConfigVisibilityTimeout);

        while (true)
        {
            IReadOnlyDictionary<string, ConfigEntryResult> entries = await ReadTopicConfigAsync(topicName);

            if (
                entries.TryGetValue(configName, out ConfigEntryResult? entry)
                && string.Equals(entry.Value, value, StringComparison.Ordinal)
            )
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Topic '{topicName}' did not report {configName}={value} within "
                        + $"{TopicConfigVisibilityTimeout}."
                );
            }

            await Task.Delay(PollInterval);
        }
    }

    /// <summary>Reads one topic's explicit configuration straight from the broker.</summary>
    public async Task<IReadOnlyDictionary<string, ConfigEntryResult>> ReadTopicConfigAsync(string topicName)
    {
        List<DescribeConfigsResult> results = await AdminClient.DescribeConfigsAsync([
            new ConfigResource { Type = ResourceType.Topic, Name = topicName },
        ]);

        return results[0].Entries;
    }

    /// <summary>
    /// Waits for a deleted topic to leave cluster metadata. Kafka acknowledges a delete before the
    /// controller has removed the topic everywhere, so an immediate read can still see it.
    /// </summary>
    public async Task<bool> WaitForTopicAbsentAsync(string topicName, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(TopicDeletionTimeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!await TopicExistsAsync(topicName))
            {
                return true;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        return false;
    }

    public async Task<bool> TopicExistsAsync(string topicName)
    {
        Metadata metadata = await Task.Run(() => AdminClient.GetMetadata(TimeSpan.FromSeconds(30)));
        return metadata.Topics.Exists(topic =>
            string.Equals(topic.Topic, topicName, StringComparison.Ordinal)
            && topic.Error.Code == ErrorCode.NoError
        );
    }

    public async Task<IReadOnlyList<AclBinding>> DescribeTopicAclsAsync(string topicName)
    {
        DescribeAclsResult result = await AdminClient.DescribeAclsAsync(
            new AclBindingFilter
            {
                PatternFilter = new ResourcePatternFilter
                {
                    Type = ResourceType.Topic,
                    Name = topicName,
                    ResourcePatternType = ResourcePatternType.Match,
                },
                EntryFilter = new AccessControlEntryFilter
                {
                    Operation = AclOperation.Any,
                    PermissionType = AclPermissionType.Any,
                },
            }
        );

        return result.AclBindings;
    }

    /// <summary>
    /// Runs provider setup against the live database and renders the connector from the deployment
    /// policy, exactly as the enable sequence composes it.
    /// </summary>
    public async Task<CdcConnectorTemplateResult> RenderConnectorAsync(CancellationToken cancellationToken)
    {
        CdcProviderSetupResult providerSetup = await RunProviderSetupAsync(cancellationToken);

        CdcConnectorTemplateRequest request = new(
            BuildBinding(),
            new CdcConnectorProviderSetupEvidence(BindingGeneration, providerSetup),
            ControlOptions.ToDeploymentPolicy(),
            ControlOptions.ToProviderConnectionProperties(CdcProvider.Postgresql),
            ControlOptions.ToKafkaClientSecurityProperties()
        );

        CdcConnectorTemplateResult rendered = _serviceProvider
            .GetRequiredService<ICdcConnectorTemplateService>()
            .Render(request);

        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        rendered.Diagnostics.Should().BeEmpty();
        return rendered;
    }

    /// <summary>Waits for the registered connector and its tasks to reach a terminal expected state.</summary>
    public async Task<CdcConnectorStatus> WaitForConnectorStateAsync(
        string expectedState,
        CancellationToken cancellationToken
    )
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(ConnectorRunningTimeout);
        string observedState = "none";

        while (DateTimeOffset.UtcNow < deadline)
        {
            CdcConnectResult<CdcConnectorStatus> status = await Connect.GetConnectorStatusAsync(
                ConnectorName,
                cancellationToken
            );

            if (status.Value is { } value)
            {
                observedState = value.ConnectorState;
                bool tasksReady =
                    expectedState != "RUNNING"
                    || (
                        value.Tasks.Count > 0
                        && value.Tasks.All(task =>
                            string.Equals(task.State, "RUNNING", StringComparison.Ordinal)
                        )
                    );

                if (string.Equals(observedState, expectedState, StringComparison.Ordinal) && tasksReady)
                {
                    return value;
                }

                if (string.Equals(observedState, "FAILED", StringComparison.Ordinal))
                {
                    break;
                }
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            $"Connector '{ConnectorName}' did not reach {expectedState}. Last observed state: {observedState}."
        );
    }

    /// <summary>
    /// The heartbeat sequence Debezium's own <c>heartbeat.action.query</c> advances, which is the
    /// provider-side evidence that the connector is streaming rather than merely registered.
    /// </summary>
    public async Task<long> ReadHeartbeatSequenceAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(ProviderAdminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT "HeartbeatSequence" FROM "dms"."CdcHeartbeat" WHERE "HeartbeatId" = 1;
            """;

        object? value = await command.ExecuteScalarAsync(cancellationToken);
        value.Should().NotBeNull("the provider heartbeat singleton should hold exactly one row");
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public async Task<long> WaitForHeartbeatProgressAsync(
        long startingSequence,
        CancellationToken cancellationToken
    )
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(HeartbeatProgressTimeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            long observed = await ReadHeartbeatSequenceAsync(cancellationToken);
            if (observed > startingSequence)
            {
                return observed;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            $"The provider heartbeat sequence did not advance from {startingSequence.ToString(CultureInfo.InvariantCulture)}."
        );
    }

    /// <summary>Waits until the connector has committed at least one offset to the shared store.</summary>
    public async Task<CdcConnectorOffsets> WaitForCommittedOffsetsAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(HeartbeatProgressTimeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            CdcConnectResult<CdcConnectorOffsets> offsets = await Connect.GetConnectorOffsetsAsync(
                ConnectorName,
                cancellationToken
            );

            if (offsets.Value is { Entries.Count: > 0 } value)
            {
                return value;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            $"Connector '{ConnectorName}' committed no source offset within the allotted time."
        );
    }

    public async ValueTask DisposeAsync()
    {
        _adminClient?.Dispose();
        await _serviceProvider.DisposeAsync();

        if (_settings.KeepContainers)
        {
            return;
        }

        await _docker.RunAllowingFailureAsync(["rm", "-f", ConnectContainerName], CancellationToken.None);
        await _docker.RunAllowingFailureAsync(["rm", "-f", ProviderContainerName], CancellationToken.None);
        await _docker.RunAllowingFailureAsync(["rm", "-f", BrokerContainerName], CancellationToken.None);
        await _docker.RunAllowingFailureAsync(["network", "rm", NetworkName], CancellationToken.None);
    }

    private CdcControlOptions WithControlOptions(Action<CdcControlOptions> configure)
    {
        CdcControlOptions variant = BuildControlOptions();
        configure(variant);
        return variant;
    }

    private CdcKafkaAdminAdapter BuildKafkaAdmin(CdcControlOptions controlOptions) =>
        new CdcKafkaAdminAdapter(
            AdminClient,
            Options.Create(controlOptions),
            TimeProvider.System,
            NullLogger<CdcKafkaAdminAdapter>.Instance
        );

    private ICdcConnectClient BuildConnectClient(CdcControlOptions controlOptions) =>
        new CdcConnectRestAdapter(
            _serviceProvider.GetRequiredService<IHttpClientFactory>(),
            Options.Create(controlOptions),
            TimeProvider.System,
            NullLogger<CdcConnectRestAdapter>.Instance
        );

    private string ProviderAdminConnectionString =>
        $"Host=127.0.0.1;Port={_providerHostPort.ToString(CultureInfo.InvariantCulture)};Username=postgres;"
        + $"Password={PinnedImage.ConnectorDatabasePassword};Database={PinnedImage.PostgresqlDatabaseName}";

    private static CoreCdc.CdcArtifactInventory BuildInventory() => BuildInventory(BindingGeneration);

    /// <summary>The governed artifact names for another generation of the same binding identity.</summary>
    public static CoreCdc.CdcArtifactInventory BuildVariantInventory(long generation) =>
        BuildInventory(generation);

    private static CoreCdc.CdcArtifactInventory BuildInventory(long generation) =>
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
            .Inventory
        ?? throw new InvalidOperationException("The broker-backed CDC artifact names are invalid.");

    private CoreCdc.CdcBinding BuildBinding() =>
        new(
            CoreCdc.CdcJsonContract.CurrentContractVersion,
            DeploymentKey,
            CoreCdc.CdcTargetValidator.DefaultBindingTenantKey,
            "1",
            InstanceKey,
            BindingGeneration,
            CoreCdc.CdcProvider.Postgresql,
            TestData.SourceFingerprint(CdcProvider.Postgresql).Value,
            Inventory.ConnectorName,
            Inventory.TopicName,
            PartitionCount,
            CoreCdc.CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
            CoreCdc.CdcJsonContract.CurrentContractVersion
        );

    private CdcControlOptions BuildControlOptions() =>
        new()
        {
            DeploymentKey = DeploymentKey,
            InstanceKey = InstanceKey,
            TopicPrefix = TopicPrefix,
            Generation = BindingGeneration,
            PartitionCount = PartitionCount,
            KafkaBootstrapServers = HostBootstrapServers,
            ConnectBaseUri = ConnectBaseUri.AbsoluteUri,
            ConnectMetricsBaseUri = ConnectMetricsBaseUri.AbsoluteUri,
            ConnectWorkerKey = _resourcePrefix,
            ConnectOffsetStorageTopic = $"{_resourcePrefix}.connect.offsets",
            DurabilityProfile = CdcControlOptions.LocalDurabilityProfile,
            MaxRecordBytes = MaxRecordBytes,
            HeartbeatInterval = TimeSpan.FromSeconds(5),
            AclsEnabled = true,
            SetupPrincipal = "postgres",
            ConnectorDatabasePrincipal = PinnedImage.ConnectorDatabaseUser,
            ConnectorKafkaPrincipal = ConnectorKafkaPrincipal,
            ConnectWorkerPrincipal = ConnectWorkerPrincipal,
            Consumers = [new() { Principal = ConsumerPrincipal, ConsumerGroup = ConsumerGroup }],
            ProviderConnectionProperties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["database.hostname"] = ProviderContainerName,
                ["database.port"] = "5432",
                ["database.user"] = PinnedImage.ConnectorDatabaseUser,
                ["database.password"] =
                    $"${{{PinnedImage.EnvConfigProviderName}:{PinnedImage.ConnectorPasswordEnvironmentVariable}}}",
                ["database.dbname"] = PinnedImage.PostgresqlDatabaseName,
            },
            // The broker and Connect are reached over loopback; a status endpoint is never called from
            // this suite, but the options are validated as a whole so both must be well formed.
            DmsBaseUrl = "http://127.0.0.1:8080",
            DmsBearerToken = "broker-backed-suite",
        };

    private string HostBootstrapServers =>
        $"127.0.0.1:{_brokerHostPort.ToString(CultureInfo.InvariantCulture)}";

    private Uri ConnectBaseUri { get; set; } = new("http://127.0.0.1:8083");

    private Uri ConnectMetricsBaseUri { get; set; } = new("http://127.0.0.1:8778");

    private static void StopIfNotConfigured(CdcConnectorTemplateSmokeSettings settings, string brokerImage)
    {
        List<string> missingVariables = [];

        if (string.IsNullOrWhiteSpace(settings.ConnectImage))
        {
            missingVariables.Add("CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE");
        }
        else if (!settings.ConnectImage.Contains("@sha256:", StringComparison.Ordinal))
        {
            settings.StopOnPrerequisiteFailure(
                CdcProvider.Postgresql,
                "CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE must identify the qualified Ed-Fi Kafka Connect image by immutable digest."
            );
        }

        if (string.IsNullOrWhiteSpace(settings.ProviderImage))
        {
            missingVariables.Add("CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE");
        }

        if (string.IsNullOrWhiteSpace(brokerImage))
        {
            missingVariables.Add(BrokerImageVariable);
        }

        if (missingVariables.Count == 0)
        {
            return;
        }

        settings.StopOnPrerequisiteFailure(
            CdcProvider.Postgresql,
            $"Broker-backed CDC control prerequisites are not configured. Missing: {string.Join(", ", missingVariables)}."
        );
    }

    /// <summary>
    /// Reserves a host port before the broker starts. The broker must advertise the address the test
    /// process connects on, and an advertised listener cannot name a port Docker has not assigned yet,
    /// so the port is chosen here and published on both sides of the mapping.
    /// </summary>
    private static int ReserveHostPort()
    {
        using System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
        string externalPort = _brokerHostPort.ToString(CultureInfo.InvariantCulture);

        await _docker.RunAsync(
            [
                "run",
                "--detach",
                "--name",
                BrokerContainerName,
                "--network",
                NetworkName,
                "-p",
                $"127.0.0.1:{externalPort}:{externalPort}",
                "-e",
                "KAFKA_NODE_ID=1",
                "-e",
                "KAFKA_PROCESS_ROLES=broker,controller",
                "-e",
                $"KAFKA_LISTENERS=PLAINTEXT://0.0.0.0:9092,EXTERNAL://0.0.0.0:{externalPort},CONTROLLER://{BrokerContainerName}:9093",
                "-e",
                $"KAFKA_ADVERTISED_LISTENERS=PLAINTEXT://{InternalBootstrapServers},EXTERNAL://127.0.0.1:{externalPort}",
                "-e",
                "KAFKA_INTER_BROKER_LISTENER_NAME=PLAINTEXT",
                "-e",
                "KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT,EXTERNAL:PLAINTEXT",
                "-e",
                $"KAFKA_CONTROLLER_QUORUM_VOTERS=1@{BrokerContainerName}:9093",
                "-e",
                "KAFKA_CONTROLLER_LISTENER_NAMES=CONTROLLER",
                "-e",
                "KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1",
                "-e",
                "KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR=1",
                "-e",
                "KAFKA_TRANSACTION_STATE_LOG_MIN_ISR=1",
                "-e",
                "KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS=0",
                // The reason this fixture exists: a real authorizer, storing and answering for real
                // grants, so an ACL item can be something other than NotApplicable.
                "-e",
                "KAFKA_AUTHORIZER_CLASS_NAME=org.apache.kafka.metadata.authorizer.StandardAuthorizer",
                "-e",
                $"KAFKA_SUPER_USERS={BrokerSuperUser}",
                "-e",
                "KAFKA_ALLOW_EVERYONE_IF_NO_ACL_FOUND=false",
                _brokerImage,
            ],
            cancellationToken
        );

        await WaitForBrokerAsync(cancellationToken);
    }

    private async Task WaitForBrokerAsync(CancellationToken cancellationToken)
    {
        AdminClientConfig config = new() { BootstrapServers = HostBootstrapServers };
        IAdminClient adminClient = new AdminClientBuilder(config).Build();
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(BrokerStartupTimeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (adminClient.GetMetadata(TimeSpan.FromSeconds(5)).Brokers.Count > 0)
                {
                    _adminClient = adminClient;
                    return;
                }
            }
            catch (KafkaException)
            {
                // The broker is not accepting connections yet; the deadline governs how long that is
                // tolerated. The exception carries a bootstrap address, so it is never surfaced.
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        adminClient.Dispose();
        throw new InvalidOperationException(
            "The authorizer-enabled Kafka broker did not accept an admin connection within the allotted time."
        );
    }

    private async Task StartProviderAsync(CancellationToken cancellationToken)
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
                $"POSTGRES_DB={PinnedImage.PostgresqlDatabaseName}",
                "-e",
                "POSTGRES_USER=postgres",
                "-e",
                $"POSTGRES_PASSWORD={PinnedImage.ConnectorDatabasePassword}",
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

        DockerCommandResult mappedPort = await _docker.RunAsync(
            ["port", ProviderContainerName, "5432/tcp"],
            cancellationToken
        );
        _providerHostPort = PinnedImage.ParseMappedPort(
            ProviderContainerName,
            mappedPort.StandardOutput,
            "CDC provider"
        );

        await WaitForProviderAsync(cancellationToken);
        await CreateMinimalProviderObjectsAsync(cancellationToken);
    }

    private async Task WaitForProviderAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(ProviderStartupTimeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await using NpgsqlConnection connection = new(ProviderAdminConnectionString);
                await connection.OpenAsync(cancellationToken);
                return;
            }
            catch (NpgsqlException)
            {
                // The database is still starting. The message can carry the connection string, so it
                // is never surfaced; the deadline governs how long the wait is tolerated.
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            "The CDC provider database did not accept a connection within the allotted time."
        );
    }

    private async Task CreateMinimalProviderObjectsAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(ProviderAdminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = PinnedImage.BuildMinimalPostgresqlObjectsSql();
        await command.ExecuteNonQueryAsync(cancellationToken);
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
                "-p",
                $"127.0.0.1::{CdcConnectorJolokiaLagReader.JolokiaPort.ToString(CultureInfo.InvariantCulture)}",
                "-e",
                $"BOOTSTRAP_SERVERS={InternalBootstrapServers}",
                "-e",
                $"GROUP_ID={ControlOptions.ConnectWorkerKey}",
                "-e",
                $"CONFIG_STORAGE_TOPIC={_resourcePrefix}.connect.configs",
                "-e",
                $"OFFSET_STORAGE_TOPIC={ControlOptions.ConnectOffsetStorageTopic}",
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
                // The Jolokia agent the Debezium base image ships, on the port its entrypoint
                // hardcodes. It is the only source of the Debezium streaming quantiles.
                "-e",
                "ENABLE_JOLOKIA=true",
                "-e",
                PinnedImage.ConnectConfigProvidersEnvironmentVariable,
                "-e",
                PinnedImage.ConnectConfigProviderEnvClassEnvironmentVariable,
                "-e",
                $"{PinnedImage.ConnectorPasswordEnvironmentVariable}={PinnedImage.ConnectorDatabasePassword}",
                _settings.ConnectImage,
            ],
            cancellationToken
        );

        DockerCommandResult restPort = await _docker.RunAsync(
            ["port", ConnectContainerName, "8083/tcp"],
            cancellationToken
        );
        ConnectBaseUri = PinnedImage.ParseMappedConnectBaseUri(ConnectContainerName, restPort.StandardOutput);

        DockerCommandResult metricsPort = await _docker.RunAsync(
            [
                "port",
                ConnectContainerName,
                $"{CdcConnectorJolokiaLagReader.JolokiaPort.ToString(CultureInfo.InvariantCulture)}/tcp",
            ],
            cancellationToken
        );
        ConnectMetricsBaseUri = PinnedImage.ParseMappedConnectBaseUri(
            ConnectContainerName,
            metricsPort.StandardOutput
        );

        ControlOptions.ConnectBaseUri = ConnectBaseUri.AbsoluteUri;
        ControlOptions.ConnectMetricsBaseUri = ConnectMetricsBaseUri.AbsoluteUri;

        await WaitForKafkaConnectAsync(cancellationToken);
    }

    private async Task WaitForKafkaConnectAsync(CancellationToken cancellationToken)
    {
        using HttpClient client = new() { BaseAddress = ConnectBaseUri };
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(ConnectStartupTimeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync(
                    "/connector-plugins?connectorsOnly=false",
                    cancellationToken
                );
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The worker's REST endpoint is not listening yet.
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            "The pinned Kafka Connect worker did not answer its REST endpoint within the allotted time."
        );
    }

    private async Task<CdcProviderSetupResult> RunProviderSetupAsync(CancellationToken cancellationToken)
    {
        await using DbConnection connection = new NpgsqlConnection(ProviderAdminConnectionString);
        var executor = new DbConnectionCdcProviderDatabaseExecutor(connection);

        CdcProviderSetupRequest request = new(
            provider: CdcProvider.Postgresql,
            mode: CdcProviderSetupMode.InitialCreateOrExactMatch,
            boundPhysicalSourceFingerprint: TestData.SourceFingerprint(CdcProvider.Postgresql),
            setupPrincipal: new CdcSetupPrincipalContext(new CdcSafeName("postgres")),
            connectorPrincipal: new CdcConnectorPrincipal(new CdcSafeName(PinnedImage.ConnectorDatabaseUser)),
            artifactNames: CdcProviderArtifactNames.ForPostgresql(
                new CdcSafeName(Inventory.PostgresqlPublicationName!),
                new CdcSafeName(Inventory.PostgresqlLogicalSlotName!)
            ),
            artifactOutput: new CdcProviderArtifactOutputRequest(IncludeManifestPayload: false),
            expectedSourceInventory: PinnedImage.BuildRequiredSourceTableInventory(CdcProvider.Postgresql),
            dmsManagedTableInventory: PinnedImage.BuildDmsManagedTableInventory(CdcProvider.Postgresql),
            databaseExecutor: executor
        );

        CdcProviderSetupResult result = await _serviceProvider
            .GetRequiredService<ICdcProviderSetupService>()
            .SetupAsync(request, cancellationToken);

        result
            .Outcome.Should()
            .Be(
                CdcProviderSetupOutcome.CreatedOrMatched,
                "provider setup diagnostics were {0}",
                string.Join(",", result.Diagnostics.Select(diagnostic => diagnostic.Code))
            );
        return result;
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
                $"Broker-backed CDC control fixture cleanup failed after startup failure: {DockerCommandResult.Sanitize(ex.Message)}"
            );
        }
    }
}
