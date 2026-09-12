// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture("success")]
[TestFixture("pause")]
[TestFixture("callback")]
[TestFixture("cancel")]
[TestFixture("kill")]
[TestFixture("unpause")]
[TestFixture("start")]
[Category("CdcMessageContract")]
public sealed class Given_MessageContractRetryCleanup(string failure)
{
    private const string Sentinel = "synthetic-body-password-tenant-connection-string";
    private RecordingDocker _docker = null!;
    private string _message = string.Empty;
    private MessageContractInterruptionEvidence _evidence = null!;

    [SetUp]
    public async Task Setup()
    {
        _docker = new RecordingDocker(failure);
        using var cancellation = new CancellationTokenSource();
        try
        {
            _evidence = await MessageContractDeliveryInterruption.ExecuteAsync(
                _docker,
                "fixture-broker",
                "fixture-worker",
                token =>
                {
                    _docker.Paused.Should().BeTrue();
                    _docker.Running.Should().BeTrue();
                    if (failure == "callback")
                    {
                        throw new InvalidOperationException(Sentinel);
                    }
                    if (failure == "cancel")
                    {
                        cancellation.Cancel();
                        token.ThrowIfCancellationRequested();
                    }
                    return Task.CompletedTask;
                },
                cancellation.Token
            );
        }
        catch (AssertionException exception)
        {
            _message = exception.ToString();
        }
    }

    [Test]
    [Property("ScenarioSuffix", "RESTORATION")]
    public void It_attempts_broker_and_worker_restoration_with_independent_uncanceled_tokens()
    {
        _docker.Commands.Should().ContainInOrder("pause", "unpause", "start");
        _docker.CleanupTokensCanceled.Should().OnlyContain(canceled => !canceled);
        if (failure != "unpause")
        {
            _docker.Paused.Should().BeFalse();
        }
        if (failure != "start")
        {
            _docker.Running.Should().BeTrue();
        }
    }

    [Test]
    [Property("ScenarioSuffix", "DIAGNOSTICS")]
    public void It_preserves_only_bounded_failure_phase_or_verified_restart_evidence()
    {
        if (failure == "success")
        {
            _message.Should().BeEmpty();
            _evidence
                .Should()
                .Be(new MessageContractInterruptionEvidence(true, true, true, true, true, true));
            _docker.Commands.Should().ContainInOrder("pause", "kill", "unpause", "start");
        }
        else
        {
            _message.Should().Contain("details redacted").And.NotContain(Sentinel);
            _message
                .Should()
                .Contain(
                    failure switch
                    {
                        "pause" => "pause-broker",
                        "callback" or "cancel" => "observe-pending-delivery",
                        "kill" => "kill-worker",
                        _ => "restoration both attempted",
                    }
                );
        }
    }

    private sealed class RecordingDocker(string failure) : IDockerCli
    {
        public bool IsOffline => false;
        public bool Paused { get; private set; }
        public bool Running { get; private set; } = true;
        public List<string> Commands { get; } = [];
        public List<bool> CleanupTokensCanceled { get; } = [];
        private int _starts;

        public Task RequireDockerAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DockerCommandResult> RunAllowingFailureAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken
        ) => RunAsync(arguments, cancellationToken);

        public Task<DockerCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken
        )
        {
            string command = arguments[0];
            Commands.Add(command);
            if (command is "unpause" or "start")
            {
                CleanupTokensCanceled.Add(cancellationToken.IsCancellationRequested);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (command == "inspect")
            {
                string output = arguments[2] switch
                {
                    "{{.Id}}" => "same-worker-id",
                    "{{.State.StartedAt}}" => _starts.ToString(),
                    "{{.State.Paused}}" => Paused ? "true" : "false",
                    "{{.State.Running}}" => Running ? "true" : "false",
                    _ => throw new InvalidOperationException(),
                };
                return Task.FromResult(new DockerCommandResult(0, output, ""));
            }
            if (command == failure && command is "unpause" or "start")
            {
                throw new InvalidOperationException(Sentinel);
            }
            switch (command)
            {
                case "pause":
                    Paused = true;
                    break;
                case "unpause":
                    Paused = false;
                    break;
                case "kill":
                    Running = false;
                    break;
                case "start":
                    Running = true;
                    _starts++;
                    break;
                default:
                    throw new InvalidOperationException();
            }
            // Simulate a Docker mutation completing before its canceled/failed response arrives.
            if (command == failure)
            {
                throw new InvalidOperationException(Sentinel);
            }
            return Task.FromResult(new DockerCommandResult(0, "", ""));
        }
    }
}
