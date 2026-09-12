// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
public sealed class Given_CdcWorkerDeployment
{
    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Id = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Image = "edfialliance/ed-fi-kafka-connect@" + Digest;
    private CdcDeploymentRequest _request = null!;
    private Docker _docker = null!;
    private CdcWorkerDeployment _inspection = null!;

    [SetUp]
    public void Setup()
    {
        _request = CdcDeploymentRequestTestData.Request(
            endpoint: "http://127.0.0.1:8083/",
            metricsEndpoint: "http://127.0.0.1:9404/metrics"
        );
        _docker = new();
        _inspection = new("dms-local", "kafka-cdc-worker", new HashSet<string> { Digest }, _docker);
    }

    [Test]
    public async Task It_reads_live_image_process_configuration_heap_and_owned_endpoint()
    {
        var result = (CdcTransportResult<CdcWorkerInspection>.Observed)
            await _inspection.InspectAsync(_request, CancellationToken.None);
        result.Value.ImageDigest.Should().Be(Digest);
        result.Value.HeapBytes.Should().Be(268_435_456);
        result.Value.ConnectWorkerId.Should().Be("worker:8083");
        result.Value.ProcessIdentity.Should().StartWith("sha256:").And.HaveLength(71);
        result.Value.EffectiveConfiguration["offset.storage.topic"].Should().Be("connect-offsets");
        result.Value.MetricsEndpoint.Should().Be(_request.WorkerMetricsEndpoint);
        JsonSerializer.Serialize(result.Value).Should().Be("{}");
        result.Value.ToString().Should().Be(nameof(CdcWorkerInspection));
        _docker.Inspections.Should().Be(2);
        _docker.RuntimeReads.Should().Be(2);
    }

    [Test]
    public async Task It_accepts_the_resolved_advertised_address_only_from_live_container_network_ownership()
    {
        _docker.Fault = "advertised-ip";
        var result = (CdcTransportResult<CdcWorkerInspection>.Observed)
            await _inspection.InspectAsync(_request, CancellationToken.None);
        result.Value.ConnectWorkerId.Should().Be("172.20.0.4:8083");
    }

    [Test]
    public async Task It_accepts_a_health_probe_log_update_when_the_worker_process_is_unchanged()
    {
        _docker.Fault = "health-probe";
        var result = await _inspection.InspectAsync(_request, CancellationToken.None);
        result.State.Should().Be(CdcTransportEvidenceState.Observed);
    }

    [TestCase("none")]
    [TestCase("multiple")]
    [TestCase("stopped")]
    [TestCase("paused")]
    [TestCase("restarting")]
    [TestCase("pid")]
    [TestCase("started")]
    [TestCase("floating")]
    [TestCase("image")]
    [TestCase("digest")]
    [TestCase("project")]
    [TestCase("service")]
    [TestCase("worker")]
    [TestCase("host-network")]
    [TestCase("metrics-port")]
    [TestCase("rest-port")]
    [TestCase("public-port")]
    [TestCase("runtime-identity")]
    [TestCase("heap")]
    [TestCase("bootstrap")]
    [TestCase("bootstrap-missing")]
    [TestCase("bootstrap-empty")]
    [TestCase("offset")]
    [TestCase("group")]
    [TestCase("advertised")]
    [TestCase("override")]
    [TestCase("recovery")]
    [TestCase("runtime-race")]
    [TestCase("topology-race")]
    [TestCase("malformed")]
    public async Task It_keeps_unsupported_missing_or_changed_evidence_unknown(string fault)
    {
        _docker.Fault = fault;
        var result = await _inspection.InspectAsync(_request, CancellationToken.None);
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        JsonSerializer.Serialize(result).Should().NotContain("sentinel-secret");
    }

    [Test]
    public async Task It_rejects_an_unqualified_request_using_the_shipped_image_inventory_before_docker_access()
    {
        var inspector = new CdcWorkerDeployment("dms-local", "kafka-cdc-worker");
        var result = await inspector.InspectAsync(_request, CancellationToken.None);
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        result.Diagnostics.Single().Failure.Should().Be(CdcDeploymentFailure.ValidationFailed);
    }

