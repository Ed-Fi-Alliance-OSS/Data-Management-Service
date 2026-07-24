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

        [Test]
        public void It_classifies_the_endpoint_as_a_single_tcp_host()
        {
            using var document = System.Text.Json.JsonDocument.Parse(_output);
            var endpoint = document.RootElement.GetProperty("endpoint");
            endpoint.GetProperty("kind").GetString().Should().Be("singleHost");
            endpoint.GetProperty("protocol").GetString().Should().Be("tcp");
            endpoint.GetProperty("host").GetString().Should().Be("dms-postgresql");
            endpoint.GetProperty("port").GetInt32().Should().Be(5432);
            endpoint.GetProperty("instance").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
            endpoint.GetProperty("hasAlternateRouting").GetBoolean().Should().BeFalse();
        }

        [Test]
        public void It_emits_exactly_the_inspect_field_set_including_the_additive_endpoint()
        {
            // The additive contract: the six original fields plus 'endpoint', and nothing else.
            using var document = System.Text.Json.JsonDocument.Parse(_output);
            var propertyNames = new System.Collections.Generic.List<string>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                propertyNames.Add(property.Name);
            }
            propertyNames
                .Should()
                .BeEquivalentTo("valid", "database", "host", "port", "username", "error", "endpoint");
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

        [Test]
        public void It_keeps_the_top_level_port_null_while_the_endpoint_splits_host_and_port()
        {
            using var document = System.Text.Json.JsonDocument.Parse(_output);
            // Existing contract: SQL Server keeps the port inside the data source, so top-level port is null.
            document
                .RootElement.GetProperty("port")
                .ValueKind.Should()
                .Be(System.Text.Json.JsonValueKind.Null);
            // The additive endpoint classification splits it out.
            var endpoint = document.RootElement.GetProperty("endpoint");
            endpoint.GetProperty("kind").GetString().Should().Be("singleHost");
            endpoint.GetProperty("host").GetString().Should().Be("dms-mssql");
            endpoint.GetProperty("port").GetInt32().Should().Be(1433);
            endpoint.GetProperty("hasAlternateRouting").GetBoolean().Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_Inspect_SqlServer_Named_Instance : ConnectionInspectCommandTests
    {
        private string _output = null!;

        [SetUp]
        public void SetUp()
        {
            (_, _output, _) = CliTestHelper.RunCliWithStandardInput(
                "Server=dms-mssql\\SQLEXPRESS;Database=edfi;User Id=sa;Password=p;TrustServerCertificate=true",
                "connection",
                "inspect",
                "--engine",
                "mssql"
            );
        }

        [Test]
        public void It_classifies_a_named_instance_as_a_valid_but_non_single_host_shape()
        {
            using var document = System.Text.Json.JsonDocument.Parse(_output);
            // Provider-valid, but not a single local TCP host - a coherent classification, not a failure.
            document.RootElement.GetProperty("valid").GetBoolean().Should().BeTrue();
            var endpoint = document.RootElement.GetProperty("endpoint");
            endpoint.GetProperty("kind").GetString().Should().Be("namedInstance");
            endpoint.GetProperty("host").GetString().Should().Be("dms-mssql");
            endpoint.GetProperty("instance").GetString().Should().Be("SQLEXPRESS");
        }
    }

    [TestFixture]
    public class Given_Inspect_SqlServer_Empty_Instance_Delimiter : ConnectionInspectCommandTests
    {
        [TestCase("Server=dms-mssql\\;Database=d;User Id=sa;Password=p;TrustServerCertificate=true")]
        [TestCase("Server=dms-mssql\\,1433;Database=d;User Id=sa;Password=p;TrustServerCertificate=true")]
        public void It_classifies_a_blank_instance_delimiter_as_unsupported(string connectionString)
        {
            // Real-provider proof (both malformed forms): a backslash with no instance must not collapse into
            // the local single-host identity.
            var (_, output, _) = CliTestHelper.RunCliWithStandardInput(
                connectionString,
                "connection",
                "inspect",
                "--engine",
                "mssql"
            );
            using var document = System.Text.Json.JsonDocument.Parse(output);
            document.RootElement.GetProperty("valid").GetBoolean().Should().BeTrue();
            var endpoint = document.RootElement.GetProperty("endpoint");
            endpoint.GetProperty("kind").GetString().Should().Be("unsupported");
            endpoint.GetProperty("host").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
            endpoint.GetProperty("instance").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
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

        [Test]
        public void It_reports_a_null_endpoint_for_an_invalid_connection()
        {
            // Provider validity and endpoint classification are distinct: an invalid connection has no
            // classified endpoint.
            using var document = System.Text.Json.JsonDocument.Parse(_output);
            document
                .RootElement.GetProperty("endpoint")
                .ValueKind.Should()
                .Be(System.Text.Json.JsonValueKind.Null);
        }
    }
}
