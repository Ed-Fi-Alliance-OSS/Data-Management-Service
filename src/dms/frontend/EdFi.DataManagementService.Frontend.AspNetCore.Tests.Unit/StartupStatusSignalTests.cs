// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
public class StartupStatusSignalTests
{
    [TestFixture]
    public class Given_A_Bootstrap_Startup_Status_Signal_With_An_Unwritable_File_Path
    {
        private StringWriter _bootstrapDiagnostics = null!;
        private string _statusDirectory = null!;
        private string _blockingFilePath = null!;
        private string _statusFilePath = null!;

        [SetUp]
        public void Setup()
        {
            _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_statusDirectory);

            _blockingFilePath = Path.Combine(_statusDirectory, "existing-file");
            File.WriteAllText(_blockingFilePath, "not a directory");

            _statusFilePath = Path.Combine(_blockingFilePath, "dms-startup-status.json");
            _bootstrapDiagnostics = new StringWriter();

            var startupStatusSignal = new FileStartupStatusSignal(_statusFilePath, _bootstrapDiagnostics);

            startupStatusSignal.WriteStarting(
                DmsStartupPhases.ConfigureServices,
                "Configuring DMS services and shared HTTP infrastructure."
            );
        }

        [TearDown]
        public void Teardown()
        {
            _bootstrapDiagnostics.Dispose();

            if (Directory.Exists(_statusDirectory))
            {
                Directory.Delete(_statusDirectory, recursive: true);
            }
        }

