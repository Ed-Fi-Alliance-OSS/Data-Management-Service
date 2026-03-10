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

    [TestFixture]
    public class Given_Backend_Mapping_Initialization_Fails : StartupStatusTests
    {
        private StartupPhaseExecutor _startupPhaseExecutor = null!;
        private RecordingStartupProcessExit _startupProcessExit = null!;
        private string _statusDirectory = null!;
        private string _statusFilePath = null!;

        [SetUp]
        public void Setup()
        {
            _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            _statusFilePath = Path.Combine(_statusDirectory, "dms-startup-status.json");
            _startupProcessExit = new RecordingStartupProcessExit();

            _startupPhaseExecutor = new StartupPhaseExecutor(
                new FileStartupStatusSignal(_statusFilePath),
                _startupProcessExit,
                NullLogger<StartupPhaseExecutor>.Instance
            );
        }

        [TearDown]
        public void Teardown()
        {
            if (Directory.Exists(_statusDirectory))
            {
                Directory.Delete(_statusDirectory, recursive: true);
            }
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

        private StartupStatusDocument ReadStartupStatus() =>
            JsonSerializer.Deserialize<StartupStatusDocument>(File.ReadAllText(_statusFilePath))!;
    }

    [TestFixture]
    public class Given_Backend_Mapping_Initialization_Succeeds : StartupStatusTests
    {
        private StartupPhaseExecutor _startupPhaseExecutor = null!;
        private string _statusDirectory = null!;
        private string _statusFilePath = null!;

        [SetUp]
        public void Setup()
        {
            _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            _statusFilePath = Path.Combine(_statusDirectory, "dms-startup-status.json");

            _startupPhaseExecutor = new StartupPhaseExecutor(
                new FileStartupStatusSignal(_statusFilePath),
                new RecordingStartupProcessExit(),
                NullLogger<StartupPhaseExecutor>.Instance
            );
        }

        [TearDown]
        public void Teardown()
        {
            if (Directory.Exists(_statusDirectory))
            {
                Directory.Delete(_statusDirectory, recursive: true);
            }
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

        private StartupStatusDocument ReadStartupStatus() =>
            JsonSerializer.Deserialize<StartupStatusDocument>(File.ReadAllText(_statusFilePath))!;
    }

    [TestFixture]
    public class Given_Backend_Mapping_Initialization_Is_Canceled : StartupStatusTests
    {
        private StartupPhaseExecutor _startupPhaseExecutor = null!;
        private RecordingStartupProcessExit _startupProcessExit = null!;
        private string _statusDirectory = null!;
        private string _statusFilePath = null!;

        [SetUp]
        public void Setup()
        {
            _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            _statusFilePath = Path.Combine(_statusDirectory, "dms-startup-status.json");
            _startupProcessExit = new RecordingStartupProcessExit();

            _startupPhaseExecutor = new StartupPhaseExecutor(
                new FileStartupStatusSignal(_statusFilePath),
                _startupProcessExit,
                NullLogger<StartupPhaseExecutor>.Instance
            );

            _startupPhaseExecutor.WriteCompleted(
                DmsStartupPhases.InitializeApiSchemas,
                "API schema initialization completed successfully."
            );
        }

        [TearDown]
        public void Teardown()
        {
            if (Directory.Exists(_statusDirectory))
            {
                Directory.Delete(_statusDirectory, recursive: true);
            }
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

        private StartupStatusDocument ReadStartupStatus() =>
            JsonSerializer.Deserialize<StartupStatusDocument>(File.ReadAllText(_statusFilePath))!;
    }

    [TestFixture]
    public class Given_Backend_Mapping_Initialization_Is_In_Progress : StartupStatusTests
    {
        private StartupPhaseExecutor _startupPhaseExecutor = null!;
        private string _statusDirectory = null!;
        private string _statusFilePath = null!;

        [SetUp]
        public void Setup()
        {
            _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            _statusFilePath = Path.Combine(_statusDirectory, "dms-startup-status.json");

            _startupPhaseExecutor = new StartupPhaseExecutor(
                new FileStartupStatusSignal(_statusFilePath),
                new RecordingStartupProcessExit(),
                NullLogger<StartupPhaseExecutor>.Instance
            );
        }

        [TearDown]
        public void Teardown()
        {
            if (Directory.Exists(_statusDirectory))
            {
                Directory.Delete(_statusDirectory, recursive: true);
            }
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

        private StartupStatusDocument ReadStartupStatus() =>
            JsonSerializer.Deserialize<StartupStatusDocument>(File.ReadAllText(_statusFilePath))!;
    }
}
