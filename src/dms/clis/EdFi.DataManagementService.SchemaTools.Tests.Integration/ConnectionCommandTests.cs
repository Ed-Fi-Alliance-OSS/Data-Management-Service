// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

/// <summary>
/// Exercises the 'connection validate' verb's engine-token boundary: the direct CLI must accept the
/// two supported engines case-insensitively (resolving publicly documented variants such as MSSQL /
/// PostgreSQL to the canonical token) while rejecting unsupported and surrounding-whitespace values as
/// usage errors (exit 2). The connection string is supplied on stdin, exactly as the start scripts pass it.
/// </summary>
[TestFixture]
public class ConnectionCommandTests
{
    [TestFixture]
    public class Given_Connection_Validate_With_Uppercase_Mssql_Engine : ConnectionCommandTests
    {
        private int _exitCode;
        private string _output = null!;

        [SetUp]
        public void SetUp()
        {
            (_exitCode, _output, _) = CliTestHelper.RunCliWithStandardInput(
                "Server=localhost;Database=Foo;",
                "connection",
                "validate",
                "--engine",
                "MSSQL"
            );
        }

        [Test]
        public void It_returns_exit_code_0()
        {
            _exitCode.Should().Be(0);
        }

        [Test]
        public void It_reports_the_connection_as_valid()
        {
            _output.Should().Contain("\"valid\":true");
        }

        [Test]
        public void It_reports_the_target_database()
        {
            _output.Should().Contain("\"database\":\"Foo\"");
        }
    }

    [TestFixture]
    public class Given_Connection_Validate_With_Mixedcase_Postgresql_Engine : ConnectionCommandTests
    {
        private int _exitCode;
        private string _output = null!;

        [SetUp]
        public void SetUp()
        {
            (_exitCode, _output, _) = CliTestHelper.RunCliWithStandardInput(
                "Host=localhost;Database=foo;",
                "connection",
                "validate",
                "--engine",
                "PostgreSQL"
            );
        }

        [Test]
        public void It_returns_exit_code_0()
        {
            _exitCode.Should().Be(0);
        }

        [Test]
        public void It_reports_the_connection_as_valid()
        {
            _output.Should().Contain("\"valid\":true");
        }

        [Test]
        public void It_reports_the_target_database()
        {
            _output.Should().Contain("\"database\":\"foo\"");
        }
    }

    [TestFixture]
    public class Given_Connection_Validate_Case_Variant_Matches_Canonical : ConnectionCommandTests
    {
        private string _canonicalOutput = null!;
        private string _variantOutput = null!;

        [SetUp]
        public void SetUp()
        {
            const string ConnectionString = "Server=localhost;Database=Foo;";
            (_, _canonicalOutput, _) = CliTestHelper.RunCliWithStandardInput(
                ConnectionString,
                "connection",
                "validate",
                "--engine",
                "mssql"
            );
            (_, _variantOutput, _) = CliTestHelper.RunCliWithStandardInput(
                ConnectionString,
                "connection",
                "validate",
                "--engine",
                "MSSQL"
            );
        }

        [Test]
        public void It_produces_the_same_result_as_the_canonical_token()
        {
            _variantOutput.Should().Be(_canonicalOutput);
        }
    }

    [TestFixture]
    public class Given_Connection_Validate_With_Lowercase_Engine_Still_Works : ConnectionCommandTests
    {
        private int _exitCode;
        private string _output = null!;

        [SetUp]
        public void SetUp()
        {
            (_exitCode, _output, _) = CliTestHelper.RunCliWithStandardInput(
                "Host=localhost;Database=foo;",
                "connection",
                "validate",
                "--engine",
                "postgresql"
            );
        }

        [Test]
        public void It_returns_exit_code_0()
        {
            _exitCode.Should().Be(0);
        }

        [Test]
        public void It_reports_the_connection_as_valid()
        {
            _output.Should().Contain("\"valid\":true");
        }
    }

    [TestFixture]
    public class Given_Connection_Validate_With_Unsupported_Engine : ConnectionCommandTests
    {
        private int _exitCode;
        private string _error = null!;

        [SetUp]
        public void SetUp()
        {
            (_exitCode, _, _error) = CliTestHelper.RunCliWithStandardInput(
                "Host=localhost;Database=foo;",
                "connection",
                "validate",
                "--engine",
                "mysql"
            );
        }

        [Test]
        public void It_returns_usage_error_exit_code_2()
        {
            _exitCode.Should().Be(2);
        }

        [Test]
        public void It_reports_the_engine_as_unsupported()
        {
            _error.Should().Contain("Unsupported engine");
        }
    }

    [TestFixture]
    public class Given_Connection_Validate_With_Whitespace_Padded_Engine : ConnectionCommandTests
    {
        private int _exitCode;
        private string _error = null!;

        [SetUp]
        public void SetUp()
        {
            // A surrounding-whitespace variant is not a canonical engine; it must fail as a usage error
            // rather than being silently trimmed.
            (_exitCode, _, _error) = CliTestHelper.RunCliWithStandardInput(
                "Server=localhost;Database=Foo;",
                "connection",
                "validate",
                "--engine",
                " mssql "
            );
        }

        [Test]
        public void It_returns_usage_error_exit_code_2()
        {
            _exitCode.Should().Be(2);
        }

        [Test]
        public void It_reports_the_engine_as_unsupported()
        {
            _error.Should().Contain("Unsupported engine");
        }
    }

    [TestFixture]
    public class Given_Connection_Validate_A_Valid_Connection : ConnectionCommandTests
    {
        private string _output = null!;

        [SetUp]
        public void SetUp()
        {
            (_, _output, _) = CliTestHelper.RunCliWithStandardInput(
                "Host=localhost;Database=Foo;",
                "connection",
                "validate",
                "--engine",
                "postgresql"
            );
        }

        [Test]
        public void It_emits_exactly_the_valid_result_contract()
        {
            // Byte-stable contract: exactly these fields, in this order. Only the platform newline is normalized.
            _output
                .Replace("\r\n", "\n")
                .TrimEnd('\n')
                .Should()
                .Be("{\"valid\":true,\"database\":\"Foo\",\"error\":null}");
        }
    }

    [TestFixture]
    public class Given_Connection_Validate_An_Invalid_Connection : ConnectionCommandTests
    {
        private int _exitCode;
        private string _output = null!;

        [SetUp]
        public void SetUp()
        {
            // An unsupported keyword makes the exact provider reject the string: a { valid:false } result at exit 0.
            (_exitCode, _output, _) = CliTestHelper.RunCliWithStandardInput(
                "Host=localhost;Database=Foo;Bogus=x",
                "connection",
                "validate",
                "--engine",
                "postgresql"
            );
        }

        [Test]
        public void It_returns_exit_code_0()
        {
            _exitCode.Should().Be(0);
        }

        [Test]
        public void It_reports_the_connection_as_invalid_with_a_null_database()
        {
            _output.Should().Contain("\"valid\":false");
            _output.Should().Contain("\"database\":null");
            _output.Should().Contain("\"error\":");
        }

        [Test]
        public void It_emits_exactly_the_validate_field_set()
        {
            // Parse and assert the exact top-level property set, so an added, removed, or renamed field
            // fails - validate must never acquire the inspection fields (host/port/username) or any other.
            using var document = System.Text.Json.JsonDocument.Parse(_output);
            var propertyNames = new System.Collections.Generic.List<string>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                propertyNames.Add(property.Name);
            }
            propertyNames.Should().BeEquivalentTo("valid", "database", "error");
        }
    }
}
