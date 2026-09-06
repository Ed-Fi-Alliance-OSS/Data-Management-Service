// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Sockets;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(0, "", "success")]
[TestFixture(1, "Bind for 127.0.0.1 failed: port is already allocated", "success")]
[TestFixture(2, "bind: address already in use", "success")]
[TestFixture(1, "failed to bind host port for 127.0.0.1", "success")]
[TestFixture(1, "BIND: ADDRESS ALREADY IN USE", "success")]
[TestFixture(3, "bind: address already in use", "exhausted")]
[TestFixture(3, "bind: address already in use", "prerequisite")]
[TestFixture(3, "invalid mount config for type bind", "unrelated")]
[TestFixture(3, "pull access denied for broker", "unrelated")]
[TestFixture(3, "failed to create endpoint: network unavailable", "unrelated")]
[TestFixture(3, "bind: address already in use", "cancel")]
[TestFixture(3, "bind: address already in use", "cleanup-failure")]
[Parallelizable]
public sealed class Given_PinnedImageFixtureBrokerStartup(int failures, string error, string scenario)
{
    private const string ResourcePrefix = "dms-cdc-broker-startup-test";
    private RecordingDockerCli _docker = null!;
    private Exception _failure = null!;

    [SetUp]
    public void Setup()
    {
        using var cancellation = new CancellationTokenSource();
        using var docker = new RecordingDockerCli(failures, error, scenario, cancellation);
        _docker = docker;
        // Expected fail-fast Assert.Fail must not mark this test's own result as failed.
        using var isolated = new NUnit.Framework.Internal.TestExecutionContext.IsolatedContext();
        _failure = Assert.CatchAsync(async () =>
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                CdcProvider.Postgresql,
                new("connect@sha256:qualified", "redpanda:qualified", "postgres:qualified", true, false),
                docker,
                ResourcePrefix,
                cancellation.Token,
                applyPrerequisitePolicy: scenario == "prerequisite"
            )
        )!;
    }

    [Test]
    public void It_limits_retries_to_port_binding_failures_and_preserves_the_terminal_outcome()
    {
        int attempts = scenario switch
        {
            "success" => failures + 1,
            "exhausted" or "prerequisite" => 3,
            _ => 1,
        };
        using var _ = new AssertionScope();
        _docker.BrokerRuns.Should().HaveCount(attempts);
        switch (scenario)
        {
            case "success":
                _failure.Should().BeSameAs(_docker.ProviderReached);
                break;
            case "cancel":
                _failure.Should().BeAssignableTo<OperationCanceledException>();
                break;
            case "cleanup-failure":
                _failure.Should().BeSameAs(_docker.CleanupFailure);
                break;
            default:
                _failure.Message.Should().Contain(error).And.Contain("docker exited with code 125");
                _failure.Message.Should().Contain("[redacted]").And.Contain("[truncated]");
                _failure
                    .Message.Should()
                    .NotContain(CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword);
                // Includes the fixed prerequisite diagnostic in the fail-fast case.
                _failure.Message.Length.Should().BeLessThan(3000);
                break;
        }
    }

    [Test]
    public void It_advertises_the_same_fresh_loopback_port_that_docker_maps_on_every_attempt()
    {
        List<int> ports = [];
        foreach (string[] arguments in _docker.BrokerRuns)
        {
            string mapping = arguments[Array.IndexOf(arguments, "-p") + 1];
            int port = int.Parse(mapping.Split(':')[1]);
            ports.Add(port);
            using var _ = new AssertionScope();
            mapping.Should().Be($"127.0.0.1:{port}:19092");
            arguments[Array.IndexOf(arguments, "--advertise-kafka-addr") + 1]
                .Should()
                .Be($"internal://{ResourcePrefix}-broker:9092,external://127.0.0.1:{port}");
        }
        ports.Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void It_removes_each_failed_container_before_retry_and_cleans_up_after_terminal_failure()
    {
        string[] brokerCommands = _docker
            .Commands.Where(command => command is "broker-run" or "retry-remove")
            .ToArray();
        List<string> expected = [];
        for (int index = 0; index < _docker.BrokerRuns.Count; index++)
        {
            expected.Add("broker-run");
            if (index < _docker.BrokerRuns.Count - 1 || scenario is "cancel" or "cleanup-failure")
            {
                expected.Add("retry-remove");
            }
        }
        using var _ = new AssertionScope();
        brokerCommands.Should().Equal(expected);
        _docker
            .Commands.TakeLast(4)
            .Should()
            .Equal(
                $"rm -f {ResourcePrefix}-connect",
                $"rm -f {ResourcePrefix}-provider",
                $"rm -f {ResourcePrefix}-broker",
                $"network rm {ResourcePrefix}-network"
            );
        _docker.RetryCleanupHadUsableToken.Should().BeTrue();
    }

    private sealed class RecordingDockerCli(
        int failures,
        string error,
        string scenario,
        CancellationTokenSource cancellation
    ) : IDockerCli, IDisposable
    {
        private readonly List<TcpListener> _competingListeners = [];
        public List<string[]> BrokerRuns { get; } = [];
        public List<string> Commands { get; } = [];
        public bool RetryCleanupHadUsableToken { get; private set; } = true;
        public Exception ProviderReached { get; } = new InvalidOperationException("provider reached");
        public Exception CleanupFailure { get; } = new InvalidOperationException("retry cleanup failed");
        public bool IsOffline => false;

        public Task RequireDockerAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DockerCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken
        )
        {
            if (arguments[0] == "run" && arguments.Contains($"{ResourcePrefix}-provider"))
            {
                throw ProviderReached;
            }
            if (arguments[0] == "rm")
            {
                Commands.Add("retry-remove");
                RetryCleanupHadUsableToken &=
                    cancellationToken.CanBeCanceled && !cancellationToken.IsCancellationRequested;
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
            if (arguments[0] != "run")
            {
                Commands.Add(string.Join(" ", arguments));
                return Task.FromResult(new DockerCommandResult(0, "", ""));
            }
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add("broker-run");
            string[] run = arguments.ToArray();
            BrokerRuns.Add(run);
            if (BrokerRuns.Count > failures)
            {
                return Task.FromResult(new DockerCommandResult(0, "broker-id", ""));
            }
            // Occupy the released reservation, reproducing the race with another local process.
            int port = int.Parse(run[Array.IndexOf(run, "-p") + 1].Split(':')[1]);
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            _competingListeners.Add(listener);
            if (scenario == "cancel")
            {
                cancellation.Cancel();
            }
            return Task.FromResult(
                new DockerCommandResult(
                    125,
                    new string('x', 4000),
                    $"{error}; {CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword}; {new string('y', 4000)}"
                )
            );
        }

        public void Dispose()
        {
            foreach (TcpListener listener in _competingListeners)
            {
                listener.Dispose();
            }
        }
    }
}
