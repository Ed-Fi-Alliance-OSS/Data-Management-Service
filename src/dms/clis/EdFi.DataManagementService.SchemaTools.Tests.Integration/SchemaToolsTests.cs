// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

[TestFixture]
public class SchemaToolsTests
{
    [TestFixture]
    public class Given_No_Arguments : SchemaToolsTests
    {
        private int _exitCode;
        private string _output = null!;

        [SetUp]
        public void SetUp()
        {
            (_exitCode, _output, _) = RunCli();
        }

        [Test]
        public void It_returns_exit_code_1()
        {
            _exitCode.Should().Be(1);
        }

        [Test]
        public void It_prints_usage_information()
        {
            _output.Should().Contain("Usage: dms-schema");
        }

        [Test]
        public void It_shows_core_schema_path_argument()
        {
            _output.Should().Contain("coreSchemaPath");
        }

        private static string GetExecutablePath()
        {
            var assemblyLocation = typeof(SchemaToolsTests).Assembly.Location;
            var testBinDir = Path.GetDirectoryName(assemblyLocation)!;

            // Navigate from test bin to SchemaTools bin (same configuration)
            var schemaToolsBinDir = Path.Combine(
                testBinDir,
                "..",
                "..",
                "..",
                "..",
                "EdFi.DataManagementService.SchemaTools",
                "bin",
                "Debug",
                "net10.0"
            );

            var exePath = Path.Combine(schemaToolsBinDir, "dms-schema.exe");
            if (!File.Exists(exePath))
            {
                // Try .dll with dotnet on non-Windows
                exePath = Path.Combine(schemaToolsBinDir, "dms-schema.dll");
            }

            return Path.GetFullPath(exePath);
        }

        private static (int ExitCode, string Output, string Error) RunCli(params string[] args)
        {
            var exePath = GetExecutablePath();

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath.EndsWith(".dll") ? "dotnet" : exePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (exePath.EndsWith(".dll"))
            {
                startInfo.ArgumentList.Add(exePath);
            }

            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode, output, error);
        }
    }
}
