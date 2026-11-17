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

[TestFixture(0, "", "success", "native")]
[TestFixture(0, "", "success", "compose")]
[TestFixture(1, "Bind for 127.0.0.1 failed: port is already allocated", "success", "native")]
[TestFixture(1, "Bind for 127.0.0.1 failed: port is already allocated", "success", "compose")]
[TestFixture(2, "bind: address already in use", "success", "native")]
[TestFixture(2, "bind: address already in use", "success", "compose")]
[TestFixture(1, "failed to bind host port for 127.0.0.1", "success", "native")]
[TestFixture(1, "failed to bind host port for 127.0.0.1", "success", "compose")]
[TestFixture(1, "BIND: ADDRESS ALREADY IN USE", "success", "native")]
[TestFixture(1, "BIND: ADDRESS ALREADY IN USE", "success", "compose")]
[TestFixture(3, "bind: address already in use", "exhausted", "native")]
[TestFixture(3, "bind: address already in use", "exhausted", "compose")]
[TestFixture(3, "bind: address already in use", "prerequisite", "native")]
[TestFixture(3, "bind: address already in use", "prerequisite", "compose")]
[TestFixture(3, "invalid mount config for type bind", "unrelated", "native")]
[TestFixture(3, "invalid mount config for type bind", "unrelated", "compose")]
[TestFixture(3, "pull access denied for broker", "unrelated", "native")]
[TestFixture(3, "pull access denied for broker", "unrelated", "compose")]
[TestFixture(3, "failed to create endpoint: network unavailable", "unrelated", "native")]
[TestFixture(3, "failed to create endpoint: network unavailable", "unrelated", "compose")]
[TestFixture(3, "bind: address already in use", "cancel", "native")]
[TestFixture(3, "bind: address already in use", "cancel", "compose")]
[TestFixture(3, "bind: address already in use", "cleanup-failure", "native")]
[TestFixture(3, "bind: address already in use", "cleanup-failure", "compose")]
[NonParallelizable]
public sealed class Given_PinnedImageFixtureBrokerStartup(
    int failures,
    string error,
    string scenario,
    string mode
)
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
                new(
                    "connect@sha256:qualified",
                    CdcComposeBrokerSizeDeployment.BrokerImage,
                    "postgres:qualified",
                    true,
                    false
                ),
                docker,
                ResourcePrefix,
                cancellation.Token,
                applyPrerequisitePolicy: scenario == "prerequisite",
                exposeBroker: true,
                composeKafka: mode == "compose"
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
        foreach (var binding in _docker.Bindings)
        {
            ports.Add(binding.Port);
            using var _ = new AssertionScope();
            binding.ContainerPort.Should().Be(mode == "compose" ? 19092 : 29092);
            binding
                .Advertised.Should()
                .Be($"INTERNAL://{ResourcePrefix}-broker:9092,EXTERNAL://127.0.0.1:{binding.Port}");
        }
        ports.Should().OnlyHaveUniqueItems();
        if (mode == "native")
        {
            foreach (string[] arguments in _docker.BrokerRuns)
            {
                using var _ = new AssertionScope();
                arguments[^1].Should().Be(CdcComposeBrokerSizeDeployment.BrokerImage);
                arguments.Should().Contain("KAFKA_PROCESS_ROLES=broker,controller");
                arguments.Should().Contain($"KAFKA_CONTROLLER_QUORUM_VOTERS=1@{ResourcePrefix}-broker:9093");
            }
        }
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
            .Commands.Where(command => command.StartsWith("rm -f -v ") || command.StartsWith("network rm "))
            .Should()
            .Equal(
                $"rm -f -v {ResourcePrefix}-connect",
                $"rm -f -v {ResourcePrefix}-provider",
                $"rm -f -v {ResourcePrefix}-broker",
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
        public List<(int Port, int ContainerPort, string Advertised)> Bindings { get; } = [];
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
            if (arguments[0] == "compose" && arguments.Contains("config"))
            {
                return Task.FromResult(
                    new DockerCommandResult(
                        0,
                        """{"services":{"kafka":{"volumes":[{"source":"kafka-data","target":"/tmp/kraft-combined-logs"}]}}}""",
                        ""
                    )
                );
            }
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
            bool composeStart =
                arguments[0] == "compose" && arguments.Contains("up") && arguments[^1] == "kafka";
            if (arguments[0] != "run" && !composeStart)
            {
                Commands.Add(string.Join(" ", arguments));
                return Task.FromResult(new DockerCommandResult(0, "", ""));
            }
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add("broker-run");
            string[] run = arguments.ToArray();
            BrokerRuns.Add(run);
            string mapping;
            string advertised;
            if (composeStart)
            {
                using var compose = JsonDocument.Parse(File.ReadAllText(run[Array.IndexOf(run, "-f") + 1]));
                var broker = compose.RootElement.GetProperty("services").GetProperty("kafka");
                mapping = broker.GetProperty("ports")[0].GetString()!;
                advertised = broker
                    .GetProperty("environment")
                    .GetProperty("KAFKA_ADVERTISED_LISTENERS")
                    .GetString()!;
                string environment = File.ReadAllText(run[Array.IndexOf(run, "--env-file") + 1]);
                environment.Should().Contain("KAFKA_PORT=" + mapping.Split(':')[1]);
            }
            else
            {
                mapping = run[Array.IndexOf(run, "-p") + 1];
                advertised = run.Single(value => value.StartsWith("KAFKA_ADVERTISED_LISTENERS="))
                    .Split('=', 2)[1];
            }
            int port = int.Parse(mapping.Split(':')[1]);
            Bindings.Add((port, int.Parse(mapping.Split(':')[2]), advertised));
            if (BrokerRuns.Count > failures)
            {
                return Task.FromResult(new DockerCommandResult(0, "broker-id", ""));
            }
            // Occupy the released reservation, reproducing the race with another local process.
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
