// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
[Parallelizable]
public sealed class Given_PinnedImageFixtureMappedPortParsing
{
    private const string ConnectContainerName = "dms-cdc-template-connect";

    [Test]
    public void It_parses_a_valid_docker_mapped_port()
    {
        Uri uri = CdcConnectorTemplatePinnedImageFixture.ParseMappedConnectBaseUri(
            ConnectContainerName,
            "0.0.0.0:32768\n"
        );

        uri.Should().Be(new Uri("http://127.0.0.1:32768"));
    }

    [TestCase("")]
    [TestCase("\n")]
    public void It_reports_empty_mapped_port_output(string dockerPortOutput)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            CdcConnectorTemplatePinnedImageFixture.ParseMappedConnectBaseUri(
                ConnectContainerName,
                dockerPortOutput
            )
        )!;

        using var _ = new AssertionScope();
        exception.Message.Should().Contain(ConnectContainerName);
        exception.Message.Should().Contain("no output");
        exception.Message.Should().Contain("<empty>");
    }

    [Test]
    public void It_reports_mapped_port_output_without_a_delimiter()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            CdcConnectorTemplatePinnedImageFixture.ParseMappedConnectBaseUri(
                ConnectContainerName,
                "0.0.0.0 32768"
            )
        )!;

        using var _ = new AssertionScope();
        exception.Message.Should().Contain(ConnectContainerName);
        exception.Message.Should().Contain("':' delimiter");
        exception.Message.Should().Contain("0.0.0.0 32768");
    }

    [Test]
    public void It_reports_mapped_port_output_with_an_empty_port()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            CdcConnectorTemplatePinnedImageFixture.ParseMappedConnectBaseUri(ConnectContainerName, "0.0.0.0:")
        )!;

        using var _ = new AssertionScope();
        exception.Message.Should().Contain(ConnectContainerName);
        exception.Message.Should().Contain("numeric TCP port");
        exception.Message.Should().Contain("0.0.0.0:");
    }

    [Test]
    public void It_sanitizes_invalid_mapped_port_output()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            CdcConnectorTemplatePinnedImageFixture.ParseMappedConnectBaseUri(
                ConnectContainerName,
                $"0.0.0.0:{CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword}"
            )
        )!;

        using var _ = new AssertionScope();
        exception.Message.Should().Contain("[redacted]");
        exception
            .Message.Should()
            .NotContain(CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword);
    }
}

[TestFixture]
[Parallelizable]
public sealed class Given_PinnedImageFixtureStartupFailureCleanup
{
    private const string ResourcePrefix = "dms-cdc-startup-test";

