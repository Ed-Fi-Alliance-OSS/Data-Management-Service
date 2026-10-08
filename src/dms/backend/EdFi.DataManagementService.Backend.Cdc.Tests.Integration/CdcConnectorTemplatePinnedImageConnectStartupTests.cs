// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(0, "", "success")]
[TestFixture(1, "Bind for 127.0.0.1 failed: port is already allocated", "success")]
[TestFixture(2, "BIND: ADDRESS ALREADY IN USE", "success")]
[TestFixture(1, "failed to bind host port for 127.0.0.1", "success")]
[TestFixture(1, "bind: An attempt was made to access a socket", "success")]
[TestFixture(3, "bind: address already in use", "exhausted")]
[TestFixture(3, "bind: address already in use", "prerequisite")]
[TestFixture(3, "invalid mount config for type bind", "unrelated")]
[TestFixture(3, "pull access denied for worker", "unrelated")]
[TestFixture(3, "failed to create endpoint: network unavailable", "unrelated")]
[TestFixture(3, "bind: address already in use", "cancel")]
[TestFixture(3, "bind: address already in use", "cleanup-failure")]
[NonParallelizable]
public sealed class Given_PinnedImageFixtureConnectStartup(int failures, string error, string scenario)
{
    private const string ResourcePrefix = "dms-cdc-connect-startup-test";
    private const string PrivateDetails = "private-docker-output-credentials";
    private RecordingDockerCli _docker = null!;
    private Exception _failure = null!;
    private string _evidence = string.Empty;
    private Uri _connect = null!;
    private Uri _metrics = null!;
    private int _preparations;

