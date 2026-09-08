// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
public sealed class Given_CdcWorkerStartup
{
    private readonly List<string> _effects = [];
    private CdcDeploymentRequest _request = null!;
    private ICdcKafkaAdministrationTransport _kafka = null!;
    private ICdcWorkerStartupTransport _infrastructure = null!;
    private CdcWorkerStartup _startup = null!;
    private Func<CdcConnectOffsetStorePolicyObservation, CdcConnectOffsetStorePolicyObservation> _change =
        value => value;

    [SetUp]
    public void Setup()
    {
        _effects.Clear();
        _change = value => value;
        _request = CdcDeploymentRequestTestData.Request();
        _kafka = A.Fake<ICdcKafkaAdministrationTransport>();
        _infrastructure = A.Fake<ICdcWorkerStartupTransport>();
        A.CallTo(() => _infrastructure.StartBrokerAsync(_request, A<CancellationToken>._))
            .Invokes(() => _effects.Add("broker"));
        A.CallTo(() => _infrastructure.StartWorkerAsync(_request, A<CancellationToken>._))
            .Invokes(() => _effects.Add("worker"));
        A.CallTo(() => _kafka.ProvisionOffsetStoreAsync(_request, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _effects.Add("offset");
                return Task.FromResult<CdcTransportResult<CdcConnectOffsetStorePolicyObservation>>(
                    new CdcTransportResult<CdcConnectOffsetStorePolicyObservation>.Observed(
                        _change(
                            new(
                                CdcJsonContract.CurrentContractVersion,
                                "startup",
                                DateTimeOffset.UtcNow,
                                _request.TargetIdentity,
                                _request.Binding.Provider,
                                _request.Binding.PhysicalSourceFingerprint,
                                "worker",
                                "connect-offsets",
                                CdcConnectOffsetStorePolicyState.Satisfied,
                                "compact",
                                1,
                                1,
                                CdcConnectOffsetStoreItemState.Satisfied,
                                []
                            )
                            {
                                TopicState = CdcConnectOffsetStoreItemState.Satisfied,
                            }
                        )
                    )
                );
            });
        _startup = new(_kafka, _infrastructure);
    }

    [Test]
    public async Task It_prepares_and_validates_offsets_after_broker_and_before_every_worker_start()
    {
        (await _startup.StartAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Observed);
        (await _startup.StartAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Observed);
        _effects.Should().Equal("broker", "offset", "worker", "broker", "offset", "worker");
    }

