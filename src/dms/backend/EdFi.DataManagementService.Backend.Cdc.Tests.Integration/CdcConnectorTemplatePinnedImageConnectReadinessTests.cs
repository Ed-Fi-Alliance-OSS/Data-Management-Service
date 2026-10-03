// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture("healthy")]
[TestFixture("recovered")]
[TestFixture("exhausted")]
[TestFixture("unrelated")]
[TestFixture("cancel")]
[TestFixture("cancel-inspection")]
[TestFixture("cleanup-failure")]
[TestFixture("restart-failure")]
[TestFixture("keep-containers")]
[TestFixture("diagnostics-unavailable")]
[NonParallelizable]
public sealed class Given_PinnedImageFixtureConnectReadiness(string scenario)
{
    private const string Prefix = "dms-cdc-connect-readiness-test";
    private const string Pattern = "admission-evidence-connect-readiness-*.json";
    private RecordingDocker _docker = null!;
    private Exception _failure = null!;
    private string[] _evidence = [];
    private int _probes;
    private int _preparations;

    [SetUp]
    public async Task Setup()
    {
        using var cancellation = new CancellationTokenSource();
        _docker = new(scenario, cancellation);
        _probes = 0;
        _preparations = 0;
        _failure = null!;
        string directory = TestContext.CurrentContext.WorkDirectory;
        string[] before = Directory.GetFiles(directory, Pattern);
        try
        {
            await using var fixture = await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                CdcProvider.Postgresql,
                new(
                    "connect:qualified",
                    "redpanda:qualified",
                    "postgres:qualified",
                    true,
                    scenario == "keep-containers"
                ),
                _docker,
                Prefix,
                cancellation.Token,
                applyPrerequisitePolicy: false,
                beforeWorker: (_, _) =>
                {
                    _preparations++;
                    return Task.CompletedTask;
                },
                waitForConnect: (_, token) =>
                {
                    _probes++;
                    _docker.Events.Add("ready-" + _probes);
                    if (scenario == "unrelated")
                    {
                        throw _docker.Unrelated;
                    }
                    if (scenario == "cancel")
                    {
                        cancellation.Cancel();
                        token.ThrowIfCancellationRequested();
                    }
                    if (scenario != "healthy" && (_probes == 1 || scenario is "exhausted"))
                    {
                        throw new ConnectReadinessTimeoutException(45, 503, "HttpStatus", injected: true);
                    }
                    return Task.CompletedTask;
                }
            );
        }
        catch (Exception exception)
        {
            _failure = exception;
        }
        _evidence = Directory.GetFiles(directory, Pattern).Except(before).Select(File.ReadAllText).ToArray();
    }

    [Test]
    public void It_recreates_at_most_once_only_after_a_readiness_timeout()
    {
        int starts = scenario is "recovered" or "exhausted" or "restart-failure" or "diagnostics-unavailable"
            ? 2
            : 1;
        _docker.Starts.Should().Be(starts);
        _preparations.Should().Be(1, "database and broker preparation must never be replayed");
        _probes.Should().Be(scenario == "restart-failure" ? 1 : starts);
        switch (scenario)
        {
            case "healthy" or "recovered" or "diagnostics-unavailable":
                _failure.Should().BeNull();
                break;
            case "exhausted" or "keep-containers":
                _failure.Should().BeOfType<ConnectReadinessTimeoutException>();
                break;
            case "cancel" or "cancel-inspection":
                _failure.Should().BeAssignableTo<OperationCanceledException>();
                break;
            default:
                _failure.Should().BeSameAs(_docker.Unrelated);
                break;
        }
    }

    [Test]
    public void It_retains_failure_evidence_before_removal_and_rechecks_the_replacement_port()
    {
        if (scenario is "recovered" or "exhausted" or "diagnostics-unavailable")
        {
            _docker
                .Events.Should()
                .ContainInOrder("ready-1", "inspect", "logs", "remove", "start-2", "port-2", "ready-2");
            _docker.EvidenceAtRemoval.Should().BeTrue();
        }
        _docker
            .Removals.Should()
            .Be(
                scenario
                    is "recovered"
                        or "exhausted"
                        or "diagnostics-unavailable"
                        or "cleanup-failure"
                        or "restart-failure"
                    ? 1
                    : 0
            );
        _docker.ProviderStarts.Should().Be(1);
        _docker.BrokerStarts.Should().Be(1);
    }

    [Test]
    public void It_publishes_bounded_readiness_state_and_log_signals_without_private_output()
    {
        int count = scenario switch
        {
            "healthy" or "unrelated" or "cancel" or "cancel-inspection" => 0,
            "recovered" or "exhausted" or "diagnostics-unavailable" => 2,
            _ => 1,
        };
        _evidence.Should().HaveCount(count);
        foreach (string json in _evidence)
        {
            json.Should()
                .NotContain("private-details")
                .And.NotContain(Prefix)
                .And.NotContain(CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword);
            using var document = JsonDocument.Parse(json);
            var evidence = document.RootElement;
            evidence.GetProperty("Injected").GetBoolean().Should().BeTrue();
            string outcome = evidence.GetProperty("Outcome").GetString()!;
            int attempt = evidence.GetProperty("Attempt").GetInt32();
            if (outcome == "Recovered")
            {
                attempt.Should().Be(2);
                scenario.Should().BeOneOf("recovered", "diagnostics-unavailable");
                continue;
            }
            outcome.Should().Be("TimedOut");
            evidence.GetProperty("Probes").GetInt32().Should().Be(45);
            evidence.GetProperty("LastStatusCode").GetInt32().Should().Be(503);
            evidence
                .GetProperty("RecreationPermitted")
                .GetBoolean()
                .Should()
                .Be(attempt == 1 && scenario != "keep-containers");
            evidence
                .GetProperty("Logs")
                .GetProperty("Available")
                .GetBoolean()
                .Should()
                .Be(scenario != "diagnostics-unavailable");
            if (scenario != "diagnostics-unavailable")
            {
                evidence.GetProperty("Container").GetProperty("Status").GetString().Should().Be("running");
                evidence
                    .GetProperty("Logs")
                    .GetProperty("Markers")
                    .EnumerateArray()
                    .Select(x => x.GetString())
                    .Should()
                    .Equal("TimeoutException", "ERROR");
            }
        }
    }

    private sealed class RecordingDocker(string scenario, CancellationTokenSource cancellation) : IDockerCli
    {
        public bool IsOffline => false;
        public int Starts { get; private set; }
        public int ProviderStarts { get; private set; }
        public int BrokerStarts { get; private set; }
        public int Removals { get; private set; }
        public bool EvidenceAtRemoval { get; private set; }
        private readonly string[] _previousEvidence = Directory.GetFiles(
            TestContext.CurrentContext.WorkDirectory,
            Pattern
        );
        public List<string> Events { get; } = [];
        public Exception Unrelated { get; } = new InvalidOperationException("unrelated failure");

        public Task RequireDockerAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DockerCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (arguments[0] == "port")
            {
                Events.Add("port-" + Starts);
                return Task.FromResult(new DockerCommandResult(0, $"127.0.0.1:{18083 + Starts}", ""));
            }
            if (arguments[0] == "rm" && arguments.Contains(Prefix + "-connect"))
            {
                Events.Add("remove");
                Removals++;
                EvidenceAtRemoval = Directory
                    .GetFiles(TestContext.CurrentContext.WorkDirectory, Pattern)
                    .Except(_previousEvidence)
                    .Any(path =>
                        File.ReadAllText(path).Contains("\"Outcome\":\"TimedOut\"", StringComparison.Ordinal)
                    );
                if (scenario == "cleanup-failure")
                {
                    throw Unrelated;
                }
            }
            return RunAllowingFailureAsync(arguments, cancellationToken);
        }

        public Task<DockerCommandResult> RunAllowingFailureAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (arguments[0] == "run")
            {
                if (arguments.Contains(Prefix + "-provider"))
                {
                    ProviderStarts++;
                }
                if (arguments.Contains(Prefix + "-broker"))
                {
                    BrokerStarts++;
                }
                if (arguments.Contains(Prefix + "-connect"))
                {
                    Starts++;
                    Events.Add("start-" + Starts);
                    if (scenario == "restart-failure" && Starts == 2)
                    {
                        throw Unrelated;
                    }
                }
            }
            if (arguments[0] is "inspect" or "logs")
            {
                Events.Add(arguments[0]);
                if (scenario == "cancel-inspection")
                {
                    cancellation.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (scenario == "diagnostics-unavailable")
                {
                    throw Unrelated;
                }
                return Task.FromResult(
                    new DockerCommandResult(
                        0,
                        arguments[0] == "inspect"
                            ? "{\"Status\":\"running\",\"ExitCode\":0,\"OOMKilled\":false,\"Error\":\"private-details\"}"
                            : "ERROR TimeoutException private-details "
                                + CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword,
                        ""
                    )
                );
            }
            return Task.FromResult(new DockerCommandResult(0, "", ""));
        }
    }
}
