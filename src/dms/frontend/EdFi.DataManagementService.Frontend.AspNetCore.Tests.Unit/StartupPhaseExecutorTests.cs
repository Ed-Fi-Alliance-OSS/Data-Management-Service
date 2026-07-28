// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
public class StartupStatusTests
{
    private sealed class RecordingStartupProcessExit : IStartupProcessExit
    {
        public int ExitCallCount { get; private set; }
        public int? ExitCode { get; private set; }

        public void Exit(int exitCode)
        {
            ExitCallCount++;
            ExitCode = exitCode;
        }
    }

    /// <summary>
    /// Captures the status file exactly as it stands when the process exit is requested, which is
    /// what proves the failure was written before the exit rather than merely alongside it.
    /// </summary>
    private sealed class StatusCapturingStartupProcessExit(string statusFilePath) : IStartupProcessExit
    {
        public int ExitCallCount { get; private set; }
        public string? StatusFileContentsAtExit { get; private set; }

        public void Exit(int exitCode)
        {
            ExitCallCount++;
            StatusFileContentsAtExit = File.Exists(statusFilePath) ? File.ReadAllText(statusFilePath) : null;
        }
    }

    protected string StatusDirectory = null!;
    protected string StatusFilePath = null!;

    /// <summary>
    /// Gives each test its own status directory so concurrent fixtures cannot collide. The
    /// directory is deliberately not created: several fixtures depend on the file being absent
    /// until the signal writes it, and the signal creates the directory on demand.
    /// </summary>
    /// <remarks>
    /// Named distinctly from the fixtures' own Setup/Teardown on purpose. NUnit runs a base and a
    /// derived setup in sequence only while their names differ; a derived method with the same
    /// name hides this one and it would silently never run.
    /// </remarks>
    [SetUp]
    public void CreateStatusFilePath()
    {
        StatusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        StatusFilePath = Path.Combine(StatusDirectory, "dms-startup-status.json");
    }

    [TearDown]
    public void DeleteStatusDirectory()
    {
        if (Directory.Exists(StatusDirectory))
        {
            Directory.Delete(StatusDirectory, recursive: true);
        }
    }

    // These two are internal rather than protected because the types they expose
    // (StartupPhaseExecutor, IStartupProcessExit, StartupStatusDocument) are internal to the
    // production assembly and reach this project through InternalsVisibleTo. A protected member of
    // a public class cannot expose an internal type — CS0050/CS0051 — so widening these to
    // protected breaks the build. The derived fixtures are in this assembly, so internal is enough.
    internal StartupPhaseExecutor CreateStartupPhaseExecutor(IStartupProcessExit startupProcessExit) =>
        new(
            new FileStartupStatusSignal(StatusFilePath),
            startupProcessExit,
            NullLogger<StartupPhaseExecutor>.Instance
        );

    internal StartupStatusDocument ReadStartupStatus() =>
        JsonSerializer.Deserialize<StartupStatusDocument>(File.ReadAllText(StatusFilePath))!;

    [TestFixture]
    public class Given_Backend_Mapping_Initialization_Fails : StartupStatusTests
    {
        private StartupPhaseExecutor _startupPhaseExecutor = null!;
        private RecordingStartupProcessExit _startupProcessExit = null!;

        [SetUp]
        public void Setup()
        {
            _startupProcessExit = new RecordingStartupProcessExit();
            _startupPhaseExecutor = CreateStartupPhaseExecutor(_startupProcessExit);
        }

        [Test]
        public async Task It_writes_failed_status_before_requesting_process_exit()
        {
            // Act
            Func<Task> act = async () =>
                await _startupPhaseExecutor.RunFatalAsync(
                    DmsStartupPhases.InitializeBackendMappings,
                    "Compiling backend mappings from initialized effective schemas.",
                    "Backend mapping initialization completed successfully.",
                    "Backend mapping initialization failed. DMS cannot start without compiled backend mappings.",
                    () =>
                        throw new InvalidOperationException(
                            "Startup task 'Backend Mapping Initialization' failed: Broken schema input."
                        )
                );

            // Assert
            await act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("Startup task 'Backend Mapping Initialization' failed: Broken schema input.");
            _startupProcessExit.ExitCallCount.Should().Be(1);
            _startupProcessExit.ExitCode.Should().Be(-1);

            var startupStatus = ReadStartupStatus();
            startupStatus.State.Should().Be("Failed");
            startupStatus.Phase.Should().Be(DmsStartupPhases.InitializeBackendMappings);
            startupStatus
                .Summary.Should()
                .Be(
                    "Backend mapping initialization failed. DMS cannot start without compiled backend mappings."
                );
            startupStatus.ErrorType.Should().Be(nameof(InvalidOperationException));
            startupStatus
                .ErrorMessage.Should()
                .Be("Startup task 'Backend Mapping Initialization' failed: Broken schema input.");
        }
    }