    [Test]
    public async Task It_rejects_a_requested_digest_without_independent_qualification()
    {
        _inspection = new("dms-local", "kafka-cdc-worker", new HashSet<string>(), _docker);
        (await _inspection.InspectAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        _docker.Inspections.Should().Be(0);
    }

    [TestCase("http://connect:9404/metrics")]
    [TestCase("http://127.0.0.1:9404/proxy")]
    [TestCase("https://127.0.0.1:9404/metrics")]
    public async Task It_does_not_infer_endpoint_ownership_from_an_address(string endpoint)
    {
        _request = CdcDeploymentRequestTestData.Request(
            endpoint: "http://127.0.0.1:8083/",
            metricsEndpoint: endpoint
        );
        (await _inspection.InspectAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
    }

    [Test]
    public async Task It_preserves_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        Func<Task> act = () => _inspection.InspectAsync(_request, cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestCase("offset.storage.topic=one\noffset.storage.topic=two")]
    [TestCase(" offset.storage.topic=one")]
    [TestCase("offset.storage.topic:one")]
    [TestCase("offset.storage.topic=one\\")]
    [TestCase("offset.storage.topic=")]
    public void It_rejects_ambiguous_or_incomplete_properties(string text)
    {
        Action act = () => CdcWorkerDeployment.ParseProperties(text);
        act.Should().Throw<InvalidDataException>();
    }

    private sealed class Docker : ICdcWorkerDockerCommand
    {
        public string Fault { get; set; } = "";
        public int Inspections { get; private set; }
        public int RuntimeReads { get; private set; }
        private int _lists;

        public Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (arguments[0] == "ps")
            {
                _lists++;
                return Task.FromResult(
                    Fault switch
                    {
                        "none" => "",
                        "multiple" => Id + "\n" + Id,
                        "topology-race" when _lists == 2 => "",
                        _ => Id,
                    }
                );
            }
            if (arguments[0] == "image")
            {
                return Task.FromResult(
                    JsonSerializer.Serialize(
                        new[]
                        {
                            new
                            {
                                Id = Fault == "image" ? "wrong" : "sha256:local-image",
                                RepoDigests = new[] { Fault == "digest" ? "wrong" : Image },
                            },
                        }
                    )
                );
            }
            if (arguments[0] == "exec")
            {
                RuntimeReads++;
                string text =
                    "1234\n268435456\noffset.storage.topic=connect-offsets\nconnector.client.config.override.policy=All\ngroup.id=worker\nconfig.storage.topic=config\nstatus.storage.topic=status\nbootstrap.servers=broker-1:9092,broker-2:9092\nrest.advertised.host.name=worker\nrest.advertised.port=8083\n";
                return Task.FromResult(
                    Fault switch
                    {
                        "runtime-identity" => text.Replace("1234", "0"),
                        "advertised-ip" => text.Replace(
                            "rest.advertised.host.name=worker",
                            "rest.advertised.host.name=172.20.0.4"
                        ),
                        "heap" => text.Replace("268435456", "1"),
                        "offset" => text.Replace("connect-offsets", "other"),
                        "bootstrap-missing" => text.Replace(
                            "bootstrap.servers=broker-1:9092,broker-2:9092\n",
                            ""
                        ),
                        "bootstrap-empty" => text.Replace(
                            "bootstrap.servers=broker-1:9092,broker-2:9092",
                            "bootstrap.servers="
                        ),
                        "bootstrap" => text.Replace(
                            "bootstrap.servers=broker-1:9092,broker-2:9092",
                            "bootstrap.servers=other:9092"
                        ),
                        "group" => text.Replace("group.id=worker", "group.id=other"),
                        "advertised" => text.Replace(
                            "rest.advertised.host.name=worker",
                            "rest.advertised.host.name=other"
                        ),
                        "override" => text.Replace("=All", "=None"),
                        "runtime-race" when RuntimeReads == 2 => text.Replace("1234", "1235"),
                        _ => text,
                    }
                );
            }
            Inspections++;
            var container = JsonNode.Parse(
                JsonSerializer.Serialize(
                    new
                    {
                        Id,
                        Name = "/worker",
                        Image = "sha256:local-image",
                        RestartCount = 0,
                        State = new
                        {
                            Running = true,
                            Restarting = false,
                            Paused = false,
                            Pid = 100,
                            StartedAt = "2026-09-08T10:00:00Z",
                        },
                        Config = new
                        {
                            Image,
                            Hostname = "worker",
                            Labels = new Dictionary<string, string>
                            {
                                ["com.docker.compose.project"] = "dms-local",
                                ["com.docker.compose.service"] = "kafka-cdc-worker",
                                ["org.edfi.cdc.worker"] = "worker",
                            },
                        },
                        HostConfig = new { NetworkMode = "dms" },
                        NetworkSettings = new
                        {
                            Networks = new Dictionary<string, object>
                            {
                                ["dms"] = new { IPAddress = "172.20.0.4", Aliases = new[] { "worker" } },
                            },
                            Ports = new Dictionary<string, object>
                            {
                                ["8083/tcp"] = new[] { new { HostIp = "127.0.0.1", HostPort = "8083" } },
                                ["9404/tcp"] = new[] { new { HostIp = "127.0.0.1", HostPort = "9404" } },
                            },
                        },
                    }
                )
            )!;
            switch (Fault)
            {
                case "stopped":
                    container["State"]!["Running"] = false;
                    break;
                case "paused":
                    container["State"]!["Paused"] = true;
                    break;
                case "restarting":
                    container["State"]!["Restarting"] = true;
                    break;
                case "pid":
                    container["State"]!["Pid"] = 0;
                    break;
                case "started":
                    container["State"]!["StartedAt"] = "unknown";
                    break;
                case "floating":
                    container["Config"]!["Image"] = "connect:latest";
                    break;
                case "project":
                    container["Config"]!["Labels"]!["com.docker.compose.project"] = "other";
                    break;
                case "service":
                    container["Config"]!["Labels"]!["com.docker.compose.service"] = "other";
                    break;
                case "worker":
                    container["Config"]!["Labels"]!["org.edfi.cdc.worker"] = "other";
                    break;
                case "host-network":
                    container["HostConfig"]!["NetworkMode"] = "host";
                    break;
                case "metrics-port":
                    container["NetworkSettings"]!["Ports"]!["9404/tcp"]![0]!["HostPort"] = "9999";
                    break;
                case "rest-port":
                    container["NetworkSettings"]!["Ports"]!["8083/tcp"]![0]!["HostPort"] = "9999";
                    break;
                case "public-port":
                    container["NetworkSettings"]!["Ports"]!["9404/tcp"]![0]!["HostIp"] = "0.0.0.0";
                    break;
                case "recovery" when Inspections == 2:
                    container["State"]!["StartedAt"] = "2026-09-08T11:00:00Z";
                    break;
                case "health-probe":
                    container["State"]!["Health"] = new JsonObject
                    {
                        ["Status"] = "healthy",
                        ["Log"] = new JsonArray(new JsonObject { ["End"] = $"probe-{Inspections}" }),
                    };
                    break;
                case "malformed":
                    return Task.FromResult("sentinel-secret");
            }
            return Task.FromResult(new JsonArray(container).ToJsonString());
        }
    }
}
