// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

/// <summary>
/// Broker qualification only: synthetic binding identities never claim database ownership/admission.
/// Real admin transport, policy, shared-store journal/startup gate and governed Kafka cleanup;
/// actual authenticated clients exercise access. No substitute provider, producer-capacity or ACL evidence.
/// SASL/PLAIN is confined to an isolated Docker network and loopback ports, with ephemeral credentials.
/// </summary>
internal sealed class CdcKafkaPolicyFixture(bool secured)
    : IAsyncDisposable,
        ICdcKafkaAuthorizationInspection,
        ICdcWorkerStartupTransport,
        ICdcKafkaProducerInspection
{
    private readonly DockerCli _docker = new();
    private readonly string _prefix = "cdc-policy-" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _password = Guid.NewGuid().ToString("N");
    private readonly int[] _ports = Enumerable.Range(0, secured ? 3 : 1).Select(_ => FreePort()).ToArray();
    private readonly int _workerPort = FreePort();
    private readonly int _metricsPort = FreePort();
    private readonly List<string> _containers = [];
    private readonly List<string> _volumes = [];
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "cdc-policy-" + Guid.NewGuid().ToString("N")
    );
    private bool _networkCreated;
    private IAdminClient _admin = null!;
    internal CdcKafkaAdminAdapter Adapter { get; private set; } = null!;
    internal CdcKafkaProvisioning Controller { get; private set; } = null!;
    internal string BootstrapServers => string.Join(',', _ports.Select(port => $"127.0.0.1:{port}"));
    internal string ConfigTopic => _prefix + ".configs";
    internal string StatusTopic => _prefix + ".status";
    internal string OffsetTopic => _prefix + ".offsets";
    internal string WorkerGroup => _prefix + ".worker";
    internal int WorkerLaunches { get; private set; }
    internal DateTimeOffset OffsetVerifiedAt { get; private set; }
    internal DateTimeOffset WorkerStartedAt { get; private set; }
    internal bool Secured => secured;

    private string Broker(int index) => _prefix + "-broker-" + (index + 1);

    private string Worker => _prefix + "-worker";

    internal async Task InitializeAsync(CancellationToken token)
    {
        await _docker.RequireDockerAsync(token);
        // No pull/skip fallback: qualification requires the pinned repository images locally.
        await DockerAsync(
            ["image", "inspect", CdcComposeBrokerSizeDeployment.BrokerImage, "--format", "{{.Id}}"],
            token
        );
        await DockerAsync(["image", "inspect", CdcQualifiedWorkerImage.Image, "--format", "{{.Id}}"], token);
        Directory.CreateDirectory(_root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                _root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
        await DockerAsync(["network", "create", _prefix], token);
        _networkCreated = true;
        for (int i = 0; i < _ports.Length; i++)
        {
            string volume = Broker(i) + "-data";
            await DockerAsync(["volume", "create", volume], token);
            _volumes.Add(volume);
            await StartNodeAsync(i, 1_048_576, token);
        }
        _admin = new AdminClientBuilder(new AdminClientConfig(Client("admin")))
            .SetLogHandler((_, _) => { })
            .SetErrorHandler((_, _) => { })
            .Build();
        await WaitAsync(
            () => Task.FromResult(_admin.GetMetadata(TimeSpan.FromSeconds(3)).Brokers.Count == _ports.Length),
            token
        );
        Adapter = Value(CdcKafkaAdminAdapter.Create(new AdminClientConfig(Client("admin")), this));
        // Neither shared-store preparation nor policy observation can initialize a projection runtime.
        Controller = new(_root, Adapter, null!, this);
        await WaitAsync(
            async () =>
                (await Adapter.InspectAclsAsync(Request(), token))
                is CdcTransportResult<CdcKafkaAclEvidence>.Observed,
            token
        );
    }

    private async Task StartNodeAsync(int index, int replicaFetchBytes, CancellationToken token)
    {
        List<string> args =
        [
            "run",
            "--detach",
            "--name",
            Broker(index),
            "--network",
            _prefix,
            "-p",
            $"127.0.0.1:{_ports[index]}:29092",
            "-v",
            Broker(index) + "-data:/tmp/kraft-combined-logs",
        ];
        Dictionary<string, string> env = new()
        {
            ["CLUSTER_ID"] = "MkU3OEVBNTcwNTJENDM2Qk",
            ["KAFKA_NODE_ID"] = (index + 1).ToString(CultureInfo.InvariantCulture),
            ["KAFKA_PROCESS_ROLES"] = "broker,controller",
            ["KAFKA_CONTROLLER_LISTENER_NAMES"] = "CONTROLLER",
            ["KAFKA_CONTROLLER_QUORUM_VOTERS"] = string.Join(
                ',',
                Enumerable.Range(0, _ports.Length).Select(i => $"{i + 1}@{Broker(i)}:9093")
            ),
            ["KAFKA_LISTENERS"] =
                $"INTERNAL://0.0.0.0:9092,EXTERNAL://0.0.0.0:29092,CONTROLLER://{Broker(index)}:9093",
            ["KAFKA_ADVERTISED_LISTENERS"] =
                $"INTERNAL://{Broker(index)}:9092,EXTERNAL://127.0.0.1:{_ports[index]}",
            ["KAFKA_LISTENER_SECURITY_PROTOCOL_MAP"] = secured
                ? "CONTROLLER:SASL_PLAINTEXT,INTERNAL:SASL_PLAINTEXT,EXTERNAL:SASL_PLAINTEXT"
                : "CONTROLLER:PLAINTEXT,INTERNAL:PLAINTEXT,EXTERNAL:PLAINTEXT",
            ["KAFKA_INTER_BROKER_LISTENER_NAME"] = "INTERNAL",
            ["KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR"] = _ports.Length.ToString(CultureInfo.InvariantCulture),
            ["KAFKA_OFFSETS_TOPIC_NUM_PARTITIONS"] = "1",
            ["KAFKA_MIN_INSYNC_REPLICAS"] = secured ? "2" : "1",
            ["KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR"] = _ports.Length.ToString(
                CultureInfo.InvariantCulture
            ),
            ["KAFKA_TRANSACTION_STATE_LOG_MIN_ISR"] = secured ? "2" : "1",
            ["KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS"] = "0",
            ["KAFKA_AUTO_CREATE_TOPICS_ENABLE"] = "false",
            ["KAFKA_REPLICA_FETCH_MAX_BYTES"] = replicaFetchBytes.ToString(CultureInfo.InvariantCulture),
            ["KAFKA_HEAP_OPTS"] = "-Xms256m -Xmx512m",
        };
        if (secured)
        {
            env["KAFKA_AUTHORIZER_CLASS_NAME"] = "org.apache.kafka.metadata.authorizer.StandardAuthorizer";
            env["KAFKA_ALLOW_EVERYONE_IF_NO_ACL_FOUND"] = "false";
            env["KAFKA_SUPER_USERS"] = "User:admin";
            env["KAFKA_SASL_ENABLED_MECHANISMS"] = "PLAIN";
            env["KAFKA_SASL_MECHANISM_INTER_BROKER_PROTOCOL"] = "PLAIN";
            env["KAFKA_SASL_MECHANISM_CONTROLLER_PROTOCOL"] = "PLAIN";
            string jaas =
                $"org.apache.kafka.common.security.plain.PlainLoginModule required username=\"admin\" password=\"{_password}\" "
                + string.Join(
                    ' ',
                    new[]
                    {
                        "admin",
                        "worker",
                        "connector-a",
                        "connector-b",
                        "consumer-a",
                        "consumer-b",
                        "outsider",
                    }.Select(user => $"user_{user}=\"{_password}\"")
                )
                + ";";
            foreach (string listener in new[] { "CONTROLLER", "INTERNAL", "EXTERNAL" })
            {
                env[$"KAFKA_LISTENER_NAME_{listener}_PLAIN_SASL_JAAS_CONFIG"] = jaas;
            }
        }
        foreach (var (key, value) in env)
        {
            args.AddRange(["-e", key + "=" + value]);
        }
        args.Add(CdcComposeBrokerSizeDeployment.BrokerImage);
        if (!_containers.Contains(Broker(index)))
        {
            _containers.Add(Broker(index));
        }

        await DockerAsync(args, token);
    }

    internal CdcDeploymentRequest Request(string instance = "a", bool production = true)
    {
        var binding = CdcConnectorTemplateTestData.BuildBinding(
            CdcProvider.SqlServer,
            deploymentKey: _prefix,
            instanceKey: instance,
            dataStoreId: instance == "a" ? "1" : "2"
        );
        var basis = CdcDeploymentRequestTestData.Request(
            CdcProvider.SqlServer,
            changeBinding: _ => binding,
            setupArtifacts: CdcDeploymentRequest.GetProviderArtifactNames(binding)
        );
        CdcWorkerDeploymentPolicy worker = new(
            new(WorkerGroup),
            new(OffsetTopic),
            CdcQualifiedWorkerImage.Digest,
            1_073_741_824,
            "All",
            secured && production
                ? CdcKafkaDurabilityProfile.Production
                : CdcKafkaDurabilityProfile.LocalSingleBroker,
            secured
                ? CdcKafkaAuthorizationProfile.AuthorizationEnabled
                : CdcKafkaAuthorizationProfile.AuthorizationDisabledLocal,
            new("User:worker"),
            new("User:connector-" + instance),
            new("User:admin"),
            [new(new("User:consumer-" + instance), new(_prefix + ".consumer-" + instance))]
        );
        return new(
            binding,
            basis.DmsSettings,
            basis.ProviderSetup,
            new($"http://127.0.0.1:{_workerPort}/"),
            new($"http://127.0.0.1:{_metricsPort}/metrics"),
            new(
                BootstrapServers,
                maxRecordBytes: 1_048_576,
                heartbeatInterval: TimeSpan.FromSeconds(1),
                sqlServerPollInterval: TimeSpan.FromSeconds(1)
            ),
            worker,
            basis.ProviderConnectionProperties,
            basis.KafkaClientSecurityProperties,
            new(
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(3),
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMinutes(1)
            )
        );
    }

    internal async Task<bool> BrokerDeniesAclDescriptionAsync(CancellationToken token)
    {
        const string script = """
            umask 077
            f=$(mktemp)
            trap 'rm -f "$f"' EXIT
            printf 'security.protocol=SASL_PLAINTEXT\nsasl.mechanism=PLAIN\nsasl.jaas.config=org.apache.kafka.common.security.plain.PlainLoginModule required username="consumer-a" password="%s";\n' "$PROBE_PASSWORD" > "$f"
            /opt/kafka/bin/kafka-acls.sh --bootstrap-server localhost:9092 --command-config "$f" --list
            """;
        var result = await _docker.RunAllowingFailureAsync(
            ["exec", "-e", "PROBE_PASSWORD=" + _password, Broker(0), "sh", "-ec", script],
            token
        );
        return result.ExitCode != 0
            && (result.StandardOutput + result.StandardError).Contains(
                "ClusterAuthorizationException",
                StringComparison.Ordinal
            );
    }

    internal ClientConfig Client(string user) =>
        new()
        {
            BootstrapServers = BootstrapServers,
            SecurityProtocol = secured ? SecurityProtocol.SaslPlaintext : SecurityProtocol.Plaintext,
            SaslMechanism = SaslMechanism.Plain,
            SaslUsername = user,
            SaslPassword = _password,
            SocketTimeoutMs = 10000,
            AllowAutoCreateTopics = false,
        };

    internal async Task ProvisionTopicsAsync(CdcDeploymentRequest request, CancellationToken token)
    {
        foreach (var topic in CdcDeploymentKafkaPolicy.Build(request).BindingTopics)
        {
            await CreateTopicAsync(request, topic, token);
        }
        await WaitAsync(
            async () =>
            {
                var acls = await Adapter.ReconcileMissingGrantsAsync(request, false, token);
                var validation = CdcDeploymentKafkaPolicy.ValidateAcls(request, acls, false);
                validation.UnsafeGrants.Should().BeFalse();
                return validation.State == CoreCdc.CdcKafkaPolicyItemState.Satisfied;
            },
            token
        );
    }

    internal async Task CreateTopicAsync(
        CdcDeploymentRequest request,
        CdcKafkaTopicIntent topic,
        CancellationToken token
    )
    {
        await WaitAsync(
            async () =>
                CdcDeploymentKafkaPolicy
                    .ObserveTopic(
                        request,
                        topic,
                        await Adapter.CreateMissingTopicAsync(request, topic, token)
                    )
                    .State == CoreCdc.CdcKafkaPolicyItemState.Satisfied,
            token
        );
        await WaitForMetadataAsync(
            topics =>
                topics.Exists(t =>
                    t.Topic == topic.Name
                    && t.Partitions.Count == topic.PartitionCount
                    && t.Partitions.TrueForAll(p => p.Replicas.Length == topic.ReplicationFactor)
                ),
            token
        );
    }

    internal Task PrepareOffsetsAsync(CdcDeploymentRequest request, CancellationToken token) =>
        WaitAsync(
            async () =>
                (await Controller.ProvisionOffsetStoreAsync(request, token))
                    is CdcTransportResult<CoreCdc.CdcConnectOffsetStorePolicyObservation>.Observed observed
                && observed.Value.PolicyState == CoreCdc.CdcConnectOffsetStorePolicyState.Satisfied,
            token
        );

    internal async Task<CdcKafkaDeploymentEvidence> EvidenceAsync(
        CdcDeploymentRequest request,
        CancellationToken token
    ) => await CdcKafkaProvisioning.InspectAsync(Adapter, this, request, false, false, token);

    public Task<CdcTransportResult<CdcKafkaProducerCapacityEvidence>> InspectAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) =>
        Task.FromResult<CdcTransportResult<CdcKafkaProducerCapacityEvidence>>(
            new CdcTransportResult<CdcKafkaProducerCapacityEvidence>.Unavailable(
                new(CdcDeploymentComponent.Worker, CdcDeploymentFailure.Unavailable)
            )
        );

    public async Task<CdcTransportResult<CdcKafkaAuthorizationDeploymentEvidence>> InspectAsync(
        IReadOnlyList<int> brokerIds,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (!brokerIds.Order().SequenceEqual(Enumerable.Range(1, _ports.Length)))
            {
                throw new InvalidDataException();
            }

            List<CdcKafkaBrokerAuthorization> brokers = [];
            for (int i = 0; i < _ports.Length; i++)
            {
                string before = await DockerAsync(
                    [
                        "inspect",
                        Broker(i),
                        "--format",
                        "{{.State.Running}} {{.State.StartedAt}} {{.RestartCount}} {{.Image}}",
                    ],
                    cancellationToken
                );
                if (!before.StartsWith("true ", StringComparison.Ordinal))
                {
                    throw new InvalidDataException();
                }
                // Read only allow-listed properties from the file passed to the running JVM. Never return JAAS credentials.
                const string script =
                    "count=0; for p in /proc/[0-9]*/cmdline; do if tr '\\000' '\\n' < \"$p\" | grep -Fx kafka.Kafka >/dev/null; then tr '\\000' '\\n' < \"$p\" | grep -Fx /opt/kafka/config/server.properties >/dev/null || exit 1; count=$((count + 1)); fi; done; test \"$count\" -eq 1; grep -E '^(node.id|authorizer.class.name|allow.everyone.if.no.acl.found|super.users|principal.builder.class)=' /opt/kafka/config/server.properties";
                string text = await DockerAsync(["exec", Broker(i), "sh", "-ec", script], cancellationToken);
                var properties = CdcComposeKafkaAuthorizationInspection.Parse(text);
                if (properties.ContainsKey("principal.builder.class"))
                {
                    throw new InvalidDataException();
                }

                int id = int.Parse(properties["node.id"], CultureInfo.InvariantCulture);
                bool enabled =
                    properties.TryGetValue("authorizer.class.name", out var authorizer)
                    && authorizer.Length > 0;
                if (
                    id != i + 1
                    || enabled && authorizer != "org.apache.kafka.metadata.authorizer.StandardAuthorizer"
                )
                {
                    throw new InvalidDataException();
                }

                brokers.Add(
                    new(
                        id,
                        enabled,
                        properties.TryGetValue("allow.everyone.if.no.acl.found", out var allow)
                            && bool.Parse(allow),
                        properties.TryGetValue("super.users", out var users)
                            ? users.Split(';', StringSplitOptions.RemoveEmptyEntries)
                            : []
                    )
                );
                (
                    await DockerAsync(
                        [
                            "inspect",
                            Broker(i),
                            "--format",
                            "{{.State.Running}} {{.State.StartedAt}} {{.RestartCount}} {{.Image}}",
                        ],
                        cancellationToken
                    )
                )
                    .Should()
                    .Be(before);
            }
            return new CdcTransportResult<CdcKafkaAuthorizationDeploymentEvidence>.Observed(
                new(true, brokers, [])
            );
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new CdcTransportResult<CdcKafkaAuthorizationDeploymentEvidence>.Unavailable(
                new(CdcDeploymentComponent.Kafka, CdcDeploymentFailure.Unavailable)
            );
        }
    }

    public Task StartBrokerAsync(CdcDeploymentRequest request, CancellationToken cancellationToken) =>
        WaitAsync(
            () => Task.FromResult(_admin.GetMetadata(TimeSpan.FromSeconds(3)).Brokers.Count == _ports.Length),
            cancellationToken
        );

    public async Task StartWorkerAsync(CdcDeploymentRequest request, CancellationToken cancellationToken)
    {
        // Independent live check inside the actual infrastructure effect records the gate-to-launch ordering.
        var offsets = Value(await Controller.ObserveOffsetStoreAsync(request, cancellationToken));
        offsets.PolicyState.Should().Be(CoreCdc.CdcConnectOffsetStorePolicyState.Satisfied);
        OffsetVerifiedAt = offsets.ObservedAt;
        foreach (string topic in new[] { ConfigTopic, StatusTopic })
        {
            await _admin.CreateTopicsAsync([
                new()
                {
                    Name = topic,
                    NumPartitions = 1,
                    ReplicationFactor = (short)_ports.Length,
                    Configs = new()
                    {
                        ["cleanup.policy"] = "compact",
                        ["min.insync.replicas"] = secured ? "2" : "1",
                    },
                },
            ]);
            if (secured)
            {
                foreach (
                    var operation in new[]
                    {
                        CdcKafkaAclOperation.Read,
                        CdcKafkaAclOperation.Write,
                        CdcKafkaAclOperation.Describe,
                    }
                )
                {
                    await AddGrantAsync(new("User:worker", CdcKafkaAclResourceType.Topic, topic, operation));
                }
            }
        }
        if (secured)
        {
            await AddGrantAsync(
                new("User:worker", CdcKafkaAclResourceType.Group, WorkerGroup, CdcKafkaAclOperation.Read)
            );
        }

        List<string> args =
        [
            "run",
            "--detach",
            "--name",
            Worker,
            "--network",
            _prefix,
            "-p",
            $"127.0.0.1:{_workerPort}:8083",
            "-p",
            $"127.0.0.1:{_metricsPort}:9404",
        ];
        Dictionary<string, string> env = new()
        {
            ["BOOTSTRAP_SERVERS"] = string.Join(
                ',',
                Enumerable.Range(0, _ports.Length).Select(i => Broker(i) + ":9092")
            ),
            ["GROUP_ID"] = WorkerGroup,
            ["CONFIG_STORAGE_TOPIC"] = ConfigTopic,
            ["OFFSET_STORAGE_TOPIC"] = OffsetTopic,
            ["STATUS_STORAGE_TOPIC"] = StatusTopic,
            ["CONFIG_STORAGE_REPLICATION_FACTOR"] = _ports.Length.ToString(CultureInfo.InvariantCulture),
            ["OFFSET_STORAGE_REPLICATION_FACTOR"] = _ports.Length.ToString(CultureInfo.InvariantCulture),
            ["STATUS_STORAGE_REPLICATION_FACTOR"] = _ports.Length.ToString(CultureInfo.InvariantCulture),
            ["CONNECT_REST_ADVERTISED_HOST_NAME"] = Worker,
            ["CONNECT_REST_ADVERTISED_PORT"] = "8083",
            ["CONNECT_CONNECTOR_CLIENT_CONFIG_OVERRIDE_POLICY"] = "All",
            ["KAFKA_HEAP_OPTS"] = "-Xms512m -Xmx1g",
        };
        if (secured)
        {
            env["CONNECT_SECURITY_PROTOCOL"] = "SASL_PLAINTEXT";
            env["CONNECT_SASL_MECHANISM"] = "PLAIN";
            env["CONNECT_SASL_JAAS_CONFIG"] =
                $"org.apache.kafka.common.security.plain.PlainLoginModule required username=\"worker\" password=\"{_password}\";";
        }
        foreach (var (key, value) in env)
        {
            args.AddRange(["-e", key + "=" + value]);
        }

        args.Add(CdcQualifiedWorkerImage.Image);
        _containers.Add(Worker);
        await DockerAsync(args, cancellationToken);
        WorkerLaunches++;
        string started = await DockerAsync(
            ["inspect", Worker, "--format", "{{.State.StartedAt}}"],
            cancellationToken
        );
        WorkerStartedAt = DateTimeOffset.Parse(started, CultureInfo.InvariantCulture);
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(3) };
        await WaitAsync(
            async () => (await http.GetAsync(request.ConnectEndpoint, cancellationToken)).IsSuccessStatusCode,
            cancellationToken
        );
        await WaitAsync(
            async () =>
                (await http.GetAsync(request.WorkerMetricsEndpoint, cancellationToken)).IsSuccessStatusCode,
            cancellationToken
        );
        await WaitAsync(
            async () =>
                (await DockerAsync(["logs", Worker], cancellationToken)).Contains(
                    "Finished starting connectors and tasks",
                    StringComparison.Ordinal
                ),
            cancellationToken
        );
    }

    internal async Task SetConfigAsync(string topic, string key, string value, bool remove = false)
    {
        await _admin.IncrementalAlterConfigsAsync(
            new Dictionary<ConfigResource, List<ConfigEntry>>
            {
                [new() { Type = ResourceType.Topic, Name = topic }] =
                [
                    new()
                    {
                        Name = key,
                        Value = value,
                        IncrementalOperation = remove ? AlterConfigOpType.Delete : AlterConfigOpType.Set,
                    },
                ],
            }
        );

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(1));
        int consistent = 0;
        await WaitAsync(
            async () =>
            {
                var result = await Adapter.InspectTopicAsync(Request(), topic, timeout.Token);
                bool matches =
                    result is CdcTransportResult<CdcKafkaTopicEvidence>.Observed found
                    && found.Value.Configuration.TryGetValue(key, out var entry)
                    && entry.IsTopicOverride == !remove
                    && (remove || entry.Value == value);
                consistent = matches ? consistent + 1 : 0;
                return consistent >= 3;
            },
            timeout.Token
        );
    }

    internal async Task AddPartitionAsync(string topic)
    {
        await _admin.CreatePartitionsAsync([new() { Topic = topic, IncreaseTo = 2 }]);
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(1));
        await WaitForMetadataAsync(
            topics => topics.Exists(t => t.Topic == topic && t.Partitions.Count == 2),
            timeout.Token
        );
    }

    internal async Task DeleteTopicAsync(string topic)
    {
        try
        {
            await _admin.DeleteTopicsAsync([topic]);
        }
        catch (DeleteTopicsException exception)
            when (exception.Results.TrueForAll(r => r.Error.Code == ErrorCode.UnknownTopicOrPart))
        {
            // Idempotent fixture cleanup still requires authoritative absence on every broker.
        }
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(1));
        await WaitForMetadataAsync(topics => !topics.Exists(t => t.Topic == topic), timeout.Token);
    }

    private async Task WaitForMetadataAsync(Func<List<TopicMetadata>, bool> expected, CancellationToken token)
    {
        // Acknowledgement and one broker's metadata do not establish propagation to its peers.
        // Separate seed connections verify the origin of every response; no random-broker sampling.
        for (int i = 0; i < _ports.Length; i++)
        {
            using var client = new AdminClientBuilder(
                new AdminClientConfig(Client("admin"))
                {
                    BootstrapServers = "127.0.0.1:" + _ports[i].ToString(CultureInfo.InvariantCulture),
                }
            )
                .SetLogHandler((_, _) => { })
                .SetErrorHandler((_, _) => { })
                .Build();
            int brokerId = i + 1;
            await WaitAsync(
                () =>
                {
                    var metadata = client.GetMetadata(TimeSpan.FromSeconds(3));
                    return Task.FromResult(
                        metadata.OriginatingBrokerId == brokerId && expected(metadata.Topics)
                    );
                },
                token
            );
        }
    }

    internal async Task CreateWeakTopicAsync(CdcKafkaTopicIntent intent)
    {
        await _admin.CreateTopicsAsync([
            new()
            {
                Name = intent.Name,
                NumPartitions = intent.PartitionCount,
                ReplicationFactor = 1,
                Configs = intent.Configuration.ToDictionary(
                    x => x.Key,
                    x => x.Value == "2" && x.Key == "min.insync.replicas" ? "1" : x.Value
                ),
            },
        ]);

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(1));
        await WaitForMetadataAsync(
            topics =>
                topics.Exists(t =>
                    t.Topic == intent.Name
                    && t.Partitions.Count == intent.PartitionCount
                    && t.Partitions.TrueForAll(p => p.Replicas.Length == 1)
                ),
            timeout.Token
        );
    }

    internal Task AddGrantAsync(CdcKafkaAclGrant grant) => ChangeGrantAsync(grant, true);

    internal Task RemoveGrantAsync(CdcKafkaAclGrant grant) => ChangeGrantAsync(grant, false);

    private async Task ChangeGrantAsync(CdcKafkaAclGrant grant, bool add)
    {
        var binding = Binding(grant);
        AclBindingFilter filter = new()
        {
            PatternFilter = new()
            {
                Type = binding.Pattern.Type,
                Name = binding.Pattern.Name,
                ResourcePatternType = binding.Pattern.ResourcePatternType,
            },
            EntryFilter = new()
            {
                Principal = binding.Entry.Principal,
                Host = binding.Entry.Host,
                Operation = binding.Entry.Operation,
                PermissionType = binding.Entry.PermissionType,
            },
        };
        if (add)
        {
            await _admin.CreateAclsAsync([binding]);
        }
        else
        {
            await _admin.DeleteAclsAsync([filter]);
        }
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(1));
        int consistent = 0;
        await WaitAsync(
            async () =>
            {
                var result = await _admin.DescribeAclsAsync(
                    filter,
                    new() { RequestTimeout = TimeSpan.FromSeconds(10) }
                );
                consistent = result.AclBindings.Any() == add ? consistent + 1 : 0;
                return consistent >= 3;
            },
            timeout.Token
        );
    }

    private static AclBinding Binding(CdcKafkaAclGrant grant) =>
        new()
        {
            Pattern = new()
            {
                Type = grant.ResourceType switch
                {
                    CdcKafkaAclResourceType.Cluster => ResourceType.Broker,
                    CdcKafkaAclResourceType.TransactionalId => (ResourceType)5,
                    _ => Enum.Parse<ResourceType>(grant.ResourceType.ToString()),
                },
                Name = grant.ResourceName,
                ResourcePatternType = Enum.Parse<ResourcePatternType>(grant.Pattern.ToString()),
            },
            Entry = new()
            {
                Principal = grant.Principal,
                Host = grant.Host,
                Operation = Enum.Parse<AclOperation>(grant.Operation.ToString()),
                PermissionType = Enum.Parse<AclPermissionType>(grant.Permission.ToString()),
            },
        };

    internal async Task<DeliveryResult<string, string>> ProduceAsync(
        string user,
        string topic,
        CancellationToken token,
        bool tombstone = false
    )
    {
        using var producer = new ProducerBuilder<string, string>(
            new ProducerConfig(Client(user))
            {
                Acks = Acks.All,
                EnableIdempotence = false,
                MessageTimeoutMs = 10000,
            }
        ).SetLogHandler((_, _) => { }).SetErrorHandler((_, _) => { }).Build();
        return await producer.ProduceAsync(
            topic,
            new() { Key = "qualification-key", Value = tombstone ? null! : "qualification-value" },
            token
        );
    }

    internal async Task<bool> ReadTombstoneAsync(TopicPartitionOffset position, CancellationToken token)
    {
        using var consumer = new ConsumerBuilder<string, string>(
            new ConsumerConfig(Client("consumer-a"))
            {
                GroupId = Request().WorkerPolicy.Consumers.Single().Group.Value,
                EnableAutoCommit = false,
            }
        )
            .SetLogHandler((_, _) => { })
            .SetErrorHandler((_, _) => { })
            .Build();
        consumer.Assign(position);
        return await Task.Run(
            () =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var result = consumer.Consume(timeout.Token);
                return result.TopicPartitionOffset == position
                    && result.Message.Key == "qualification-key"
                    && result.Message.Value is null;
            },
            token
        );
    }

    internal Task WaitForReplicasAsync(string topic, CancellationToken token) =>
        WaitAsync(
            () =>
            {
                var partitions = _admin
                    .GetMetadata(topic, TimeSpan.FromSeconds(3))
                    .Topics.Single()
                    .Partitions;
                return Task.FromResult(
                    partitions.Count > 0
                        && partitions.TrueForAll(p =>
                            p.Error.Code == ErrorCode.NoError
                            && p.Replicas.Length == _ports.Length
                            && p.InSyncReplicas.Length >= (secured ? 2 : 1)
                        )
                );
            },
            token
        );

    internal async Task<ErrorCode> ReadAsync(string user, string topic, string group, CancellationToken token)
    {
        using var consumer = new ConsumerBuilder<string, string>(
            new ConsumerConfig(Client(user))
            {
                GroupId = group,
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
                FetchWaitMaxMs = 100,
                SessionTimeoutMs = 6000,
            }
        ).SetLogHandler((_, _) => { }).SetErrorHandler((_, _) => { }).Build();
        consumer.Subscribe(topic);
        return await Task.Run(
            () =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    var result = consumer.Consume(timeout.Token);
                    result.Message.Key.Should().Be("qualification-key");
                    return ErrorCode.NoError;
                }
                catch (ConsumeException exception)
                {
                    return exception.Error.Code;
                }
            },
            token
        );
    }

    internal async Task ChangeBrokerCapacityAsync(int bytes, CancellationToken token)
    {
        await DockerAsync(["rm", "-f", "-v", Broker(0)], token);
        await StartNodeAsync(0, bytes, token);
        await WaitAsync(
            async () =>
            {
                var evidence = await Adapter.InspectBrokersAsync(Request(), token);
                return evidence is CdcTransportResult<CdcKafkaBrokerEvidence>.Observed found
                    && found.Value.Brokers.Count == _ports.Length
                    && found.Value.Brokers.Single(b => b.BrokerId == 1).ReplicaFetchMaxBytes == bytes;
            },
            token
        );
    }

    internal async Task<string> SnapshotAsync(CdcDeploymentRequest request, CancellationToken token)
    {
        var evidence = await EvidenceAsync(request, token);
        return JsonSerializer.Serialize(
            new
            {
                topics = evidence
                    .Topics.OrderBy(x => x.Key)
                    .Select(x => new
                    {
                        name = x.Key,
                        value = Value(x.Value)
                            .Configuration.OrderBy(c => c.Key)
                            .Select(c => new
                            {
                                c.Key,
                                c.Value.Value,
                                c.Value.IsTopicOverride,
                            }),
                        replicas = Value(x.Value).PartitionReplicas,
                    }),
                grants = Value(evidence.Acls)
                    .Grants.OrderBy(g => g.Principal)
                    .ThenBy(g => g.ResourceName)
                    .ThenBy(g => g.Operation)
                    .ThenBy(g => g.ResourceType)
                    .ThenBy(g => g.Pattern)
                    .ThenBy(g => g.Permission)
                    .ThenBy(g => g.Host)
                    .Select(g => new
                    {
                        g.Principal,
                        g.ResourceType,
                        g.ResourceName,
                        g.Operation,
                        g.Pattern,
                        g.Permission,
                        g.Host,
                    }),
            }
        );
    }

    internal static T Value<T>(CdcTransportResult<T> result)
        where T : notnull
    {
        result
            .Should()
            .BeOfType<CdcTransportResult<T>.Observed>(
                "live evidence must be available; diagnostics: {0}",
                JsonSerializer.Serialize(result.Diagnostics)
            );
        return ((CdcTransportResult<T>.Observed)result).Value;
    }

    internal static async Task WaitAsync(Func<Task<bool>> predicate, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                if (await predicate().WaitAsync(timeout.Token))
                {
                    return;
                }
            }
            catch (Exception exception)
                when (exception is KafkaException or HttpRequestException or TaskCanceledException)
            {
                // Startup polling retries transient transport failures within one finite deadline.
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
        }
    }

    private async Task<string> DockerAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        var result = await _docker.RunAllowingFailureAsync(args, token);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Kafka qualification Docker operation failed (" + args[0] + ")."
            );
        }

        return result.StandardOutput.Trim();
    }

    private static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        Adapter?.Dispose();
        _admin?.Dispose();
        bool failed = false;
        foreach (
            var args in _containers
                .AsEnumerable()
                .Reverse()
                .Select(name => new[] { "rm", "-f", "-v", name })
                .Concat(_volumes.Select(name => new[] { "volume", "rm", "-f", name }))
                .Concat(_networkCreated ? [new[] { "network", "rm", _prefix }] : [])
        )
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            try
            {
                await DockerAsync(args, timeout.Token);
            }
            catch
            {
                failed = true;
            }
        }
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }

        if (failed)
        {
            throw new InvalidOperationException("Kafka qualification resource cleanup failed.");
        }
    }
}