    [Test]
    public void It_disposes_partially_started_resources_when_start_docker_resources_fails()
    {
        var startupException = new OperationCanceledException("connect startup canceled");
        var docker = new RecordingDockerCli(arguments =>
            IsKafkaConnectRunCommand(arguments) ? startupException : null
        );

        OperationCanceledException exception = Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                CdcProvider.Postgresql,
                BuildSettings(),
                docker,
                ResourcePrefix,
                CancellationToken.None
            )
        )!;

        using var _ = new AssertionScope();
        exception.Should().BeSameAs(startupException);
        AssertCleanupCommandsWereRun(docker);
    }

    [Test]
    public void It_disposes_started_resources_when_startup_is_canceled()
    {
        AssertCancellationCleanup(new OperationCanceledException("port read canceled"));
    }

    [Test]
    public void It_disposes_started_resources_when_startup_task_is_canceled()
    {
        AssertCancellationCleanup(new TaskCanceledException("port read task canceled"));
    }

    [Test]
    public void It_disposes_started_resources_when_kafka_connect_wait_is_canceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var docker = new RecordingDockerCli(arguments => null);

        Exception exception = Assert.CatchAsync(async () =>
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                CdcProvider.Postgresql,
                BuildSettings(),
                docker,
                ResourcePrefix,
                cancellation.Token
            )
        )!;

        using var _ = new AssertionScope();
        exception.Should().BeAssignableTo<OperationCanceledException>();
        docker.Commands.Should().Contain($"run port {ResourcePrefix}-connect 8083/tcp");
        AssertCleanupCommandsWereRun(docker);
    }

    [Test]
    public void It_keeps_started_resources_when_keep_containers_is_enabled_after_startup_failure()
    {
        var startupException = new OperationCanceledException("port mapping canceled");
        var docker = new RecordingDockerCli(arguments => IsPortCommand(arguments) ? startupException : null);

        OperationCanceledException exception = Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                CdcProvider.Postgresql,
                BuildSettings(keepContainers: true),
                docker,
                ResourcePrefix,
                CancellationToken.None
            )
        )!;

        using var _ = new AssertionScope();
        exception.Should().BeSameAs(startupException);
        docker.Commands.Intersect(ExpectedCleanupCommands(), StringComparer.Ordinal).Should().BeEmpty();
    }

    [Test]
    public void It_disposes_started_resources_when_mapped_port_output_is_invalid()
    {
        var docker = new RecordingDockerCli(
            arguments => null,
            mappedPortOutput: $"malformed {CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword}"
        );

        InvalidOperationException exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                CdcProvider.Postgresql,
                BuildSettings(),
                docker,
                ResourcePrefix,
                CancellationToken.None,
                applyPrerequisitePolicy: false
            )
        )!;

        using var _ = new AssertionScope();
        exception.Message.Should().Contain($"{ResourcePrefix}-connect");
        exception.Message.Should().Contain("[redacted]");
        exception
            .Message.Should()
            .NotContain(CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword);
        AssertCleanupCommandsWereRun(docker);
    }

    private static void AssertCancellationCleanup<TException>(TException startupException)
        where TException : OperationCanceledException
    {
        var docker = new RecordingDockerCli(arguments => IsPortCommand(arguments) ? startupException : null);

        TException exception = Assert.ThrowsAsync<TException>(async () =>
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                CdcProvider.Postgresql,
                BuildSettings(),
                docker,
                ResourcePrefix,
                CancellationToken.None
            )
        )!;

        using var _ = new AssertionScope();
        exception.Should().BeSameAs(startupException);
        AssertCleanupCommandsWereRun(docker);
    }

    private static CdcConnectorTemplateSmokeSettings BuildSettings(bool keepContainers = false) =>
        new(
            "connect@sha256:qualified",
            "redpanda:qualified",
            "postgres:qualified",
            FailFast: true,
            keepContainers
        );

    private static void AssertCleanupCommandsWereRun(RecordingDockerCli docker)
    {
        docker.Commands.TakeLast(4).Should().Equal(ExpectedCleanupCommands());
    }

    private static IReadOnlyList<string> ExpectedCleanupCommands() =>
        [
            $"allow rm -f {ResourcePrefix}-connect",
            $"allow rm -f {ResourcePrefix}-provider",
            $"allow rm -f {ResourcePrefix}-broker",
            $"allow network rm {ResourcePrefix}-network",
        ];

    private static bool IsKafkaConnectRunCommand(IReadOnlyList<string> arguments) =>
        arguments.Count > 0
        && string.Equals(arguments[0], "run", StringComparison.Ordinal)
        && arguments.Contains($"{ResourcePrefix}-connect");

    private static bool IsPortCommand(IReadOnlyList<string> arguments) =>
        arguments.Count == 3
        && string.Equals(arguments[0], "port", StringComparison.Ordinal)
        && string.Equals(arguments[1], $"{ResourcePrefix}-connect", StringComparison.Ordinal)
        && string.Equals(arguments[2], "8083/tcp", StringComparison.Ordinal);

    private sealed class RecordingDockerCli(
        Func<IReadOnlyList<string>, Exception?> failureForCommand,
        string mappedPortOutput = "127.0.0.1:32768\n"
    ) : IDockerCli
    {
        private readonly List<string> _commands = [];

        public IReadOnlyList<string> Commands => _commands;

        public bool IsOffline => false;

        public Task RequireDockerAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DockerCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken
        )
        {
            _commands.Add($"run {CommandText(arguments)}");
            Exception? failure = failureForCommand(arguments);
            if (failure is not null)
            {
                throw failure;
            }

            return Task.FromResult(ResultFor(arguments));
        }

        public Task<DockerCommandResult> RunAllowingFailureAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken
        )
        {
            _commands.Add($"allow {CommandText(arguments)}");
            return Task.FromResult(ResultFor(arguments));
        }

        private DockerCommandResult ResultFor(IReadOnlyList<string> arguments) =>
            IsPortCommand(arguments)
                ? new DockerCommandResult(0, mappedPortOutput, string.Empty)
                : new DockerCommandResult(0, string.Empty, string.Empty);

        private static string CommandText(IReadOnlyList<string> arguments) => string.Join(" ", arguments);
    }
}
