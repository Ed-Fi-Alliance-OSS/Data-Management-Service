// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
}
