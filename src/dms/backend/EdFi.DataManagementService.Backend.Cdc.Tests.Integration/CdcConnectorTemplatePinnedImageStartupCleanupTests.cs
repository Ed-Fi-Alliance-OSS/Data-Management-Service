// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
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

    [TestCase("cancellation")]
    [TestCase("assertion")]
    [TestCase("failure")]
    public void It_preserves_the_startup_failure_and_cleans_up_when_evidence_cannot_be_written(
        string scenario
    )
    {
        Exception startupException = scenario switch
        {
            "cancellation" => new OperationCanceledException("startup canceled"),
            "assertion" => new AssertionException("startup rejected"),
            _ => new InvalidOperationException("startup failed"),
        };
        var docker = new RecordingDockerCli(arguments => IsPortCommand(arguments) ? startupException : null);
        bool evidenceAttempted = false;
        Exception actual = Assert.CatchAsync(async () =>
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                CdcProvider.Postgresql,
                BuildSettings(),
                docker,
                ResourcePrefix,
                CancellationToken.None,
                applyPrerequisitePolicy: scenario != "failure",
                writeStartupFailureEvidence: (_, _) =>
                {
                    evidenceAttempted = true;
                    return Task.FromException(new IOException("evidence destination unavailable"));
                }
            )
        )!;

        evidenceAttempted.Should().BeTrue();
        actual.Should().BeSameAs(startupException);
        AssertCleanupCommandsWereRun(docker);
    }

    [TestCase(1, "SQL Server Agent has not finished startup.", "", "AgentStarting")]
    [TestCase(1, "", "SQL Server configuration has not settled.", "ConfigurationPending")]
    [TestCase(1, "", "", "SqlCommandFailed")]
    [TestCase(125, "", "", "SqlCommandFailed")]
    [TestCase(1, "", "Container private-name is not running", "ContainerNotRunning")]
    [TestCase(1, "", "Login failed for user private-user", "LoginFailed")]
    [TestCase(1, "", "Login timeout expired", "ConnectionTimeout")]
    [TestCase(1, "", "TCP Provider: private-connection-details", "ConnectionFailed")]
    [TestCase(127, "", "", "SqlCommandMissing")]
    [NonParallelizable]
    public async Task It_preserves_the_last_sql_readiness_state_on_cancellation_without_raw_output(
        int exitCode,
        string standardOutput,
        string standardError,
        string expectedState
    )
    {
        string directory = TestContext.CurrentContext.WorkDirectory;
        const string pattern = "admission-evidence-startup-*.json";
        string[] before = Directory.GetFiles(directory, pattern);
        const string privateDetails = "private-sql-command-output-and-credentials";
        using var cancellation = new CancellationTokenSource();
        var docker = new RecordingDockerCli(_ => null)
        {
            ProviderProbe = _ =>
            {
                cancellation.Cancel();
                return new(exitCode, standardOutput + privateDetails, standardError + privateDetails);
            },
        };

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                CdcProvider.SqlServer,
                BuildSettings(),
                docker,
                ResourcePrefix,
                cancellation.Token,
                applyPrerequisitePolicy: false
            )
        );

        AssertCleanupCommandsWereRun(docker);
        string path = Directory.GetFiles(directory, pattern).Except(before).Should().ContainSingle().Subject;
        string text = await File.ReadAllTextAsync(path);
        text.Should().NotContain(privateDetails).And.NotContain(ResourcePrefix);
        using var evidence = JsonDocument.Parse(text);
        JsonElement readiness = evidence.RootElement.GetProperty("SqlServerReadiness");
        readiness.GetProperty("Attempts").GetInt32().Should().Be(1);
        readiness.GetProperty("LastExitCode").GetInt32().Should().Be(exitCode);
        readiness.GetProperty("State").GetString().Should().Be(expectedState);
    }

    [TestCase("exited")]
    [TestCase("running")]
    [TestCase("malformed")]
    [TestCase("unknown-status")]
    [TestCase("failure")]
    [TestCase("cancellation")]
    [NonParallelizable]
    public async Task It_records_bounded_container_state_before_cleanup_despite_startup_cancellation(
        string scenario
    )
    {
        string directory = TestContext.CurrentContext.WorkDirectory;
        const string pattern = "admission-evidence-startup-*.json";
        string[] before = Directory.GetFiles(directory, pattern);
        const string privateDetails = "private-inspect-error-and-configuration";
        using var cancellation = new CancellationTokenSource();
        bool inspected = false;
        var docker = new RecordingDockerCli(_ => null)
        {
            ProviderProbe = _ =>
            {
                cancellation.Cancel();
                return new(1, string.Empty, privateDetails);
            },
            ProviderInspect = token =>
            {
                inspected = true;
                token.IsCancellationRequested.Should().BeFalse();
                if (scenario == "cancellation")
                {
                    throw new OperationCanceledException(privateDetails);
                }
                string status = scenario == "unknown-status" ? privateDetails : scenario;
                int exitCode = scenario == "exited" ? 137 : 0;
                string output =
                    scenario == "malformed"
                        ? privateDetails
                        : JsonSerializer.Serialize(
                            new
                            {
                                Status = status,
                                ExitCode = exitCode,
                                OOMKilled = scenario == "exited",
                                Error = privateDetails,
                                Configuration = privateDetails,
                            }
                        );
                return Task.FromResult(
                    new DockerCommandResult(scenario == "failure" ? 1 : 0, output, privateDetails)
                );
            },
        };

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                CdcProvider.SqlServer,
                BuildSettings(),
                docker,
                ResourcePrefix,
                cancellation.Token,
                applyPrerequisitePolicy: false
            )
        );

        inspected.Should().BeTrue();
        AssertCleanupCommandsWereRun(docker);
        docker
            .Commands.ToList()
            .FindIndex(c => c.StartsWith("allow inspect", StringComparison.Ordinal))
            .Should()
            .BeLessThan(
                docker.Commands.ToList().FindIndex(c => c.StartsWith("allow rm", StringComparison.Ordinal))
            );
        string path = Directory.GetFiles(directory, pattern).Except(before).Should().ContainSingle().Subject;
        string text = await File.ReadAllTextAsync(path);
        text.Should().NotContain(privateDetails).And.NotContain(ResourcePrefix);
        using var evidence = JsonDocument.Parse(text);
        var state = evidence.RootElement.GetProperty("SqlServerReadiness").GetProperty("Container");
        state.EnumerateObject().Should().HaveCount(4);
        state
            .GetProperty("Status")
            .GetString()
            .Should()
            .Be(scenario is "exited" or "running" ? scenario : "Unavailable");
        state.GetProperty("ExitCode").GetInt32().Should().Be(scenario == "exited" ? 137 : 0);
        state.GetProperty("OomKilled").GetBoolean().Should().Be(scenario == "exited");
    }

    [TestCase("captured")]
    [TestCase("failure")]
    [TestCase("cancellation")]
    [NonParallelizable]
    public async Task It_retains_exit_state_and_cleanup_when_startup_log_collection_fails(string scenario)
    {
        const string pattern = "admission-evidence-startup-*.json";
        string directory = TestContext.CurrentContext.WorkDirectory;
        string[] before = Directory.GetFiles(directory, pattern);
        const string secret = "private-log-host-path-password";
        using var cancellation = new CancellationTokenSource();
        CancellationToken inspectToken = default;
        int logCalls = 0;
        var docker = new RecordingDockerCli(_ => null)
        {
            ProviderProbe = _ =>
            {
                cancellation.Cancel();
                return new(1, string.Empty, secret);
            },
            ProviderInspect = token =>
            {
                inspectToken = token;
                return Task.FromResult(
                    new DockerCommandResult(
                        0,
                        "{\"Status\":\"exited\",\"ExitCode\":1,\"OOMKilled\":false}",
                        string.Empty
                    )
                );
            },
            ProviderLogs = token =>
            {
                logCalls++;
                token.Should().Be(inspectToken, "logs share the existing inspection deadline");
                token.IsCancellationRequested.Should().BeFalse();
                if (scenario == "cancellation")
                {
                    throw new OperationCanceledException(secret);
                }
                return Task.FromResult(
                    new DockerCommandResult(
                        scenario == "failure" ? 1 : 0,
                        "Error: 701, Severity: 17, State: 1. " + secret,
                        secret
                    )
                );
            },
        };
        var exception = Assert.CatchAsync<OperationCanceledException>(async () =>
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                CdcProvider.SqlServer,
                BuildSettings(),
                docker,
                ResourcePrefix,
                cancellation.Token,
                applyPrerequisitePolicy: false
            )
        );
        exception!.CancellationToken.Should().Be(cancellation.Token);
        logCalls.Should().Be(1);
        AssertCleanupCommandsWereRun(docker);
        docker
            .Commands.ToList()
            .FindIndex(c => c.StartsWith("allow logs --tail 400", StringComparison.Ordinal))
            .Should()
            .BeLessThan(
                docker.Commands.ToList().FindIndex(c => c.StartsWith("allow rm", StringComparison.Ordinal))
            );
        string path = Directory.GetFiles(directory, pattern).Except(before).Should().ContainSingle().Subject;
        string text = await File.ReadAllTextAsync(path);
        text.Should().NotContain(secret).And.NotContain(ResourcePrefix);
        using var evidence = JsonDocument.Parse(text);
        var container = evidence.RootElement.GetProperty("SqlServerReadiness").GetProperty("Container");
        container.GetProperty("Status").GetString().Should().Be("exited");
        container.GetProperty("ExitCode").GetInt32().Should().Be(1);
        container.GetProperty("OomKilled").GetBoolean().Should().BeFalse();
        var logs = container.GetProperty("Logs");
        logs.GetProperty("State")
            .GetString()
            .Should()
            .Be(scenario == "captured" ? "Observed" : "Unavailable");
        int[] expectedCodes = scenario == "captured" ? [701] : [];
        logs.GetProperty("SqlErrorNumbers")
            .EnumerateArray()
            .Select(n => n.GetInt32())
            .Should()
            .Equal(expectedCodes);
    }

    [TestCase(false)]
    [TestCase(true)]
    [NonParallelizable]
    public async Task It_records_compose_startup_locations_without_exception_details_and_still_cleans_up(
        bool wrapped
    )
    {
        string directory = TestContext.CurrentContext.WorkDirectory;
        const string pattern = "admission-evidence-startup-*.json";
        string[] before = Directory.GetFiles(directory, pattern);
        const string privateDetails = "private-docker-command-password-and-output";
        Exception startupException;
        try
        {
            throw new InvalidOperationException(privateDetails, new IOException(privateDetails));
        }
        catch (InvalidOperationException cause)
        {
            startupException = wrapped ? new AssertionException(privateDetails, cause) : cause;
        }
        var docker = new RecordingDockerCli(arguments => arguments[0] == "network" ? startupException : null);
        var exception = Assert.CatchAsync(async () =>
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                CdcProvider.Postgresql,
                BuildSettings(),
                docker,
                ResourcePrefix,
                CancellationToken.None,
                applyPrerequisitePolicy: false,
                composeKafka: true
            )
        );
        exception.Should().BeSameAs(startupException);
        AssertCleanupCommandsWereRun(docker);
        string path = Directory.GetFiles(directory, pattern).Except(before).Should().ContainSingle().Subject;
        string text = await File.ReadAllTextAsync(path);
        text.Should().NotContain(privateDetails);
        text.Should().NotContain(ResourcePrefix);
        using var evidence = JsonDocument.Parse(text);
        evidence.RootElement.GetProperty("Stage").GetString().Should().Be("start-docker-resources");
        evidence
            .RootElement.GetProperty("ExceptionType")
            .GetString()
            .Should()
            .Be(wrapped ? "AssertionException" : "InvalidOperationException");
        evidence
            .RootElement.GetProperty("CauseExceptionType")
            .GetString()
            .Should()
            .Be("InvalidOperationException");
        evidence.RootElement.GetProperty("SqlServerErrorNumbers").GetArrayLength().Should().Be(0);
        evidence.RootElement.GetProperty("Locations").GetArrayLength().Should().BeGreaterThan(0);
        evidence.RootElement.TryGetProperty("Message", out _).Should().BeFalse();
    }

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

    [Test]
    public void It_cleans_up_when_the_pre_worker_controller_phase_rejects_before_launch()
    {
        var docker = new RecordingDockerCli(_ => null);
        var rejected = new AssertionException("controller rejected");
        var exception = Assert.ThrowsAsync<AssertionException>(async () =>
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                CdcProvider.Postgresql,
                BuildSettings(),
                docker,
                ResourcePrefix,
                CancellationToken.None,
                beforeWorker: (_, _) => Task.FromException(rejected)
            )
        );
        exception.Should().BeSameAs(rejected);
        docker
            .Commands.Should()
            .NotContain(command =>
                command.StartsWith("run run", StringComparison.Ordinal)
                && command.Contains(ResourcePrefix + "-connect", StringComparison.Ordinal)
            );
        AssertCleanupCommandsWereRun(docker);
    }

    [Test]
    public void It_attempts_every_owned_cleanup_after_one_cleanup_command_fails()
    {
        var canceled = new OperationCanceledException("startup canceled");
        var docker = new RecordingDockerCli(
            arguments => IsPortCommand(arguments) ? canceled : null,
            failFirstCleanup: true
        );
        var exception = Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                CdcProvider.Postgresql,
                BuildSettings(),
                docker,
                ResourcePrefix,
                CancellationToken.None
            )
        );
        exception.Should().BeSameAs(canceled);
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
            $"allow rm -f -v {ResourcePrefix}-connect",
            $"allow rm -f -v {ResourcePrefix}-provider",
            $"allow rm -f -v {ResourcePrefix}-broker",
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
        string mappedPortOutput = "127.0.0.1:32768\n",
        bool failFirstCleanup = false
    ) : IDockerCli
    {
        private readonly List<string> _commands = [];

        public IReadOnlyList<string> Commands => _commands;

        public Func<IReadOnlyList<string>, DockerCommandResult> ProviderProbe { get; init; } =
            _ => new(0, string.Empty, string.Empty);

        public Func<CancellationToken, Task<DockerCommandResult>> ProviderInspect { get; init; } =
            _ => Task.FromResult(new DockerCommandResult(0, string.Empty, string.Empty));

        public Func<CancellationToken, Task<DockerCommandResult>> ProviderLogs { get; init; } =
            _ => Task.FromResult(new DockerCommandResult(0, string.Empty, string.Empty));

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
            if (failFirstCleanup && arguments.Contains(ResourcePrefix + "-connect") && arguments[0] == "rm")
            {
                throw new IOException("sentinel-cleanup-secret");
            }
            if (arguments[0] == "exec" && arguments.Contains(ResourcePrefix + "-provider"))
            {
                return Task.FromResult(ProviderProbe(arguments));
            }
            if (arguments[0] == "inspect" && arguments.Contains(ResourcePrefix + "-provider"))
            {
                return ProviderInspect(cancellationToken);
            }
            if (arguments[0] == "logs" && arguments.Contains(ResourcePrefix + "-provider"))
            {
                return ProviderLogs(cancellationToken);
            }
            return Task.FromResult(ResultFor(arguments));
        }

        private DockerCommandResult ResultFor(IReadOnlyList<string> arguments) =>
            IsPortCommand(arguments)
                ? new DockerCommandResult(0, mappedPortOutput, string.Empty)
                : new DockerCommandResult(0, string.Empty, string.Empty);

        private static string CommandText(IReadOnlyList<string> arguments) => string.Join(" ", arguments);
    }
}

