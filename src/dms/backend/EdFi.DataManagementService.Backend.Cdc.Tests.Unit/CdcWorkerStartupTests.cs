// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture(false)]
[TestFixture(true)]
internal sealed class Given_CdcWorkerStartup(bool retained)
    : CdcRegistrationTestBase(EdFi.DataManagementService.Backend.Ddl.CdcProvider.Postgresql)
{
    private readonly List<string> _effects = [];
    private ICdcWorkerStartupTransport _infrastructure = null!;
    private CdcWorkerStartup _startup = null!;

    [SetUp]
    public async Task SetupStartup()
    {
        _effects.Clear();
        if (retained)
        {
            await using var session = await _store.AcquireAsync(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1),
                default
            );
            var journal = await session.ReadAsync(Target, default);
            var intent = await session.RecordIntentAsync(
                Target,
                journal.WorkflowId,
                Guid.NewGuid(),
                CdcWorkflowEffect.StopConnector,
                [],
                default
            );
            await session.ReconcileCompletionAsync(
                Target,
                journal.WorkflowId,
                intent.Operations[^1].OperationId,
                (_, _) =>
                    Task.FromResult<CdcTransportResult<CdcWorkflowCompletion>>(
                        new CdcTransportResult<CdcWorkflowCompletion>.Observed(
                            new CdcWorkflowCompletion.Shutdown()
                        )
                    ),
                default
            );
        }
        _infrastructure = A.Fake<ICdcWorkerStartupTransport>();
        A.CallTo(() => _infrastructure.StartBrokerAsync(_request, A<CancellationToken>._))
            .Invokes(() => _effects.Add("broker"));
        A.CallTo(() => _infrastructure.StartWorkerAsync(_request, A<CancellationToken>._))
            .Invokes(() => _effects.Add("worker"));
        _onCall = name =>
        {
            if (name.StartsWith("topic:", StringComparison.Ordinal))
            {
                _effects.Add("offset");
            }
        };
        _startup = new(
            new CdcKafkaProvisioning(
                _root,
                _kafka,
                _runtime,
                new CdcKafkaProducerInspection(_connect, _worker)
            ),
            _infrastructure
        );
    }

    [Test]
    public async Task It_prepares_and_validates_offsets_after_broker_and_before_every_worker_start()
    {
        (await StartAsync(default)).State.Should().Be(CdcTransportEvidenceState.Observed);
        (await StartAsync(default)).State.Should().Be(CdcTransportEvidenceState.Observed);
        _effects.Where(e => e != "offset").Should().Equal("broker", "worker", "broker", "worker");
        if (retained)
        {
            A.CallTo(() =>
                    _kafka.CreateMissingTopicAsync(_request, A<CdcKafkaTopicIntent>._, A<CancellationToken>._)
                )
                .MustNotHaveHappened();
            Directory.Exists(Path.Combine(_root, "kafka")).Should().BeFalse();
        }
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
        var kafka = new CdcKafkaProvisioning(
            _root,
            _kafka,
            _runtime,
            new CdcKafkaProducerInspection(_connect, _worker)
        );
        var started = DateTimeOffset.UtcNow;
        var value = (
            (CdcTransportResult<CdcConnectOffsetStorePolicyObservation>.Observed)
                await kafka.ObserveOffsetStoreAsync(_request, default)
        ).Value;
        CdcWorkerStartup.ValidOffsetStore(_request, value, started, DateTimeOffset.UtcNow).Should().BeTrue();
        value = fault switch
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
        CdcWorkerStartup.ValidOffsetStore(_request, value, started, DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [TestCase("broker")]
    [TestCase("worker")]
    public async Task It_releases_the_startup_session_after_infrastructure_failure(string boundary)
    {
        if (boundary == "broker")
        {
            A.CallTo(() => _infrastructure.StartBrokerAsync(_request, A<CancellationToken>._))
                .ThrowsAsync(new IOException("sentinel-secret"));
        }
        else
        {
            A.CallTo(() => _infrastructure.StartWorkerAsync(_request, A<CancellationToken>._))
                .ThrowsAsync(new IOException("sentinel-secret"));
        }
        var result = await StartAsync(default);
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        result.Diagnostics.Single().Message.Should().NotContain("sentinel-secret");
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1),
            default
        );
    }

    [Test]
    public async Task It_never_starts_when_offset_inspection_is_unavailable()
    {
        A.CallTo(() => _kafka.InspectTopicAsync(_request, A<string>._, A<CancellationToken>._))
            .Returns(
                new CdcTransportResult<CdcKafkaTopicEvidence>.Unavailable(
                    new(CdcDeploymentComponent.Kafka, CdcDeploymentFailure.Unavailable)
                )
            );
        (await StartAsync(default)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _effects.Should().Equal("broker");
    }

    [Test]
    public async Task It_preserves_caller_cancellation_without_starting_the_worker()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        Func<Task> act = () => StartAsync(cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        _effects.Should().BeEmpty();
    }

    [TestCase(CdcWorkflowEffect.ResumeConnector)]
    [TestCase(CdcWorkflowEffect.Retire)]
    public async Task It_rechecks_changed_eligibility_after_acquiring_the_startup_session(
        CdcWorkflowEffect effect
    )
    {
        Task<CdcTransportResult<CdcTransportAcknowledgement>> startup;
        await using (
            var session = await _store.AcquireAsync(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1),
                default
            )
        )
        {
            startup = StartAsync(default);
            startup.IsCompleted.Should().BeFalse();
            _effects.Should().BeEmpty();
            var journal = await session.ReadAsync(Target, default);
            await session.RecordIntentAsync(Target, journal.WorkflowId, Guid.NewGuid(), effect, [], default);
        }
        (await startup).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _effects.Should().BeEmpty();
    }

    private Task<CdcTransportResult<CdcTransportAcknowledgement>> StartAsync(CancellationToken token) =>
        retained ? _startup.StartRetainedAsync(_request, token) : _startup.StartAsync(_request, token);
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
                            BOOTSTRAP_SERVERS = _request.ConnectorPolicy.KafkaBootstrapServers,
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
    [TestCase("bootstrap")]
    [TestCase("worker")]
    [TestCase("profile")]
    [TestCase("override")]
    public async Task It_rejects_unqualified_or_mismatched_selection_before_any_start(string fault)
    {
        _configuration = fault switch
        {
            "image" => _configuration.Replace(Image, "example/qualified:latest"),
            "offset" => _configuration.Replace("connect-offsets", "other-offsets"),
            "bootstrap" => _configuration.Replace(
                _request.ConnectorPolicy.KafkaBootstrapServers,
                "other:9092"
            ),
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