    [TestFixture]
    public class Given_Backend_Mapping_Initialization_Succeeds : StartupStatusTests
    {
        private StartupPhaseExecutor _startupPhaseExecutor = null!;

        [SetUp]
        public void Setup()
        {
            _startupPhaseExecutor = CreateStartupPhaseExecutor(new RecordingStartupProcessExit());
        }

        [Test]
        public async Task It_writes_completed_status_for_backend_mapping_before_ready()
        {
            // Act
            await _startupPhaseExecutor.RunFatalAsync(
                DmsStartupPhases.InitializeBackendMappings,
                "Compiling backend mappings from initialized effective schemas.",
                "Backend mapping initialization completed successfully.",
                "Backend mapping initialization failed. DMS cannot start without compiled backend mappings.",
                () => Task.CompletedTask
            );

            // Assert
            var startupStatus = ReadStartupStatus();
            startupStatus.State.Should().Be("Completed");
            startupStatus.Phase.Should().Be(DmsStartupPhases.InitializeBackendMappings);
            startupStatus.Summary.Should().Be("Backend mapping initialization completed successfully.");
            startupStatus.ErrorType.Should().BeNull();
            startupStatus.ErrorMessage.Should().BeNull();
        }

        [Test]
        public async Task It_writes_ready_status_after_the_startup_sequence_is_marked_complete()
        {
            // Act
            await _startupPhaseExecutor.RunFatalAsync(
                DmsStartupPhases.InitializeBackendMappings,
                "Compiling backend mappings from initialized effective schemas.",
                "Backend mapping initialization completed successfully.",
                "Backend mapping initialization failed. DMS cannot start without compiled backend mappings.",
                () => Task.CompletedTask
            );
            _startupPhaseExecutor.WriteReady(
                "DMS startup completed successfully and HTTP endpoints are configured."
            );

            // Assert
            var startupStatus = ReadStartupStatus();
            startupStatus.State.Should().Be("Ready");
            startupStatus.Phase.Should().Be(DmsStartupPhases.Ready);
            startupStatus
                .Summary.Should()
                .Be("DMS startup completed successfully and HTTP endpoints are configured.");
            startupStatus.ErrorType.Should().BeNull();
            startupStatus.ErrorMessage.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_Backend_Mapping_Initialization_Is_Canceled : StartupStatusTests
    {
        private StartupPhaseExecutor _startupPhaseExecutor = null!;
        private RecordingStartupProcessExit _startupProcessExit = null!;

        [SetUp]
        public void Setup()
        {
            _startupProcessExit = new RecordingStartupProcessExit();
            _startupPhaseExecutor = CreateStartupPhaseExecutor(_startupProcessExit);

            _startupPhaseExecutor.WriteCompleted(
                DmsStartupPhases.InitializeApiSchemas,
                "API schema initialization completed successfully."
            );
        }

        [Test]
        public async Task It_preserves_the_previous_non_failure_status_and_does_not_request_exit()
        {
            // Act
            Func<Task> act = async () =>
                await _startupPhaseExecutor.RunFatalAsync(
                    DmsStartupPhases.InitializeBackendMappings,
                    "Compiling backend mappings from initialized effective schemas.",
                    "Backend mapping initialization completed successfully.",
                    "Backend mapping initialization failed. DMS cannot start without compiled backend mappings.",
                    () => throw new OperationCanceledException("Startup canceled.")
                );

            // Assert
            await act.Should().ThrowAsync<OperationCanceledException>().WithMessage("Startup canceled.");
            _startupProcessExit.ExitCallCount.Should().Be(0);
            _startupProcessExit.ExitCode.Should().BeNull();

            var startupStatus = ReadStartupStatus();
            startupStatus.State.Should().Be("Completed");
            startupStatus.Phase.Should().Be(DmsStartupPhases.InitializeApiSchemas);
            startupStatus.Summary.Should().Be("API schema initialization completed successfully.");
            startupStatus.ErrorType.Should().BeNull();
            startupStatus.ErrorMessage.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_Backend_Mapping_Initialization_Is_In_Progress : StartupStatusTests
    {
        private StartupPhaseExecutor _startupPhaseExecutor = null!;

        [SetUp]
        public void Setup()
        {
            _startupPhaseExecutor = CreateStartupPhaseExecutor(new RecordingStartupProcessExit());
        }

        [Test]
        public async Task It_writes_the_configured_starting_summary_while_the_phase_is_running()
        {
            // Arrange
            var phaseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            // Act
            Task runTask = _startupPhaseExecutor.RunFatalAsync(
                DmsStartupPhases.InitializeBackendMappings,
                "Compiling backend mappings from initialized effective schemas.",
                "Backend mapping initialization completed successfully.",
                "Backend mapping initialization failed. DMS cannot start without compiled backend mappings.",
                async () =>
                {
                    phaseStarted.SetResult();
                    await allowCompletion.Task;
                }
            );

            await phaseStarted.Task;

            // Assert
            var startupStatus = ReadStartupStatus();
            startupStatus.State.Should().Be("Starting");
            startupStatus.Phase.Should().Be(DmsStartupPhases.InitializeBackendMappings);
            startupStatus
                .Summary.Should()
                .Be("Compiling backend mappings from initialized effective schemas.");
            startupStatus.ErrorType.Should().BeNull();
            startupStatus.ErrorMessage.Should().BeNull();

            allowCompletion.SetResult();
            await runTask;
        }
    }

    [TestFixture]
    public class Given_Auth_Metadata_Initialization_Fails : StartupStatusTests
    {
        private StartupPhaseExecutor _startupPhaseExecutor = null!;
        private RecordingStartupProcessExit _startupProcessExit = null!;

        [SetUp]
        public void Setup()
        {
            _startupProcessExit = new RecordingStartupProcessExit();
            _startupPhaseExecutor = CreateStartupPhaseExecutor(_startupProcessExit);
        }

        [Test]
        public async Task It_requests_the_phase_specific_exit_code_rather_than_the_default()
        {
            // Act
            // Authentication metadata is the one phase that overrides the default exit code, so
            // this is the only coverage that the exitCode argument is honored at all.
            Func<Task> act = async () =>
                await _startupPhaseExecutor.RunFatalAsync(
                    DmsStartupPhases.InitializeAuthMetadata,
                    "Initializing authentication metadata caches (OIDC warm-up and claim sets).",
                    "Authentication metadata initialization completed successfully.",
                    "Authentication metadata initialization failed. JWT authentication will not function correctly.",
                    () => throw new InvalidOperationException("OIDC metadata endpoint unreachable."),
                    exitCode: 1
                );

            // Assert
            await act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("OIDC metadata endpoint unreachable.");
            _startupProcessExit.ExitCallCount.Should().Be(1);
            _startupProcessExit.ExitCode.Should().Be(1);

            var startupStatus = ReadStartupStatus();
            startupStatus.State.Should().Be("Failed");
            startupStatus.Phase.Should().Be(DmsStartupPhases.InitializeAuthMetadata);
            startupStatus.ErrorType.Should().Be(nameof(InvalidOperationException));
        }
    }

    [TestFixture]
    public class Given_A_Phase_Is_Canceled_With_No_Preceding_Status_File : StartupStatusTests
    {
        private StartupPhaseExecutor _startupPhaseExecutor = null!;
        private RecordingStartupProcessExit _startupProcessExit = null!;

        [SetUp]
        public void Setup()
        {
            _startupProcessExit = new RecordingStartupProcessExit();

            // Deliberately no status write here: the snapshot must capture a non-existent file so
            // the restore path exercises removal rather than rewrite.
            _startupPhaseExecutor = CreateStartupPhaseExecutor(_startupProcessExit);
        }

        [Test]
        public async Task It_removes_the_status_file_it_created_and_does_not_request_exit()
        {
            // Act
            Func<Task> act = async () =>
                await _startupPhaseExecutor.RunFatalAsync(
                    DmsStartupPhases.LoadDataStores,
                    "Loading data stores from Configuration Service.",
                    "Loaded data stores from Configuration Service.",
                    "Unable to load data stores from Configuration Service.",
                    () => throw new OperationCanceledException("Startup canceled.")
                );

            // Assert
            await act.Should().ThrowAsync<OperationCanceledException>().WithMessage("Startup canceled.");
            _startupProcessExit.ExitCallCount.Should().Be(0);

            // Cancellation must leave no trace: a stranded Starting document would be read as a
            // hung phase by anyone collecting the file.
            File.Exists(StatusFilePath).Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_A_Fatal_Phase_Failure_Is_Handled : StartupStatusTests
    {
        private StartupPhaseExecutor _startupPhaseExecutor = null!;
        private StatusCapturingStartupProcessExit _startupProcessExit = null!;

        [SetUp]
        public void Setup()
        {
            _startupProcessExit = new StatusCapturingStartupProcessExit(StatusFilePath);
            _startupPhaseExecutor = CreateStartupPhaseExecutor(_startupProcessExit);
        }

        [Test]
        public async Task It_writes_the_failure_before_requesting_exit()
        {
            // Act
            Func<Task> act = async () =>
                await _startupPhaseExecutor.RunFatalAsync(
                    DmsStartupPhases.InitializeApiSchemas,
                    "Loading API schemas and initializing effective schema metadata.",
                    "API schema loading and effective-schema initialization completed successfully.",
                    "API schema initialization failed. DMS cannot start with invalid schemas.",
                    () => throw new InvalidOperationException("Broken schema input.")
                );

            // Assert
            await act.Should().ThrowAsync<InvalidOperationException>();
            _startupProcessExit.ExitCallCount.Should().Be(1);

            // In production the exit terminates the process, so anything written after it would
            // never reach disk. Asserting on the snapshot taken during Exit proves the ordering.
            _startupProcessExit.StatusFileContentsAtExit.Should().NotBeNull();

            var startupStatusAtExit = JsonSerializer.Deserialize<StartupStatusDocument>(
                _startupProcessExit.StatusFileContentsAtExit!
            )!;
            startupStatusAtExit.State.Should().Be("Failed");
            startupStatusAtExit.Phase.Should().Be(DmsStartupPhases.InitializeApiSchemas);
            startupStatusAtExit
                .Summary.Should()
                .Be("API schema initialization failed. DMS cannot start with invalid schemas.");
            startupStatusAtExit.ErrorType.Should().Be(nameof(InvalidOperationException));
            startupStatusAtExit.ErrorMessage.Should().Be("Broken schema input.");
        }
    }

    [TestFixture]
    public class Given_A_Fatal_Failure_Is_Recorded_Outside_The_Executor : StartupStatusTests
    {
        private StartupPhaseExecutor _startupPhaseExecutor = null!;
        private RecordingStartupProcessExit _startupProcessExit = null!;

        [SetUp]
        public void Setup()
        {
            _startupProcessExit = new RecordingStartupProcessExit();
            _startupPhaseExecutor = CreateStartupPhaseExecutor(_startupProcessExit);
        }

        [Test]
        public void It_writes_failed_status_and_leaves_termination_to_the_caller()
        {
            // Act
            // ConfigureEndpoints uses this instead of RunFatalAsync because it terminates by
            // rethrow. Not exiting is the contract Program.cs depends on: an exit here would kill
            // the process before the rethrow could carry the exception to the runtime.
            _startupPhaseExecutor.WriteFatalFailure(
                DmsStartupPhases.ConfigureEndpoints,
                "Middleware and endpoint configuration failed. DMS cannot serve requests without mapped HTTP endpoints.",
                new InvalidOperationException(
                    "The route parameter name 'districtId' appears more than one time in the route template."
                )
            );

            // Assert
            _startupProcessExit.ExitCallCount.Should().Be(0);
            _startupProcessExit.ExitCode.Should().BeNull();

            var startupStatus = ReadStartupStatus();
            startupStatus.State.Should().Be("Failed");
            startupStatus.Phase.Should().Be(DmsStartupPhases.ConfigureEndpoints);
            startupStatus
                .Summary.Should()
                .Be(
                    "Middleware and endpoint configuration failed. DMS cannot serve requests without mapped HTTP endpoints."
                );
            startupStatus.ErrorType.Should().Be(nameof(InvalidOperationException));
            startupStatus
                .ErrorMessage.Should()
                .Be(
                    "The route parameter name 'districtId' appears more than one time in the route template."
                );
        }
    }
}
