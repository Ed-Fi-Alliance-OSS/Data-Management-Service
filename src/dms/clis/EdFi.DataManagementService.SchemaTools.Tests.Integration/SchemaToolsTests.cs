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
    private static string GetExecutablePath()
    {
        var assemblyLocation = typeof(SchemaToolsTests).Assembly.Location;
        var testBinDir = Path.GetDirectoryName(assemblyLocation)!;

        // Extract configuration and framework from current test assembly path
        // Path structure: .../bin/{Configuration}/{Framework}/
        var frameworkDir = new DirectoryInfo(testBinDir);
        var configurationDir = frameworkDir.Parent!;
        var framework = frameworkDir.Name;
        var configuration = configurationDir.Name;

        // Navigate from test bin to SchemaTools bin (same configuration)
        var schemaToolsBinDir = Path.Combine(
            testBinDir,
            "..",
            "..",
            "..",
            "..",
            "EdFi.DataManagementService.SchemaTools",
            "bin",
            configuration,
            framework
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

    private static string GetAuthoritativeFixturePath()
    {
        var assemblyLocation = typeof(SchemaToolsTests).Assembly.Location;
        var testBinDir = Path.GetDirectoryName(assemblyLocation)!;

        // Navigate from test bin to backend Fixtures
        // Path: .../clis/SchemaTools.Tests.Integration/bin/Debug/net10.0/
        // Go up 5 levels to reach .../dms/, then down to backend/Fixtures
        var fixturePath = Path.Combine(
            testBinDir,
            "..",
            "..",
            "..",
            "..",
            "..",
            "backend",
            "Fixtures",
            "authoritative",
            "ds-5.2",
            "inputs",
            "ds-5.2-api-schema-authoritative.json"
        );

        return Path.GetFullPath(fixturePath);
    }

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
            // System.CommandLine returns 1 when a required subcommand is not provided
            _exitCode.Should().Be(1);
        }

        [Test]
        public void It_prints_usage_information()
        {
            _output.Should().Contain("Usage:");
            _output.Should().Contain("dms-schema");
        }

        [Test]
        public void It_shows_hash_command()
        {
            _output.Should().Contain("hash");
        }

        [Test]
        public void It_shows_ddl_command()
        {
            _output.Should().Contain("ddl");
        }
    }

    [TestFixture]
    public class Given_Hash_Command_With_Valid_Schema : SchemaToolsTests
    {
        private int _exitCode;
        private string _output = null!;

        [SetUp]
        public void SetUp()
        {
            var fixturePath = GetAuthoritativeFixturePath();
            (_exitCode, _output, _) = RunCli("hash", fixturePath);
        }

        [Test]
        public void It_returns_exit_code_0()
        {
            _exitCode.Should().Be(0);
        }

        [Test]
        public void It_prints_success_message()
        {
            _output.Should().Contain("Schema normalization successful.");
        }

        [Test]
        public void It_prints_effective_schema_hash()
        {
            _output.Should().Contain("Effective schema hash:");
        }
    }

    [TestFixture]
    public class Given_Hash_Command_With_Missing_File : SchemaToolsTests
    {
        private int _exitCode;
        private string _error = null!;

        [SetUp]
        public void SetUp()
        {
            (_exitCode, _, _error) = RunCli("hash", "nonexistent/path/ApiSchema.json");
        }

        [Test]
        public void It_returns_exit_code_1()
        {
            _exitCode.Should().Be(1);
        }

        [Test]
        public void It_prints_error_message()
        {
            _error.Should().Contain("File not found");
        }
    }

    [TestFixture]
    public class Given_Ddl_Emit_With_Valid_Schema : SchemaToolsTests
    {
        private int _exitCode;
        private string _output = null!;
        private string _outputDir = null!;

        [SetUp]
        public void SetUp()
        {
            _outputDir = Path.Combine(Path.GetTempPath(), $"dms-schema-test-{Guid.NewGuid():N}");
            var fixturePath = GetAuthoritativeFixturePath();
            (_exitCode, _output, _) = RunCli(
                "ddl",
                "emit",
                "--schema",
                fixturePath,
                "--output",
                _outputDir,
                "--dialect",
                "both"
            );
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_outputDir))
            {
                Directory.Delete(_outputDir, recursive: true);
            }
        }

        [Test]
        public void It_returns_exit_code_0()
        {
            _exitCode.Should().Be(0);
        }

        [Test]
        public void It_prints_completion_message()
        {
            _output.Should().Contain("DDL emission complete.");
        }

        [Test]
        public void It_prints_effective_schema_hash()
        {
            _output.Should().Contain("Effective schema hash:");
        }

        [Test]
        public void It_prints_resource_key_count()
        {
            _output.Should().Contain("Resource key count:");
        }

        [Test]
        public void It_creates_pgsql_sql_file()
        {
            File.Exists(Path.Combine(_outputDir, "pgsql.sql")).Should().BeTrue();
        }

        [Test]
        public void It_creates_mssql_sql_file()
        {
            File.Exists(Path.Combine(_outputDir, "mssql.sql")).Should().BeTrue();
        }

        [Test]
        public void It_creates_effective_schema_manifest()
        {
            File.Exists(Path.Combine(_outputDir, "effective-schema.manifest.json")).Should().BeTrue();
        }

        [Test]
        public void It_creates_pgsql_relational_model_manifest()
        {
            File.Exists(Path.Combine(_outputDir, "relational-model.pgsql.manifest.json")).Should().BeTrue();
        }

        [Test]
        public void It_creates_mssql_relational_model_manifest()
        {
            File.Exists(Path.Combine(_outputDir, "relational-model.mssql.manifest.json")).Should().BeTrue();
        }

        [Test]
        public void It_produces_non_empty_pgsql_sql()
        {
            new FileInfo(Path.Combine(_outputDir, "pgsql.sql")).Length.Should().BeGreaterThan(0);
        }

        [Test]
        public void It_produces_non_empty_mssql_sql()
        {
            new FileInfo(Path.Combine(_outputDir, "mssql.sql")).Length.Should().BeGreaterThan(0);
        }
    }

    [TestFixture]
    public class Given_Ddl_Emit_With_Single_Dialect : SchemaToolsTests
    {
        private int _exitCode;
        private string _outputDir = null!;

        [SetUp]
        public void SetUp()
        {
            _outputDir = Path.Combine(Path.GetTempPath(), $"dms-schema-test-{Guid.NewGuid():N}");
            var fixturePath = GetAuthoritativeFixturePath();
            (_exitCode, _, _) = RunCli(
                "ddl",
                "emit",
                "--schema",
                fixturePath,
                "--output",
                _outputDir,
                "--dialect",
                "pgsql"
            );
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_outputDir))
            {
                Directory.Delete(_outputDir, recursive: true);
            }
        }

        [Test]
        public void It_returns_exit_code_0()
        {
            _exitCode.Should().Be(0);
        }

        [Test]
        public void It_creates_pgsql_sql_file()
        {
            File.Exists(Path.Combine(_outputDir, "pgsql.sql")).Should().BeTrue();
        }

        [Test]
        public void It_does_not_create_mssql_sql_file()
        {
            File.Exists(Path.Combine(_outputDir, "mssql.sql")).Should().BeFalse();
        }

        [Test]
        public void It_creates_dialect_suffixed_relational_model_manifest()
        {
            // Manifest always includes dialect suffix since the model is dialect-dependent
            File.Exists(Path.Combine(_outputDir, "relational-model.pgsql.manifest.json")).Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_Ddl_Emit_Deterministic_Output : SchemaToolsTests
    {
        private string _outputDir1 = null!;
        private string _outputDir2 = null!;
        private bool _filesIdentical;

        [SetUp]
        public void SetUp()
        {
            _outputDir1 = Path.Combine(Path.GetTempPath(), $"dms-schema-test-{Guid.NewGuid():N}");
            _outputDir2 = Path.Combine(Path.GetTempPath(), $"dms-schema-test-{Guid.NewGuid():N}");
            var fixturePath = GetAuthoritativeFixturePath();

            RunCli("ddl", "emit", "--schema", fixturePath, "--output", _outputDir1, "--dialect", "pgsql");

            RunCli("ddl", "emit", "--schema", fixturePath, "--output", _outputDir2, "--dialect", "pgsql");

            // Compare all files in both directories
            _filesIdentical = true;
            foreach (var file1 in Directory.GetFiles(_outputDir1))
            {
                var fileName = Path.GetFileName(file1);
                var file2 = Path.Combine(_outputDir2, fileName);
                if (!File.Exists(file2))
                {
                    _filesIdentical = false;
                    break;
                }

                var bytes1 = File.ReadAllBytes(file1);
                var bytes2 = File.ReadAllBytes(file2);
                if (!bytes1.AsSpan().SequenceEqual(bytes2))
                {
                    _filesIdentical = false;
                    break;
                }
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_outputDir1))
            {
                Directory.Delete(_outputDir1, recursive: true);
            }

            if (Directory.Exists(_outputDir2))
            {
                Directory.Delete(_outputDir2, recursive: true);
            }
        }

        [Test]
        public void It_produces_byte_identical_output()
        {
            _filesIdentical.Should().BeTrue("DDL output should be deterministic across runs");
        }
    }

    [TestFixture]
    public class Given_Ddl_Emit_With_Invalid_Dialect : SchemaToolsTests
    {
        private int _exitCode;
        private string _error = null!;

        [SetUp]
        public void SetUp()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), $"dms-schema-test-{Guid.NewGuid():N}");
            var fixturePath = GetAuthoritativeFixturePath();
            (_exitCode, _, _error) = RunCli(
                "ddl",
                "emit",
                "--schema",
                fixturePath,
                "--output",
                outputDir,
                "--dialect",
                "oracle"
            );

            // Clean up
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }

        [Test]
        public void It_returns_exit_code_1()
        {
            _exitCode.Should().Be(1);
        }

        [Test]
        public void It_prints_error_about_invalid_dialect()
        {
            _error.Should().Contain("Invalid dialect");
        }
    }

    [TestFixture]
    public class Given_Ddl_Emit_With_Missing_Schema_File : SchemaToolsTests
    {
        private int _exitCode;
        private string _error = null!;

        [SetUp]
        public void SetUp()
        {
            var outputDir = Path.Combine(Path.GetTempPath(), $"dms-schema-test-{Guid.NewGuid():N}");
            (_exitCode, _, _error) = RunCli(
                "ddl",
                "emit",
                "--schema",
                "nonexistent/ApiSchema.json",
                "--output",
                outputDir
            );
        }

        [Test]
        public void It_returns_exit_code_1()
        {
            _exitCode.Should().Be(1);
        }

        [Test]
        public void It_prints_error_about_missing_file()
        {
            _error.Should().Contain("Schema file not found");
        }
    }
}
