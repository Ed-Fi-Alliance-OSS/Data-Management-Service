// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
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
    private bool _replace;
    private int _reads;
    private const string Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [SetUp]
    public void Setup()
    {
        _docker = new Docker();
        _adapter = new("selected", "localhost:9092", _docker);
        _image = CdcComposeBrokerSizeDeployment.BrokerImage;
        _project = "selected";
        _running = true;
        _replace = false;
        _reads = 0;
        _properties =
            "node.id=1\nadvertised.listeners=PLAINTEXT://kafka:19092,EXTERNAL://localhost:9092\nlistener.security.protocol.map=PLAINTEXT:PLAINTEXT,EXTERNAL:PLAINTEXT,CONTROLLER:PLAINTEXT\n";
        _docker.Run = (IReadOnlyList<string> args, CancellationToken _) =>
        {
            if (args[0] == "ps")
            {
                return Task.FromResult(Id);
            }
            if (args[0] == "exec")
            {
                return Task.FromResult(_properties);
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
            return Task.FromResult(
                JsonSerializer.Serialize(
                    new[]
                    {
                        new
                        {
                            Id,
                            Image = "image",
                            State = new
                            {
                                Running = _running,
                                StartedAt = _replace && _reads > 1 ? "later" : "original",
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
                        },
                    }
                )
            );
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

    [Test]
    public async Task It_rejects_a_broker_replacement_during_inspection()
    {
        _replace = true;
        (await _adapter.InspectAsync([1], default)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
    }

    [Test]
    public async Task It_rejects_a_different_admin_endpoint()
    {
        _properties = _properties.Replace("localhost:9092", "other:9092", StringComparison.Ordinal);
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
