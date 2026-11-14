// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(CdcProvider.Postgresql, false)]
[TestFixture(CdcProvider.SqlServer, true)]
[Parallelizable]
public sealed class Given_PinnedImageFixtureKafkaTopics(CdcProvider provider, bool isolateSourceProducer)
{
    private const string Prefix = "dms-cdc-kafka-test";
    private readonly List<string[]> _commands = [];
    private CdcConnectorTemplateRequest _request = null!;

    [SetUp]
    public void Setup()
    {
        _commands.Clear();
        var original = Given_PinnedImageConnectorCommittedSourceOffsetSelection.BuildRequest(provider);
        _request = new(
            original.Binding with
            {
                PartitionCount = 3,
            },
            original.ProviderSetupEvidence,
            original.DeploymentPolicy,
            original.ProviderConnectionProperties,
            original.KafkaClientSecurityProperties
        );
        var docker = new RecordingDockerCli(_commands);
        var reached = new InvalidOperationException("topics created");
        // Stop before worker startup; exercise real broker and topic command construction with fake Docker.
        Assert
            .CatchAsync(async () =>
                await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                    CdcProvider.Postgresql,
                    new(
                        "connect:qualified",
                        CdcComposeBrokerSizeDeployment.BrokerImage,
                        "postgres:qualified",
                        false,
                        false
                    ),
                    docker,
                    Prefix,
                    CancellationToken.None,
                    applyPrerequisitePolicy: false,
                    beforeWorker: async (fixture, token) =>
                    {
                        await fixture.CreateMinimalTopicsAsync(_request, token);
                        throw reached;
                    },
                    isolateSourceProducer: isolateSourceProducer
                )
            )
            .Should()
            .BeSameAs(reached);
    }

    [Test]
    public void It_creates_compacted_public_and_progress_topics_with_their_required_partition_counts()
    {
        TopicCommand(_request.PublicTopicName)
            .Should()
            .Equal(
                ExpectedTopicCommand(
                    _request.PublicTopicName,
                    "3",
                    "cleanup.policy=compact",
                    "delete.retention.ms=604800000"
                )
            );
        TopicCommand(_request.ProgressTopicName)
            .Should()
            .Equal(
                ExpectedTopicCommand(
                    _request.ProgressTopicName,
                    "1",
                    "cleanup.policy=compact",
                    "delete.retention.ms=604800000"
                )
            );
    }

    [Test]
    public void It_creates_single_partition_unbounded_schema_history_only_for_sql_server()
    {
        string[][] topics = _commands.Where(command => command.Contains("--create")).ToArray();
        topics.Should().HaveCount(provider == CdcProvider.SqlServer ? 3 : 2);
        if (provider == CdcProvider.SqlServer)
        {
            TopicCommand(_request.SchemaHistoryTopicName!)
                .Should()
                .Equal(
                    ExpectedTopicCommand(
                        _request.SchemaHistoryTopicName!,
                        "1",
                        "cleanup.policy=delete",
                        "retention.ms=-1",
                        "retention.bytes=-1"
                    )
                );
        }
    }

    [Test]
    public void It_preserves_the_optional_isolated_source_producer_listener()
    {
        string[] broker = _commands.Single(command =>
            command[0] == "run" && command.Contains(Prefix + "-broker")
        );
        broker
            .Single(argument => argument.StartsWith("KAFKA_LISTENERS=", StringComparison.Ordinal))
            .Should()
            .Be(
                $"KAFKA_LISTENERS=INTERNAL://0.0.0.0:9092,EXTERNAL://0.0.0.0:29092,CONTROLLER://{Prefix}-broker:9093"
                    + (isolateSourceProducer ? ",PRODUCER://0.0.0.0:9094" : "")
            );
        broker
            .Single(argument =>
                argument.StartsWith("KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=", StringComparison.Ordinal)
            )
            .Should()
            .Be(
                "KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=CONTROLLER:PLAINTEXT,INTERNAL:PLAINTEXT,EXTERNAL:PLAINTEXT"
                    + (isolateSourceProducer ? ",PRODUCER:PLAINTEXT" : "")
            );
        string advertised = broker.Single(argument =>
            argument.StartsWith("KAFKA_ADVERTISED_LISTENERS=", StringComparison.Ordinal)
        );
        if (isolateSourceProducer)
        {
            advertised.Should().EndWith($",PRODUCER://{Prefix}-connect:19094");
        }
        else
        {
            advertised.Should().NotContain("PRODUCER");
        }
    }

    private sealed class RecordingDockerCli(List<string[]> commands) : IDockerCli
    {
        public bool IsOffline => false;

        public Task RequireDockerAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DockerCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken
        )
        {
            commands.Add(arguments.ToArray());
            return Task.FromResult(new DockerCommandResult(0, "", ""));
        }

        public Task<DockerCommandResult> RunAllowingFailureAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken
        ) => RunAsync(arguments, cancellationToken);
    }

    private string[] TopicCommand(string topic) => _commands.Single(command => command.Contains(topic));

    private static string[] ExpectedTopicCommand(string topic, string partitions, params string[] configs) =>
        [
            "exec",
            Prefix + "-broker",
            "/opt/kafka/bin/kafka-topics.sh",
            "--bootstrap-server",
            Prefix + "-broker:9092",
            "--create",
            "--if-not-exists",
            "--topic",
            topic,
            "--partitions",
            partitions,
            "--replication-factor",
            "1",
            .. configs.SelectMany(config => new[] { "--config", config }),
        ];
}

[TestFixture]
[NonParallelizable]
public sealed class Given_PinnedImageFixtureKafkaSettings
{
    [Test]
    public void It_defaults_to_the_shipped_kafka_image_without_a_redpanda_prerequisite()
    {
        const string variable = "CDC_CONNECTOR_TEMPLATE_KAFKA_IMAGE";
        string? original = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, null);
            CdcConnectorTemplateSmokeSettings
                .FromEnvironment(CdcProvider.Postgresql)
                .BrokerImage.Should()
                .Be(CdcComposeBrokerSizeDeployment.BrokerImage);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }
}
