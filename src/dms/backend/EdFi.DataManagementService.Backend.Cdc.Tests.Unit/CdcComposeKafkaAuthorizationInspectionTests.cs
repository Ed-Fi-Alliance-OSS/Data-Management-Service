// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Confluent.Kafka;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
public class Given_Cdc_compose_authorization_inspection
{
    private Docker _docker = null!;
    private CdcComposeKafkaAuthorizationInspection _adapter = null!;
    private string _properties = null!;
    private string _image = null!;
    private string _project = null!;
    private bool _running;
    private Action<JsonObject, int> _changeInspection = null!;
    private string _finalPropertiesSuffix = null!;
    private int _propertyReads;
    private int _reads;
    private const string Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [SetUp]
    public void Setup()
    {
        _docker = new Docker();
        _adapter = new("selected", "127.0.0.1:9092", "dms-kafka1:9092", _docker);
        _image = CdcComposeBrokerSizeDeployment.BrokerImage;
        _project = "selected";
        _running = true;
        _changeInspection = (_, _) => { };
        _finalPropertiesSuffix = "";
        _propertyReads = 0;
        _reads = 0;
        _properties =
            "node.id=1\nadvertised.listeners=PLAINTEXT://dms-kafka1:9092,EXTERNAL://127.0.0.1:9092\nlistener.security.protocol.map=PLAINTEXT:PLAINTEXT,EXTERNAL:PLAINTEXT,CONTROLLER:PLAINTEXT\n";
        _docker.Run = (IReadOnlyList<string> args, CancellationToken _) =>
        {
            if (args[0] == "ps")
            {
                return Task.FromResult(Id);
            }
            if (args[0] == "exec")
            {
                return Task.FromResult(_properties + (++_propertyReads > 1 ? _finalPropertiesSuffix : ""));
            }
            if (args[0] == "image")
            {
                return Task.FromResult(
                    JsonSerializer.Serialize(
                        new[]
                        {
                            new
                            {
                                Id = "image",
                                RepoDigests = new[]
                                {
                                    _image.Replace(":3.9.0@", "@", StringComparison.Ordinal),
                                },
                            },
                        }
                    )
                );
            }
            _reads++;
            var container = JsonSerializer
                .SerializeToNode(
                    new
                    {
                        Id,
                        Image = "image",
                        State = new
                        {
                            Running = _running,
                            Restarting = false,
                            Paused = false,
                            Pid = 1234,
                            StartedAt = "2026-09-09T10:00:00Z",
                            Health = new
                            {
                                Status = "healthy",
                                Log = new[]
                                {
                                    new
                                    {
                                        Start = "2026-09-09T10:00:01Z",
                                        End = "2026-09-09T10:00:02Z",
                                        ExitCode = 0,
                                        Output = "healthy",
                                    },
                                },
                            },
                        },
                        RestartCount = 0,
                        Config = new
                        {
                            Image = _image,
                            Labels = new Dictionary<string, string>
                            {
                                ["com.docker.compose.project"] = _project,
                                ["com.docker.compose.service"] = "kafka",
                            },
                        },
                    }
                )!
                .AsObject();
            _changeInspection(container, _reads);
            return Task.FromResult(new JsonArray(container).ToJsonString());
        };
    }

    [Test]
    public async Task It_reports_observed_disabled_authorization_only_from_live_local_authority()
    {
        var result = await _adapter.InspectAsync([1], default);
        var evidence = result
            .Should()
            .BeOfType<CdcTransportResult<CdcKafkaAuthorizationDeploymentEvidence>.Observed>()
            .Subject.Value;
        evidence.InventoryComplete.Should().BeTrue();
        evidence.Brokers.Single().AuthorizationEnabled.Should().BeFalse();
        evidence.InheritedGrants.Should().BeEmpty();
    }