    [TestCase("stale")]
    [TestCase("future")]
    [TestCase("worker")]
    [TestCase("topic")]
    [TestCase("policy")]
    [TestCase("contract")]
    [TestCase("cleanup")]
    [TestCase("acl")]
    public async Task It_never_starts_from_unusable_offset_evidence(string fault)
    {
        _change = value =>
            fault switch
            {
                "stale" => value with { ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
                "future" => value with { ObservedAt = DateTimeOffset.UtcNow.AddMinutes(1) },
                "worker" => value with { WorkerKey = "other" },
                "topic" => value with { OffsetStorageTopic = "other" },
                "policy" => value with { PolicyState = CdcConnectOffsetStorePolicyState.Unknown },
                "contract" => value with { ContractVersion = -1 },
                "cleanup" => value with { CleanupPolicy = "delete" },
                _ => value with { AclState = CdcConnectOffsetStoreItemState.Unknown },
            };
        (await _startup.StartAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        _effects.Should().Equal("broker", "offset");
    }

    [Test]
    public async Task It_stops_after_failed_broker_start_without_preparing_offsets()
    {
        A.CallTo(() => _infrastructure.StartBrokerAsync(_request, A<CancellationToken>._))
            .ThrowsAsync(new IOException("sentinel-secret"));
        var result = await _startup.StartAsync(_request, CancellationToken.None);
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        result.Diagnostics.Single().Message.Should().NotContain("sentinel-secret");
        _effects.Should().NotContain("offset").And.NotContain("worker");
    }

    [Test]
    public async Task It_never_starts_when_offset_preparation_is_unavailable()
    {
        A.CallTo(() => _kafka.ProvisionOffsetStoreAsync(_request, A<CancellationToken>._))
            .Returns(
                new CdcTransportResult<CdcConnectOffsetStorePolicyObservation>.Unavailable(
                    new(CdcDeploymentComponent.Kafka, CdcDeploymentFailure.Unavailable)
                )
            );
        (await _startup.StartAsync(_request, CancellationToken.None))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        _effects.Should().Equal("broker");
    }

    [Test]
    public async Task It_preserves_caller_cancellation_without_starting_the_worker()
    {
        using var cancellation = new CancellationTokenSource();
        _change = _ => throw new OperationCanceledException(cancellation.Token);
        await cancellation.CancelAsync();
        Func<Task> act = () => _startup.StartAsync(_request, cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        _effects.Should().NotContain("worker");
    }
}

[TestFixture]
public sealed class Given_CdcWorkerComposeStartup
{
    private ICdcWorkerDockerCommand _docker = null!;
    private CdcDeploymentRequest _request = null!;
    private CdcComposeWorkerStartupTransport _transport = null!;
    private readonly List<string> _commands = [];
    private string _configuration = "";
    private const string Image =
        "example/qualified@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [SetUp]
    public void Setup()
    {
        _commands.Clear();
        _request = CdcDeploymentRequestTestData.Request();
        _configuration = System.Text.Json.JsonSerializer.Serialize(
            new
            {
                services = new Dictionary<string, object>
                {
                    ["kafka"] = new { image = "broker" },
                    ["kafka-cdc-worker"] = new
                    {
                        image = Image,
                        profiles = new[] { "cdc-managed-worker" },
                        environment = new
                        {
                            OFFSET_STORAGE_TOPIC = "connect-offsets",
                            GROUP_ID = "worker",
                            CONNECT_CONNECTOR_CLIENT_CONFIG_OVERRIDE_POLICY = "All",
                        },
                    },
                },
            }
        );
        _docker = new Docker(args =>
        {
            _commands.Add(string.Join(" ", args));
            return args.Contains("config") ? _configuration : "";
        });
        _transport = new(
            "/compose/kafka-cdc.yml",
            "/configuration/selected.env",
            "selected-project",
            new HashSet<string> { Image },
            _docker
        );
    }

    [Test]
    public async Task It_checks_the_exact_qualified_configuration_and_starts_only_the_explicit_service()
    {
        await _transport.StartBrokerAsync(_request, CancellationToken.None);
        await _transport.StartWorkerAsync(_request, CancellationToken.None);
        _commands.Should().HaveCount(4);
        _commands[0].Should().EndWith("--profile cdc-managed-worker config --format json");
        _commands[1].Should().EndWith("up --detach --wait --wait-timeout 180 kafka");
        _commands[2].Should().Be(_commands[0]);
        _commands[3].Should().EndWith("up --detach --no-deps --wait --wait-timeout 180 kafka-cdc-worker");
        _commands
            .Should()
            .OnlyContain(command =>
                command.Contains("--env-file /configuration/selected.env -p selected-project")
            );
    }

    [TestCase("image")]
    [TestCase("offset")]
    [TestCase("worker")]
    [TestCase("profile")]
    [TestCase("override")]
    public async Task It_rejects_unqualified_or_mismatched_selection_before_any_start(string fault)
    {
        _configuration = fault switch
        {
            "image" => _configuration.Replace(Image, "example/qualified:latest"),
            "offset" => _configuration.Replace("connect-offsets", "other-offsets"),
            "worker" => _configuration.Replace("\"worker\"", "\"other\""),
            "profile" => _configuration.Replace("cdc-managed-worker", "default"),
            _ => _configuration.Replace("All", "None"),
        };
        Func<Task> act = () => _transport.StartWorkerAsync(_request, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidDataException>();
        _commands.Should().ContainSingle().Which.Should().NotContain(" up ");
    }

    [Test]
    public async Task It_CdcRecordSizeIncrease_includes_override_created_after_startup_transport_construction()
    {
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            _transport = new(
                "/compose/kafka-cdc.yml",
                "/configuration/selected.env",
                "selected-project",
                new HashSet<string> { Image },
                _docker,
                file
            );
            await _transport.StartBrokerAsync(_request, CancellationToken.None);
            await File.WriteAllTextAsync(file, "{}");
            _commands.Clear();
            await _transport.StartBrokerAsync(_request, CancellationToken.None);
            await _transport.StartWorkerAsync(_request, CancellationToken.None);
            _commands.Should().OnlyContain(command => command.Contains("-f " + file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    private sealed class Docker(Func<IReadOnlyList<string>, string> run) : ICdcWorkerDockerCommand
    {
        public Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(run(arguments));
        }
    }
}
