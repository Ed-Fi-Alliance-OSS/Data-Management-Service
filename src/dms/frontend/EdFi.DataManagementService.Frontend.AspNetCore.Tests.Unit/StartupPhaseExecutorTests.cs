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
public class StartupPhaseExecutorTests
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
    public class Given_A_Fatal_Startup_Phase_That_Fails : StartupPhaseExecutorTests
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
                    DmsStartupPhases.InitializeApiSchemas,
                    "API schema initialization completed successfully.",
                    "API schema initialization failed. DMS cannot start with invalid schemas.",
                    () => throw new InvalidOperationException("Broken schema input.")
                );

            // Assert
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Broken schema input.");
            _startupProcessExit.ExitCallCount.Should().Be(1);
            _startupProcessExit.ExitCode.Should().Be(-1);

            var startupStatus = ReadStartupStatus();
            startupStatus.State.Should().Be("Failed");
            startupStatus.Phase.Should().Be(DmsStartupPhases.InitializeApiSchemas);
            startupStatus
                .Summary.Should()
                .Be("API schema initialization failed. DMS cannot start with invalid schemas.");
            startupStatus.ErrorType.Should().Be(nameof(InvalidOperationException));
            startupStatus.ErrorMessage.Should().Be("Broken schema input.");
        }

        private StartupStatusDocument ReadStartupStatus() =>
            JsonSerializer.Deserialize<StartupStatusDocument>(File.ReadAllText(_statusFilePath))!;
    }

    [TestFixture]
    public class Given_A_Fatal_Startup_Phase_That_Succeeds : StartupPhaseExecutorTests
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
        public async Task It_writes_ready_status_after_the_startup_sequence_is_marked_complete()
        {
            // Act
            await _startupPhaseExecutor.RunFatalAsync(
                DmsStartupPhases.LoadDmsInstances,
                "Loaded DMS instances from Configuration Service.",
                "Unable to load DMS instances from Configuration Service. DMS cannot start without proper instance configuration.",
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
}