[TestFixture("success", false, false, false)]
[TestFixture("absent", false, false, false)]
[TestFixture("failure", false, false, false)]
[TestFixture("cancellation", false, false, false)]
[TestFixture("failure", true, false, false)]
[TestFixture("cancellation", true, false, false)]
[TestFixture("failure", false, true, false)]
[TestFixture("failure", false, false, true)]
[Parallelizable]
public sealed class Given_PinnedImageFixtureComposeCleanup(
    string composeResult,
    bool failResourceCleanup,
    bool keepContainers,
    bool offline
)
{
    private string _resourcePrefix = null!;
    private string _directory = null!;
    private RecordingComposeDockerCli _docker = null!;
    private CdcConnectorTemplatePinnedImageFixture _fixture = null!;
    private Exception _failure = null!;

    [SetUp]
    public async Task Setup()
    {
        _resourcePrefix = $"dms-cdc-cleanup-test-{Guid.NewGuid():N}";
        _directory = Path.Combine(Path.GetTempPath(), _resourcePrefix + "-compose");
        _docker = new(_resourcePrefix, composeResult, failResourceCleanup, offline);
        _fixture = await CdcConnectorTemplatePinnedImageFixture.StartAsync(
            CdcProvider.Postgresql,
            new("connect@sha256:qualified", "broker:qualified", "postgres:qualified", true, keepContainers),
            _docker,
            _resourcePrefix,
            CancellationToken.None,
            exposeBroker: true,
            composeKafka: true,
            offlineKafka: true
        );
        await _fixture.AssertComposeDataMountAsync(CancellationToken.None);
        _docker.Commands.Clear();
        _failure = null!;
        try
        {
            await _fixture.DisposeAsync();
        }
        catch (Exception exception)
        {
            _failure = exception;
        }
    }

    [TearDown]
    public void Teardown()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public void It_attempts_only_the_exact_owned_resources_unless_cleanup_is_disabled()
    {
        _docker
            .Commands.Should()
            .Equal(
                keepContainers || offline
                    ? []
                    : new[]
                    {
                        $"compose -f {_fixture.ControllerComposeFile} --env-file {_fixture.ControllerComposeEnvironment} -p {_resourcePrefix} down --volumes --remove-orphans",
                        $"rm -f -v {_resourcePrefix}-connect",
                        $"rm -f -v {_resourcePrefix}-provider",
                        $"rm -f -v {_resourcePrefix}-broker",
                        $"network rm {_resourcePrefix}-network",
                        $"volume rm {_resourcePrefix}_kafka-data",
                        $"volume rm {_resourcePrefix}-anonymous",
                    }
            );
    }

    [Test]
    public void It_reports_accumulated_failures_without_raw_output_or_secrets()
    {
        if (keepContainers || offline || composeResult is "success" or "absent")
        {
            _failure.Should().BeNull();
            return;
        }
        _failure.Should().BeOfType<InvalidOperationException>();
        _failure
            .Message.Should()
            .Be(
                $"CDC fixture cleanup failed for {(failResourceCleanup ? 3 : 1)} isolated resources. Details redacted."
            );
        _failure.InnerException.Should().BeNull();
    }

    [Test]
    public void It_removes_the_temporary_directory_unless_cleanup_is_disabled() =>
        Directory.Exists(_directory).Should().Be(keepContainers || offline);

    [Test]
    public async Task It_does_not_repeat_cleanup_on_later_disposal()
    {
        string[] commands = [.. _docker.Commands];
        await _fixture.DisposeAsync();
        _docker.Commands.Should().Equal(commands);
    }

    private sealed class RecordingComposeDockerCli(
        string prefix,
        string composeResult,
        bool failResourceCleanup,
        bool offline
    ) : IDockerCli
    {
        public List<string> Commands { get; } = [];
        public bool IsOffline => offline;

        public Task RequireDockerAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DockerCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken
        )
        {
            Commands.Add(string.Join(" ", arguments));
            if (arguments[0] == "compose" && arguments.Contains("down"))
            {
                cancellationToken.CanBeCanceled.Should().BeTrue();
                if (composeResult == "failure")
                {
                    throw new InvalidOperationException("sentinel-compose-secret");
                }
                if (composeResult == "cancellation")
                {
                    return Task.FromCanceled<DockerCommandResult>(new CancellationToken(canceled: true));
                }
            }
            string output = arguments[0] switch
            {
                "compose" when arguments.Contains("config") =>
                    """{"services":{"kafka":{"volumes":[{"source":"kafka-data","target":"/tmp/kraft-combined-logs"}]}}}""",
                "inspect" =>
                    $$"""[{"Mounts":[{"Type":"volume","Name":"{{prefix}}_kafka-data","Destination":"/tmp/kraft-combined-logs"}]},{"Mounts":[{"Type":"volume","Name":"{{prefix}}-anonymous","Destination":"/other"},{"Type":"bind","Name":"unowned-bind","Destination":"/bind"}]}]""",
                "exec" when arguments.Contains("/opt/kafka/config/server.properties") =>
                    "log.dirs=/tmp/kraft-combined-logs",
                "exec" when arguments.Contains("/proc/1/status") => "Uid:\t1000\n",
                _ => "",
            };
            return Task.FromResult(new DockerCommandResult(0, output, ""));
        }

        public Task<DockerCommandResult> RunAllowingFailureAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken
        )
        {
            Commands.Add(string.Join(" ", arguments));
            cancellationToken.ThrowIfCancellationRequested();
            if (arguments[0] != "exec")
            {
                cancellationToken.CanBeCanceled.Should().BeTrue();
                if (failResourceCleanup && arguments[0] == "network")
                {
                    throw new IOException("sentinel-network-secret");
                }
                if (failResourceCleanup && arguments.Contains(prefix + "-anonymous"))
                {
                    return Task.FromResult(
                        new DockerCommandResult(1, "sentinel-stdout", "sentinel-volume-secret")
                    );
                }
                if (composeResult == "absent")
                {
                    return Task.FromResult(
                        new DockerCommandResult(
                            1,
                            "",
                            arguments[0] == "volume" ? "not found" : "No such resource"
                        )
                    );
                }
            }
            return Task.FromResult(new DockerCommandResult(0, "", ""));
        }
    }
}