        [Test]
        public void It_writes_a_visible_bootstrap_diagnostic()
        {
            _bootstrapDiagnostics
                .ToString()
                .Should()
                .Contain("Unable to write DMS startup status file")
                .And.Contain(_statusFilePath)
                .And.Contain(nameof(IOException));
        }
    }

    [TestFixture]
    public class Given_No_Startup_Status_File_Path_Is_Configured
    {
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void It_falls_back_to_the_temp_directory_default(string? configuredFilePath)
        {
            FileStartupStatusSignal
                .ResolveFilePath(configuredFilePath)
                .Should()
                .Be(Path.Combine(Path.GetTempPath(), "dms-startup-status.json"));
        }
    }

    [TestFixture]
    public class Given_A_Startup_Status_File_Path_Is_Configured
    {
        [Test]
        public void It_uses_the_configured_path_unchanged()
        {
            string configuredFilePath = Path.Combine(Path.GetTempPath(), "dms-custom", "startup-state.json");

            FileStartupStatusSignal.ResolveFilePath(configuredFilePath).Should().Be(configuredFilePath);
        }
    }

    [TestFixture]
    public class Given_A_Non_Failure_Status_Is_Written
    {
        private FileStartupStatusSignal _startupStatusSignal = null!;
        private string _statusDirectory = null!;
        private string _statusFilePath = null!;

        [SetUp]
        public void Setup()
        {
            _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            _statusFilePath = Path.Combine(_statusDirectory, "dms-startup-status.json");
            _startupStatusSignal = new FileStartupStatusSignal(_statusFilePath);
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
        public void It_records_starting_without_error_details()
        {
            // Act
            _startupStatusSignal.WriteStarting(
                DmsStartupPhases.ConfigureEndpoints,
                "Configuring DMS middleware and endpoints."
            );

            // Assert
            var startupStatus = ReadStartupStatus();
            startupStatus.State.Should().Be("Starting");
            startupStatus.Phase.Should().Be(DmsStartupPhases.ConfigureEndpoints);
            startupStatus.Summary.Should().Be("Configuring DMS middleware and endpoints.");
            startupStatus.ErrorType.Should().BeNull();
            startupStatus.ErrorMessage.Should().BeNull();
        }

        [Test]
        public void It_records_completed_without_error_details()
        {
            // Act
            _startupStatusSignal.WriteCompleted(
                DmsStartupPhases.InitializeApiSchemas,
                "API schema loading and effective-schema initialization completed successfully."
            );

            // Assert
            var startupStatus = ReadStartupStatus();
            startupStatus.State.Should().Be("Completed");
            startupStatus.Phase.Should().Be(DmsStartupPhases.InitializeApiSchemas);
            startupStatus
                .Summary.Should()
                .Be("API schema loading and effective-schema initialization completed successfully.");
            startupStatus.ErrorType.Should().BeNull();
            startupStatus.ErrorMessage.Should().BeNull();
        }

        [Test]
        public void It_records_ready_as_the_ready_phase_regardless_of_the_preceding_phase()
        {
            // Arrange
            _startupStatusSignal.WriteStarting(
                DmsStartupPhases.ConfigureEndpoints,
                "Configuring DMS middleware and endpoints."
            );

            // Act
            _startupStatusSignal.WriteReady(
                "DMS startup completed successfully and HTTP endpoints are configured."
            );

            // Assert
            var startupStatus = ReadStartupStatus();
            startupStatus.State.Should().Be("Ready");
            startupStatus.Phase.Should().Be(DmsStartupPhases.Ready);
            startupStatus.ErrorType.Should().BeNull();
            startupStatus.ErrorMessage.Should().BeNull();
        }

        [Test]
        public void It_stamps_an_utc_timestamp()
        {
            // Act
            _startupStatusSignal.WriteStarting(
                DmsStartupPhases.LoadDataStores,
                "Loading data stores from Configuration Service."
            );

            // Assert
            ReadStartupStatus().UpdatedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        }

        private StartupStatusDocument ReadStartupStatus() =>
            JsonSerializer.Deserialize<StartupStatusDocument>(File.ReadAllText(_statusFilePath))!;
    }

    [TestFixture]
    public class Given_A_Failure_Status_Is_Written
    {
        private FileStartupStatusSignal _startupStatusSignal = null!;
        private string _statusDirectory = null!;
        private string _statusFilePath = null!;

        [SetUp]
        public void Setup()
        {
            _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            _statusFilePath = Path.Combine(_statusDirectory, "dms-startup-status.json");
            _startupStatusSignal = new FileStartupStatusSignal(_statusFilePath);
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
        public void It_records_the_exception_type_and_message()
        {
            // Act
            _startupStatusSignal.WriteFailed(
                DmsStartupPhases.ConfigureEndpoints,
                "Middleware and endpoint configuration failed. DMS cannot serve requests without mapped HTTP endpoints.",
                new InvalidOperationException("The route parameter name 'districtId' appears more than once.")
            );

            // Assert
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
                .Be("The route parameter name 'districtId' appears more than once.");
        }

        private StartupStatusDocument ReadStartupStatus() =>
            JsonSerializer.Deserialize<StartupStatusDocument>(File.ReadAllText(_statusFilePath))!;
    }

    [TestFixture]
    public class Given_The_Startup_Status_File_Is_Written_More_Than_Once
    {
        private FileStartupStatusSignal _startupStatusSignal = null!;
        private string _statusDirectory = null!;
        private string _statusFilePath = null!;

        [SetUp]
        public void Setup()
        {
            _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            _statusFilePath = Path.Combine(_statusDirectory, "dms-startup-status.json");
            _startupStatusSignal = new FileStartupStatusSignal(_statusFilePath);
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
        public void It_replaces_the_previous_document_rather_than_appending()
        {
            // Arrange
            _startupStatusSignal.WriteStarting(
                DmsStartupPhases.LoadDataStores,
                "Loading data stores from Configuration Service."
            );

            // Act
            _startupStatusSignal.WriteCompleted(
                DmsStartupPhases.LoadDataStores,
                "Loaded data stores from Configuration Service."
            );

            // Assert
            // The executor's snapshot/restore assumes the file holds exactly one document.
            string fileContents = File.ReadAllText(_statusFilePath);
            fileContents.Should().NotContain("Starting");

            var startupStatus = JsonSerializer.Deserialize<StartupStatusDocument>(fileContents)!;
            startupStatus.State.Should().Be("Completed");
            startupStatus.Summary.Should().Be("Loaded data stores from Configuration Service.");
        }

        [Test]
        public void It_advances_the_timestamp_without_moving_backwards()
        {
            // Act
            _startupStatusSignal.WriteStarting(
                DmsStartupPhases.LoadDataStores,
                "Loading data stores from Configuration Service."
            );
            DateTimeOffset firstWrite = ReadStartupStatus().UpdatedAtUtc;

            _startupStatusSignal.WriteCompleted(
                DmsStartupPhases.LoadDataStores,
                "Loaded data stores from Configuration Service."
            );
            DateTimeOffset secondWrite = ReadStartupStatus().UpdatedAtUtc;

            // Assert
            // Non-decreasing rather than strictly increasing: two immediate writes can land in the
            // same clock tick, which would make a strict comparison flaky.
            secondWrite.Should().BeOnOrAfter(firstWrite);
            firstWrite.Offset.Should().Be(TimeSpan.Zero);
            secondWrite.Offset.Should().Be(TimeSpan.Zero);
        }

        private StartupStatusDocument ReadStartupStatus() =>
            JsonSerializer.Deserialize<StartupStatusDocument>(File.ReadAllText(_statusFilePath))!;
    }

    [TestFixture]
    public class Given_The_Startup_Status_File_Path_Is_In_A_Missing_Nested_Directory
    {
        private string _statusDirectory = null!;
        private string _statusFilePath = null!;

        [SetUp]
        public void Setup()
        {
            _statusDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            _statusFilePath = Path.Combine(_statusDirectory, "nested", "deeper", "dms-startup-status.json");
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
        public void It_creates_the_directory_and_writes_the_document()
        {
            // Act
            new FileStartupStatusSignal(_statusFilePath).WriteStarting(
                DmsStartupPhases.ConfigureServices,
                "Configuring DMS services and shared HTTP infrastructure."
            );

            // Assert
            File.Exists(_statusFilePath).Should().BeTrue();
            JsonSerializer
                .Deserialize<StartupStatusDocument>(File.ReadAllText(_statusFilePath))!
                .State.Should()
                .Be("Starting");
        }
    }
}
