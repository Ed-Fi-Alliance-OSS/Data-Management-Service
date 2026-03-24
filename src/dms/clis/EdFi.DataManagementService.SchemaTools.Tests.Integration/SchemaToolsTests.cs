// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
            (_exitCode, _output, _) = CliTestHelper.RunCli();
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
    [Category("Authoritative")]
    public class Given_Hash_Command_With_Valid_Schema : SchemaToolsTests
    {
        private int _exitCode;
        private string _output = null!;

        [SetUp]
        public void SetUp()
        {
            var fixturePath = CliTestHelper.GetMinimalSchemaPath();
            (_exitCode, _output, _) = CliTestHelper.RunCli("hash", fixturePath);
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
            (_exitCode, _, _error) = CliTestHelper.RunCli("hash", "nonexistent/path/ApiSchema.json");
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
    [Category("Authoritative")]
    public class Given_Ddl_Emit_With_Valid_Schema : SchemaToolsTests
    {
        private int _exitCode;
        private string _output = null!;
        private string _outputDir = null!;

        [SetUp]
        public void SetUp()
        {
            _outputDir = Path.Combine(Path.GetTempPath(), $"dms-schema-test-{Guid.NewGuid():N}");
            var fixturePath = CliTestHelper.GetMinimalSchemaPath();
            (_exitCode, _output, _) = CliTestHelper.RunCli(
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
    [Category("Authoritative")]
    public class Given_Ddl_Emit_With_Single_Dialect : SchemaToolsTests
    {
        private int _exitCode;
        private string _outputDir = null!;

        [SetUp]
        public void SetUp()
        {
            _outputDir = Path.Combine(Path.GetTempPath(), $"dms-schema-test-{Guid.NewGuid():N}");
            var fixturePath = CliTestHelper.GetMinimalSchemaPath();
            (_exitCode, _, _) = CliTestHelper.RunCli(
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
        private string _outputDir3 = null!;
        private string _outputDir4 = null!;

        [SetUp]
        public void SetUp()
        {
            _outputDir1 = Path.Combine(Path.GetTempPath(), $"dms-schema-test-{Guid.NewGuid():N}");
            _outputDir2 = Path.Combine(Path.GetTempPath(), $"dms-schema-test-{Guid.NewGuid():N}");
            _outputDir3 = Path.Combine(Path.GetTempPath(), $"dms-schema-test-{Guid.NewGuid():N}");
            _outputDir4 = Path.Combine(Path.GetTempPath(), $"dms-schema-test-{Guid.NewGuid():N}");
            var fixturePath = CliTestHelper.GetMinimalSchemaPath();

            var (exit1, _, err1) = CliTestHelper.RunCli(
                "ddl",
                "emit",
                "--schema",
                fixturePath,
                "--output",
                _outputDir1,
                "--dialect",
                "pgsql"
            );
            Assert.That(exit1, Is.EqualTo(0), $"pgsql run 1 failed: {err1}");

            var (exit2, _, err2) = CliTestHelper.RunCli(
                "ddl",
                "emit",
                "--schema",
                fixturePath,
                "--output",
                _outputDir2,
                "--dialect",
                "pgsql"
            );
            Assert.That(exit2, Is.EqualTo(0), $"pgsql run 2 failed: {err2}");

            var (exit3, _, err3) = CliTestHelper.RunCli(
                "ddl",
                "emit",
                "--schema",
                fixturePath,
                "--output",
                _outputDir3,
                "--dialect",
                "mssql"
            );
            Assert.That(exit3, Is.EqualTo(0), $"mssql run 1 failed: {err3}");

            var (exit4, _, err4) = CliTestHelper.RunCli(
                "ddl",
                "emit",
                "--schema",
                fixturePath,
                "--output",
                _outputDir4,
                "--dialect",
                "mssql"
            );
            Assert.That(exit4, Is.EqualTo(0), $"mssql run 2 failed: {err4}");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var dir in new[] { _outputDir1, _outputDir2, _outputDir3, _outputDir4 })
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }

        [Test]
        public void It_produces_byte_identical_pgsql_output()
        {
            AssertDirectoriesIdentical(_outputDir1, _outputDir2)
                .Should()
                .BeTrue("pgsql DDL output should be deterministic across runs");
        }

        [Test]
        public void It_produces_byte_identical_mssql_output()
        {
            AssertDirectoriesIdentical(_outputDir3, _outputDir4)
                .Should()
                .BeTrue("mssql DDL output should be deterministic across runs");
        }

        private static bool AssertDirectoriesIdentical(string dir1, string dir2)
        {
            var files1 = Directory.GetFiles(dir1).Select(Path.GetFileName).Order().ToList();
            var files2 = Directory.GetFiles(dir2).Select(Path.GetFileName).Order().ToList();

            files1.Should().BeEquivalentTo(files2, "both directories should contain the same set of files");

            foreach (var fileName in files1)
            {
                var bytes1 = File.ReadAllBytes(Path.Combine(dir1, fileName!));
                var bytes2 = File.ReadAllBytes(Path.Combine(dir2, fileName!));
                if (!bytes1.AsSpan().SequenceEqual(bytes2))
                {
                    // Find first differing byte offset for diagnostics
                    var offset = 0;
                    while (
                        offset < bytes1.Length && offset < bytes2.Length && bytes1[offset] == bytes2[offset]
                    )
                    {
                        offset++;
                    }

                    Assert.Fail(
                        $"File '{fileName}' differs at byte offset {offset} "
                            + $"(length1={bytes1.Length}, length2={bytes2.Length})"
                    );
                }
            }

            return true;
        }
    }

    [TestFixture]
    public class Given_Ddl_Emit_Without_Ddl_Manifest_Flag : SchemaToolsTests
    {
        private int _exitCode;
        private string _outputDir = null!;

        [SetUp]
        public void SetUp()
        {
            _outputDir = Path.Combine(Path.GetTempPath(), $"dms-schema-test-{Guid.NewGuid():N}");
            var fixturePath = CliTestHelper.GetMinimalSchemaPath();
            (_exitCode, _, _) = CliTestHelper.RunCli(
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
        public void It_does_not_create_ddl_manifest()
        {
            File.Exists(Path.Combine(_outputDir, "ddl.manifest.json")).Should().BeFalse();
        }

        [Test]
        public void It_still_creates_effective_schema_manifest()
        {
            File.Exists(Path.Combine(_outputDir, "effective-schema.manifest.json")).Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_Ddl_Emit_With_Ddl_Manifest_Flag : SchemaToolsTests
    {
        private int _exitCode;
        private string _outputDir = null!;

        [SetUp]
        public void SetUp()
        {
            _outputDir = Path.Combine(Path.GetTempPath(), $"dms-schema-test-{Guid.NewGuid():N}");
            var fixturePath = CliTestHelper.GetMinimalSchemaPath();
            (_exitCode, _, _) = CliTestHelper.RunCli(
                "ddl",
                "emit",
                "--schema",
                fixturePath,
                "--output",
                _outputDir,
                "--dialect",
                "pgsql",
                "--ddl-manifest"
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
        public void It_creates_ddl_manifest()
        {
            File.Exists(Path.Combine(_outputDir, "ddl.manifest.json")).Should().BeTrue();
        }

        [Test]
        public void It_creates_effective_schema_manifest()
        {
            File.Exists(Path.Combine(_outputDir, "effective-schema.manifest.json")).Should().BeTrue();
        }
    }

    [TestFixture]
    [Category("Authoritative")]
    public class Given_Ddl_Emit_With_Invalid_Dialect : SchemaToolsTests
    {
        private int _exitCode;
        private string _error = null!;
        private string _outputDir = null!;

        [SetUp]
        public void SetUp()
        {
            _outputDir = Path.Combine(Path.GetTempPath(), $"dms-schema-test-{Guid.NewGuid():N}");
            var fixturePath = CliTestHelper.GetMinimalSchemaPath();
            (_exitCode, _, _error) = CliTestHelper.RunCli(
                "ddl",
                "emit",
                "--schema",
                fixturePath,
                "--output",
                _outputDir,
                "--dialect",
                "oracle"
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
        public void It_returns_exit_code_1()
        {
            _exitCode.Should().Be(1);
        }

        [Test]
        public void It_prints_error_about_invalid_dialect()
        {
            // System.CommandLine's AcceptOnlyFromAmong rejects 'oracle' at parse time
            _error.Should().Contain("oracle");
        }
    }

    [TestFixture]
    public class Given_Ddl_Emit_With_Missing_Schema_File : SchemaToolsTests
    {
        private int _exitCode;
        private string _error = null!;
        private string _outputDir = null!;

        [SetUp]
        public void SetUp()
        {
            _outputDir = Path.Combine(Path.GetTempPath(), $"dms-schema-test-{Guid.NewGuid():N}");
            (_exitCode, _, _error) = CliTestHelper.RunCli(
                "ddl",
                "emit",
                "--schema",
                "nonexistent/ApiSchema.json",
                "--output",
                _outputDir
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
        public void It_returns_exit_code_1()
        {
            _exitCode.Should().Be(1);
        }

        [Test]
        public void It_prints_error_about_missing_file()
        {
            _error.Should().Contain("File not found");
        }
    }
}