    [TestCase("authorizer.class.name=org.apache.kafka.metadata.authorizer.StandardAuthorizer\n")]
    [TestCase("super.users=User:admin\n")]
    [TestCase("node.id=2\n")]
    [TestCase("authorizer.class.name : hidden\n")]
    [TestCase("authorizer.class.name=hidden\\value\n")]
    public async Task It_rejects_unqualified_or_ambiguous_authority(string suffix)
    {
        _properties += suffix;
        (await _adapter.InspectAsync([1], default)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
    }

    [Test]
    public async Task It_rejects_different_broker_inventory() =>
        (await _adapter.InspectAsync([2], default)).State.Should().Be(CdcTransportEvidenceState.Unavailable);

    [Test]
    public async Task It_rejects_a_second_broker() =>
        (await _adapter.InspectAsync([1, 2], default))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);

    [Test]
    public async Task It_rejects_an_unqualified_image()
    {
        _image = "unqualified";
        (await _adapter.InspectAsync([1], default)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
    }

    [Test]
    public async Task It_rejects_a_different_deployment()
    {
        _project = "peer";
        (await _adapter.InspectAsync([1], default)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
    }

    [Test]
    public async Task It_rejects_a_stopped_broker()
    {
        _running = false;
        (await _adapter.InspectAsync([1], default)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
    }

    [TestCase("Running", "false")]
    [TestCase("Paused", "true")]
    [TestCase("Restarting", "true")]
    [TestCase("Pid", "5678")]
    [TestCase("StartedAt", "\"2026-09-09T10:01:00Z\"")]
    public async Task It_rejects_a_broker_process_change_during_inspection(string property, string value)
    {
        _changeInspection = (container, read) =>
        {
            if (read > 1)
            {
                container["State"]![property] = JsonNode.Parse(value);
            }
        };
        (await _adapter.InspectAsync([1], default)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
    }

    [TestCase("Id", "\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"")]
    [TestCase("RestartCount", "1")]
    public async Task It_rejects_a_replaced_or_restarted_container(string property, string value)
    {
        _changeInspection = (container, read) =>
        {
            if (read > 1)
            {
                container[property] = JsonNode.Parse(value);
            }
        };
        (await _adapter.InspectAsync([1], default)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
    }

    [TestCase("Paused")]
    [TestCase("Restarting")]
    public async Task It_rejects_a_broker_that_is_not_live_on_both_reads(string property)
    {
        _changeInspection = (container, _) => container["State"]![property] = true;
        (await _adapter.InspectAsync([1], default)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
    }

    [Test]
    public async Task It_accepts_routine_health_check_history_changes_between_reads()
    {
        _changeInspection = (container, read) =>
        {
            if (read > 1)
            {
                container["State"]!["Health"]!["Log"]!
                    .AsArray()
                    .Add(
                        JsonSerializer.SerializeToNode(
                            new
                            {
                                Start = "2026-09-09T10:00:31Z",
                                End = "2026-09-09T10:00:32Z",
                                ExitCode = 0,
                                Output = "healthy",
                            }
                        )
                    );
            }
        };
        (await _adapter.InspectAsync([1], default)).State.Should().Be(CdcTransportEvidenceState.Observed);
        _reads.Should().Be(2);
        _propertyReads.Should().Be(2);
    }

    [Test]
    public async Task It_rejects_live_broker_properties_that_change_between_reads()
    {
        _finalPropertiesSuffix =
            "authorizer.class.name=org.apache.kafka.metadata.authorizer.StandardAuthorizer\n";
        (await _adapter.InspectAsync([1], default)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _propertyReads.Should().Be(2);
    }

    [Test]
    public async Task It_rejects_a_different_admin_endpoint()
    {
        _properties = _properties.Replace("127.0.0.1:9092", "other:9092", StringComparison.Ordinal);
        (await _adapter.InspectAsync([1], default)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
    }

    [TestCase("private-other-broker:9092", "127.0.0.1:9092")]
    [TestCase("dms-kafka1:9092", "private-other-broker:9092")]
    [TestCase("private-other-broker:9092", "private-other-broker:9092")]
    [TestCase("dms-kafka1:9092,private-other-broker:9092", "127.0.0.1:9092")]
    public async Task It_rejects_requested_endpoints_outside_the_same_live_broker(
        string workerEndpoint,
        string adminEndpoint
    )
    {
        _adapter = new("selected", adminEndpoint, workerEndpoint, _docker);
        var result = await _adapter.InspectAsync([1], default);
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        result.Diagnostics.Should().ContainSingle().Which.Component.Should().Be(CdcDeploymentComponent.Kafka);
        JsonSerializer.Serialize(result).Should().NotContain("private-other-broker");
    }

    [TestCase("dms-kafka1:9092", CdcTransportEvidenceState.Observed)]
    [TestCase("private-other-broker:9092", CdcTransportEvidenceState.Unavailable)]
    public async Task It_correlates_matching_requested_and_effective_worker_endpoints_with_admin_evidence(
        string workerEndpoint,
        CdcTransportEvidenceState expected
    )
    {
        var original = CdcDeploymentRequestTestData.Request(
            worker: CdcDeploymentRequestTestData.Worker(digest: CdcQualifiedWorkerImage.Digests.Single())
        );
        CdcDeploymentRequest request = new(
            original.Binding,
            original.DmsSettings,
            original.ProviderSetup,
            original.ConnectEndpoint,
            original.WorkerMetricsEndpoint,
            new(workerEndpoint, original.ConnectorPolicy.MaxRecordBytes),
            original.WorkerPolicy,
            original.ProviderConnectionProperties,
            original.KafkaClientSecurityProperties,
            original.Timing
        );
        CdcWorkerInspection worker = new(
            "healthy-worker-process",
            request.WorkerMetricsEndpoint,
            new Dictionary<string, string>
            {
                ["bootstrap.servers"] = workerEndpoint,
                ["group.id"] = request.WorkerPolicy.WorkerKey.Value,
                ["offset.storage.topic"] = request.WorkerPolicy.OffsetStorageTopic.Value,
                ["connector.client.config.override.policy"] = "All",
            },
            request.WorkerPolicy.QualifiedImageDigest,
            request.WorkerPolicy.HeapBytes,
            "worker:8083"
        );
        // Matching worker input/evidence alone is insufficient; Kafka policy needs the broker correlation.
        CdcConnectorRegistration.RequireWorker(request, worker);
        var client = A.Fake<IAdminClient>(options => options.Strict());
        A.CallTo(() => client.Dispose()).DoesNothing();
        A.CallTo(() => client.GetMetadata(request.Timing.CallTimeout))
            .Returns(new Metadata([new BrokerMetadata(1, "127.0.0.1", 9092)], [], 1, "selected"));
        using var kafka = new CdcKafkaAdminAdapter(
            client,
            new CdcComposeKafkaAuthorizationInspection(
                "selected",
                "127.0.0.1:9092",
                request.ConnectorPolicy.KafkaBootstrapServers,
                _docker
            )
        );
        var result = await kafka.InspectAclsAsync(request, default);
        result.State.Should().Be(expected);
        if (expected == CdcTransportEvidenceState.Unavailable)
        {
            result
                .Diagnostics.Should()
                .ContainSingle()
                .Which.Component.Should()
                .Be(CdcDeploymentComponent.Kafka);
        }
        JsonSerializer.Serialize(result).Should().NotContain("private-other-broker");
    }

    [TestCase("dms-kafka1:9092", "other:9092")]
    [TestCase("PLAINTEXT://", "")]
    [TestCase("advertised.listeners", "unavailable.listeners")]
    public async Task It_requires_unambiguous_live_worker_listener_evidence(string before, string after)
    {
        _properties = _properties.Replace(before, after, StringComparison.Ordinal);
        (await _adapter.InspectAsync([1], default)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
    }

    [Test]
    public async Task It_preserves_cancellation()
    {
        _docker.Run = (_, _) => throw new OperationCanceledException();
        Func<Task> act = () => _adapter.InspectAsync([1], default);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task It_does_not_emit_docker_error_payloads()
    {
        _docker.Run = (_, _) => throw new InvalidOperationException("secret-sentinel");
        var result = await _adapter.InspectAsync([1], default);
        JsonSerializer.Serialize(result).Should().NotContain("secret-sentinel");
    }

    private sealed class Docker : ICdcWorkerDockerCommand
    {
        internal Func<IReadOnlyList<string>, CancellationToken, Task<string>> Run { get; set; } = null!;

        public Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken token) =>
            Run(arguments, token);
    }
}
