// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
[Category("CdcMessageContract")]
public sealed class Given_MessageContractRunner_prerequisites
{
    private static readonly string Image = $"contract/connect@sha256:{new string('a', 64)}";

    [TestCase("")]
    [TestCase("connect:latest")]
    [TestCase("connect@sha256:qualified")]
    [TestCase(
        "credentials-sentinel@connect@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
    )]
    public void It_rejects_missing_or_mutable_images_without_echoing_the_value(string image)
    {
        var docker = new RecordingDocker();
        var runner = new MessageContractRunner(docker, image);
        MessageContractRunnerPrerequisiteException failure =
            Assert.ThrowsAsync<MessageContractRunnerPrerequisiteException>(async () =>
                await runner.RunAsync([])
            )!;
        failure.Message.Should().NotContain("credentials-sentinel");
        docker.Commands.Should().BeEmpty();
    }

    [TestCase("version", "docker-unavailable")]
    [TestCase("image", "connect-image-unavailable")]
    [TestCase("create", "runner-container-startup")]
    [TestCase("cp", "runner-container-startup")]
    public void It_reports_specific_sanitized_prerequisites_and_cleans_failed_startup(
        string command,
        string reason
    )
    {
        var docker = new RecordingDocker(failCommand: command);
        var runner = new MessageContractRunner(docker, Image);
        MessageContractRunnerPrerequisiteException failure =
            Assert.ThrowsAsync<MessageContractRunnerPrerequisiteException>(async () =>
                await runner.RunAsync([])
            )!;
        failure.Reason.Should().Be(reason);
        failure.ToString().Should().NotContain("synthetic-secret");
        failure.InnerException.Should().BeNull();
        if (command is "create" or "cp")
        {
            docker.Commands[^1].Take(2).Should().Equal("rm", "-f");
        }
        if (command == "cp")
        {
            Directory.Exists(docker.Commands.Single(c => c[0] == "cp")[1]).Should().BeFalse();
        }
    }

    [TestCase(41, "required-class-missing")]
    [TestCase(42, "runner-classpath-incompatible")]
    [TestCase(1, "runner-classpath-incompatible")]
    public void It_distinguishes_class_loading_from_bad_runtime_and_never_reports_plugin_output(
        int exitCode,
        string reason
    )
    {
        var docker = new RecordingDocker(startExitCode: exitCode);
        var runner = new MessageContractRunner(docker, Image);
        MessageContractRunnerPrerequisiteException failure =
            Assert.ThrowsAsync<MessageContractRunnerPrerequisiteException>(async () =>
                await runner.RunAsync([])
            )!;
        failure.Reason.Should().Be(reason);
        failure.ToString().Should().NotContain("synthetic-secret");
        docker.Commands[^1].Take(2).Should().Equal("rm", "-f");
        string[] create = docker.Commands.Single(c => c[0] == "create");
        create
            .Should()
            .ContainInOrder("--network", "none", "--hostname", "localhost", "--entrypoint", "sh", Image);
        docker.Commands.Count(c => c[0] == "create").Should().Be(1);
    }

    [Test]
    public void It_cleans_up_with_an_independent_token_when_the_caller_cancels()
    {
        using var cancellation = new CancellationTokenSource();
        var docker = new RecordingDocker(cancelOnStart: cancellation);
        var runner = new MessageContractRunner(docker, Image);
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await runner.RunAsync([], cancellation.Token)
        );
        docker.Commands[^1].Take(2).Should().Equal("rm", "-f");
        docker.CleanupWasCanceled.Should().BeFalse();
        Directory.Exists(docker.Commands.Single(c => c[0] == "cp")[1]).Should().BeFalse();
    }

    [Test]
    public void It_bounds_execution_time_and_cleans_up_after_timeout()
    {
        var docker = new RecordingDocker(waitOnStart: true);
        var runner = new MessageContractRunner(docker, Image) { ExecutionTimeout = TimeSpan.FromSeconds(1) };
        MessageContractRunnerPrerequisiteException failure =
            Assert.ThrowsAsync<MessageContractRunnerPrerequisiteException>(async () =>
                await runner.RunAsync([])
            )!;
        failure.Reason.Should().Be("runner-timeout");
        docker.Commands[^1].Take(2).Should().Equal("rm", "-f");
        docker.CleanupWasCanceled.Should().BeFalse();
        Directory.Exists(docker.Commands.Single(c => c[0] == "cp")[1]).Should().BeFalse();
    }

    [Test]
    public void It_fails_visibly_when_cleanup_fails()
    {
        var docker = new RecordingDocker(failCommand: "rm", startExitCode: 41);
        MessageContractRunnerPrerequisiteException failure =
            Assert.ThrowsAsync<MessageContractRunnerPrerequisiteException>(async () =>
                await new MessageContractRunner(docker, Image).RunAsync([])
            )!;
        failure.Reason.Should().Be("runner-cleanup-failed");
        Directory.Exists(docker.Commands.Single(c => c[0] == "cp")[1]).Should().BeFalse();
    }

    private sealed class RecordingDocker(
        string failCommand = "",
        int startExitCode = 0,
        bool waitOnStart = false,
        CancellationTokenSource cancelOnStart = null!
    ) : IDockerCli
    {
        public bool IsOffline => false;
        public List<string[]> Commands { get; } = [];
        public bool CleanupWasCanceled { get; private set; }

        public async Task RequireDockerAsync(CancellationToken cancellationToken) =>
            _ = await RunAsync(["version"], cancellationToken);

        public Task<DockerCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken
        ) => RunAllowingFailureAsync(arguments, cancellationToken);

        public async Task<DockerCommandResult> RunAllowingFailureAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken
        )
        {
            Commands.Add(arguments.ToArray());
            if (arguments[0] == "rm")
            {
                CleanupWasCanceled = cancellationToken.IsCancellationRequested;
            }
            if (arguments[0] == failCommand)
            {
                throw new InvalidOperationException("synthetic-secret credentials and body");
            }
            if (arguments[0] == "start" && cancelOnStart is not null)
            {
                await cancelOnStart.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (arguments[0] == "start" && waitOnStart)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new DockerCommandResult(
                arguments[0] == "start" ? startExitCode : 0,
                "synthetic-secret stdout",
                "synthetic-secret stderr"
            );
        }
    }
}