    [SetUp]
    public void Setup()
    {
        string directory = TestContext.CurrentContext.WorkDirectory;
        const string pattern = "admission-evidence-connect-startup-*.json";
        string[] before = Directory.GetFiles(directory, pattern);
        using var cancellation = new CancellationTokenSource();
        _docker = new(failures, error, scenario, cancellation);
        _preparations = 0;
        using var isolated = new NUnit.Framework.Internal.TestExecutionContext.IsolatedContext();
        _failure = Assert.CatchAsync(async () =>
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                CdcProvider.Postgresql,
                new("connect@sha256:qualified", "redpanda:qualified", "postgres:qualified", true, false),
                _docker,
                ResourcePrefix,
                cancellation.Token,
                applyPrerequisitePolicy: scenario == "prerequisite",
                beforeWorker: async (fixture, token) =>
                {
                    _preparations++;
                    _connect = fixture.ControllerConnectEndpoint;
                    _metrics = await fixture.ControllerMetricsEndpointAsync(token);
                    // Another process cannot steal either endpoint during preparation.
                    AssertReserved(_connect.Port);
                    AssertReserved(_metrics.Port);
                },
                exposeBroker: true
            )
        )!;
        string path = Directory.GetFiles(directory, pattern).Except(before).Should().ContainSingle().Subject;
        _evidence = File.ReadAllText(path);
    }

    [Test]
    public void It_retries_only_recognized_conflicts_and_preserves_the_terminal_outcome()
    {
        int attempts = scenario switch
        {
            "success" => failures + 1,
            "exhausted" or "prerequisite" => 3,
            _ => 1,
        };
        _docker.ConnectRuns.Should().HaveCount(attempts);
        switch (scenario)
        {
            case "success":
                _failure.Should().BeSameAs(_docker.PortReadReached);
                break;
            case "cancel":
                _failure.Should().BeAssignableTo<OperationCanceledException>();
                break;
            case "cleanup-failure":
                _failure.Should().BeSameAs(_docker.CleanupFailure);
                break;
            case "prerequisite":
                _failure.Should().BeOfType<AssertionException>();
                _failure.Message.Should().Contain("CDC_TEMPLATE_PINNED_IMAGE_DOCKER_PREREQUISITE_FAILURE");
                break;
            default:
                _failure.Should().BeOfType<InvalidOperationException>();
                _failure.Message.Should().Contain("exitCode=125");
                break;
        }
    }

    [Test]
    public void It_preserves_prepared_endpoints_and_releases_reservations_before_launch()
    {
        _preparations.Should().Be(1);
        foreach (string[] run in _docker.ConnectRuns)
        {
            run.Where((_, index) => index > 0 && run[index - 1] == "-p")
                .Should()
                .Equal($"127.0.0.1:{_connect.Port}:8083", $"127.0.0.1:{_metrics.Port}:9404");
        }
        // Also verifies disposal after terminal failure and cancellation.
        AssertAvailable(_connect.Port);
        AssertAvailable(_metrics.Port);
    }

    [Test]
    public void It_cleans_failed_containers_before_retry_and_stops_if_cleanup_or_cancellation_fails()
    {
        List<string> expected = [];
        for (int index = 0; index < _docker.ConnectRuns.Count; index++)
        {
            expected.Add("connect-run");
            if (index < _docker.ConnectRuns.Count - 1 || scenario is "cancel" or "cleanup-failure")
            {
                expected.Add("retry-remove");
            }
        }
        _docker
            .Commands.Where(command => command is "connect-run" or "retry-remove")
            .Should()
            .Equal(expected);
        _docker
            .Commands.TakeLast(4)
            .Should()
            .Equal(
                $"rm -f -v {ResourcePrefix}-connect",
                $"rm -f -v {ResourcePrefix}-provider",
                $"rm -f -v {ResourcePrefix}-broker",
                $"network rm {ResourcePrefix}-network"
            );
    }

    [Test]
    public void It_retains_each_exit_code_and_fixed_category_without_private_docker_output()
    {
        _evidence.Should().NotContain(PrivateDetails).And.NotContain(ResourcePrefix).And.NotContain("sha256");
        _failure.Message.Should().NotContain(PrivateDetails);
        using var evidence = JsonDocument.Parse(_evidence);
        evidence.RootElement.GetProperty("Stage").GetString().Should().Be("start-kafka-connect");
        var attempts = evidence.RootElement.GetProperty("Attempts").EnumerateArray().ToArray();
        attempts.Should().HaveCount(_docker.ConnectRuns.Count);
        for (int index = 0; index < attempts.Length; index++)
        {
            bool success = index >= failures;
            using var _ = new AssertionScope();
            attempts[index].GetProperty("Attempt").GetInt32().Should().Be(index + 1);
            attempts[index].GetProperty("ExitCode").GetInt32().Should().Be(success ? 0 : 125);
            attempts[index]
                .GetProperty("Category")
                .GetString()
                .Should()
                .Be(
                    (success, scenario) switch
                    {
                        (true, _) => "Succeeded",
                        (_, "unrelated") => "OtherDockerFailure",
                        _ => "PortBindingConflict",
                    }
                );
        }
    }

    private static void AssertReserved(int port)
    {
        using var listener = new TcpListener(IPAddress.Loopback, port);
        Assert
            .Throws<SocketException>(() => listener.Start())!
            .SocketErrorCode.Should()
            .Be(SocketError.AddressAlreadyInUse);
    }

    private static void AssertAvailable(int port)
    {
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
    }

    private sealed class RecordingDockerCli(
        int failures,
        string error,
        string scenario,
        CancellationTokenSource cancellation
    ) : IDockerCli
    {
        public List<string[]> ConnectRuns { get; } = [];
        public List<string> Commands { get; } = [];
        public Exception PortReadReached { get; } = new InvalidOperationException("port read reached");
        public Exception CleanupFailure { get; } = new InvalidOperationException("cleanup failed");
        public bool IsOffline => false;

        public Task RequireDockerAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DockerCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken
        )
        {
            if (arguments[0] == "port")
            {
                throw PortReadReached;
            }
            if (arguments[0] == "rm")
            {
                Commands.Add("retry-remove");
                cancellationToken.CanBeCanceled.Should().BeTrue();
                cancellationToken.IsCancellationRequested.Should().BeFalse();
                if (scenario == "cleanup-failure")
                {
                    throw CleanupFailure;
                }
            }
            return Task.FromResult(new DockerCommandResult(0, "", ""));
        }

        public Task<DockerCommandResult> RunAllowingFailureAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken
        )
        {
            if (arguments[0] != "run" || !arguments.Contains(ResourcePrefix + "-connect"))
            {
                Commands.Add(string.Join(" ", arguments));
                return Task.FromResult(new DockerCommandResult(0, "", ""));
            }
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add("connect-run");
            string[] run = arguments.ToArray();
            ConnectRuns.Add(run);
            foreach (string mapping in run.Where((_, index) => index > 0 && run[index - 1] == "-p"))
            {
                AssertAvailable(int.Parse(mapping.Split(':')[1]));
            }
            if (ConnectRuns.Count > failures)
            {
                return Task.FromResult(new DockerCommandResult(0, PrivateDetails, ""));
            }
            if (scenario == "cancel")
            {
                cancellation.Cancel();
            }
            return Task.FromResult(
                new DockerCommandResult(125, PrivateDetails, error + "; " + PrivateDetails)
            );
        }
    }
}
