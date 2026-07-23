// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

/// <summary>
/// Exercises the 'connection inspect' verb: it reads the connection string from stdin (so the password never
/// enters the process arguments), parses it with the exact runtime provider, and prints a JSON
/// { valid, database, host, port, username, error } of NON-SECRET canonical fields. It shares the engine
/// boundary with 'connection validate' (unsupported engine is a usage error, exit 2).
/// </summary>
[TestFixture]
public class ConnectionInspectCommandTests
{
    [TestFixture]
    public class Given_Inspect_Postgresql_With_Aliases : ConnectionInspectCommandTests
    {
        private int _exitCode;
        private string _output = null!;

        [SetUp]
        public void SetUp()
        {
            (_exitCode, _output, _) = CliTestHelper.RunCliWithStandardInput(
                "Server=dms-postgresql;User Id=postgres;Database=edfi;Password=sup3rSecretValue",
                "connection",
                "inspect",
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

        [Test]
        public void It_canonicalizes_the_alias_coordinates()
        {
            _output.Should().Contain("\"database\":\"edfi\"");
            _output.Should().Contain("\"host\":\"dms-postgresql\"");
            _output.Should().Contain("\"username\":\"postgres\"");
            _output.Should().Contain("\"port\":5432");
        }

        [Test]
        public void It_does_not_emit_the_password()
        {
            _output.Should().NotContain("sup3rSecretValue");
            _output.Should().NotContain("password");
        }
    }

    [TestFixture]
    public class Given_Inspect_SqlServer : ConnectionInspectCommandTests
    {
        private int _exitCode;
        private string _output = null!;

        [SetUp]
        public void SetUp()
        {
            (_exitCode, _output, _) = CliTestHelper.RunCliWithStandardInput(
                "Server=dms-mssql,1433;Database=edfi;User Id=sa;Password=sup3rSecretValue;TrustServerCertificate=true",
                "connection",
                "inspect",
                "--engine",
                "mssql"
            );
        }

        [Test]
        public void It_returns_exit_code_0()
        {
            _exitCode.Should().Be(0);
        }

        [Test]
        public void It_reports_the_host_with_the_port_encoded_in_the_data_source_and_a_null_port()
        {
            _output.Should().Contain("\"database\":\"edfi\"");
            _output.Should().Contain("\"host\":\"dms-mssql,1433\"");
            _output.Should().Contain("\"username\":\"sa\"");
            _output.Should().Contain("\"port\":null");
        }

        [Test]
        public void It_does_not_emit_the_password()
        {
            _output.Should().NotContain("sup3rSecretValue");
        }
    }

    [TestFixture]
    public class Given_Inspect_A_Valid_Connection_With_No_Database : ConnectionInspectCommandTests
    {
        private int _exitCode;
        private string _output = null!;

        [SetUp]
        public void SetUp()
        {
            (_exitCode, _output, _) = CliTestHelper.RunCliWithStandardInput(
                "Host=dms-postgresql;Username=postgres",
                "connection",
                "inspect",
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
        public void It_reports_valid_with_a_null_database()
        {
            _output.Should().Contain("\"valid\":true");
            _output.Should().Contain("\"database\":null");
        }
    }

    [TestFixture]
    public class Given_Inspect_With_Unsupported_Engine : ConnectionInspectCommandTests
    {
        private int _exitCode;
        private string _error = null!;

        [SetUp]
        public void SetUp()
        {
            (_exitCode, _, _error) = CliTestHelper.RunCliWithStandardInput(
                "Host=localhost;Database=foo",
                "connection",
                "inspect",
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
    public class Given_Inspect_An_Invalid_Connection_Carrying_A_Secret : ConnectionInspectCommandTests
    {
        private int _exitCode;
        private string _output = null!;
        private string _error = null!;

        [SetUp]
        public void SetUp()
        {
            // An unsupported keyword makes the provider reject the string; the sentinel password must not leak
            // into the structured result or the diagnostic stderr.
            (_exitCode, _output, _error) = CliTestHelper.RunCliWithStandardInput(
                "Host=dms-postgresql;Database=edfi;Password=sup3rSecretValue;Bogus=x",
                "connection",
                "inspect",
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
        public void It_reports_the_connection_as_invalid()
        {
            _output.Should().Contain("\"valid\":false");
        }

        [Test]
        public void It_leaks_the_password_in_neither_stdout_nor_stderr()
        {
            _output.Should().NotContain("sup3rSecretValue");
            _error.Should().NotContain("sup3rSecretValue");
        }
    }
}
