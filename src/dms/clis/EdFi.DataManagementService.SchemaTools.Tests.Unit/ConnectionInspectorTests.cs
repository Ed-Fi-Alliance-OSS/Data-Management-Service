// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaTools.Connections;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

/// <summary>
/// Exercises the single exact-provider connection authority shared by the connection verbs and the
/// ddl-provision endpoint override. Parsing goes through the real Npgsql / Microsoft.Data.SqlClient builders,
/// so alias canonicalization, last-wins duplicate synonyms, and unsupported-keyword rejection match runtime;
/// there is no second alias table. The override rewrites only the endpoint and preserves every other option,
/// including the (secret) password, while the parse projection never exposes it.
/// </summary>
public class ConnectionInspectorTests
{
    [TestFixture]
    public class PostgreSql
    {
        private readonly IConnectionInspector _inspector = new PgsqlConnectionInspector();

        [Test]
        public void Parse_reads_canonical_coordinates()
        {
            var target = _inspector.Parse("Host=h;Port=5433;Username=u;Database=d;Password=secret");
            target.Host.Should().Be("h");
            target.Port.Should().Be(5433);
            target.Username.Should().Be("u");
            target.Database.Should().Be("d");
        }

        [Test]
        public void Parse_canonicalizes_Server_and_UserId_aliases()
        {
            var target = _inspector.Parse("Server=dms-postgresql;User Id=postgres;Database=edfi;Password=p");
            target.Host.Should().Be("dms-postgresql");
            target.Username.Should().Be("postgres");
            target.Database.Should().Be("edfi");
        }

        [Test]
        public void Parse_canonicalizes_UID_alias()
        {
            _inspector.Parse("Server=x;UID=postgres;Database=edfi").Username.Should().Be("postgres");
        }

        [Test]
        public void Parse_applies_provider_last_wins_when_Host_precedes_Server()
        {
            // The provider - not a text scanner - decides the effective endpoint: Server is later, so it wins.
            _inspector
                .Parse("Host=external;Server=dms-postgresql;Database=d")
                .Host.Should()
                .Be("dms-postgresql");
        }

        [Test]
        public void Parse_applies_provider_last_wins_when_Server_precedes_Host()
        {
            _inspector.Parse("Server=dms-postgresql;Host=external;Database=d").Host.Should().Be("external");
        }

        [Test]
        public void Parse_returns_null_database_when_absent()
        {
            _inspector.Parse("Host=h;Username=u").Database.Should().BeNull();
        }

        [Test]
        public void Parse_returns_null_username_when_absent()
        {
            _inspector.Parse("Host=h;Database=d").Username.Should().BeNull();
        }

        [Test]
        public void Parse_defaults_port_to_the_provider_default_when_absent()
        {
            _inspector.Parse("Host=h;Database=d").Port.Should().Be(5432);
        }

        [Test]
        public void Parse_throws_on_a_keyword_the_provider_does_not_support()
        {
            var act = () => _inspector.Parse("Host=h;Database=d;Bogus=x");
            act.Should().Throw<Exception>();
        }

        [Test]
        public void Parse_username_last_wins_when_Username_precedes_UserId()
        {
            _inspector
                .Parse("Host=h;Database=d;Username=first;User Id=second")
                .Username.Should()
                .Be("second");
        }

        [Test]
        public void Parse_username_last_wins_when_UserId_precedes_Username()
        {
            _inspector
                .Parse("Host=h;Database=d;User Id=first;Username=second")
                .Username.Should()
                .Be("second");
        }

        [Test]
        public void Parse_canonicalizes_the_Userid_alias()
        {
            _inspector.Parse("Host=h;Database=d;Userid=postgres").Username.Should().Be("postgres");
        }

        [Test]
        public void ApplyEndpointOverride_swaps_only_the_endpoint_and_preserves_everything_else()
        {
            var result = _inspector.ApplyEndpointOverride(
                "Host=dms-postgresql;Port=5432;Username=postgres;Password=s3cr3tP@ss;Database=edfi;SSL Mode=Require",
                "localhost",
                5439
            );

            // Endpoint swapped, proven by re-parsing with the exact provider.
            var reparsed = _inspector.Parse(result);
            reparsed.Host.Should().Be("localhost");
            reparsed.Port.Should().Be(5439);

            // Non-endpoint coordinates preserved.
            reparsed.Username.Should().Be("postgres");
            reparsed.Database.Should().Be("edfi");

            // The secret and other options are preserved verbatim in the connection string.
            result.Should().Contain("s3cr3tP@ss");
            result.Should().Contain("Require");
        }
    }

    [TestFixture]
    public class SqlServer
    {
        private readonly IConnectionInspector _inspector = new MssqlConnectionInspector();

        [Test]
        public void Parse_reads_canonical_coordinates_with_host_and_port_in_the_data_source()
        {
            var target = _inspector.Parse(
                "Server=dms-mssql,1433;Database=edfi;User Id=sa;Password=p;TrustServerCertificate=true"
            );
            target.Database.Should().Be("edfi");
            target.Host.Should().Be("dms-mssql,1433");
            target.Username.Should().Be("sa");
            // SQL Server encodes the port inside the data source; it exposes no separate port.
            target.Port.Should().BeNull();
        }

        [Test]
        public void Parse_collapses_Database_and_Initial_Catalog_last_wins()
        {
            _inspector.Parse("Server=x;Database=first;Initial Catalog=second").Database.Should().Be("second");
        }

        [Test]
        public void Parse_canonicalizes_Data_Source_and_UID_aliases()
        {
            var target = _inspector.Parse("Data Source=dms-mssql;Initial Catalog=edfi;UID=sa;Password=p");
            target.Host.Should().Be("dms-mssql");
            target.Database.Should().Be("edfi");
            target.Username.Should().Be("sa");
        }

        [Test]
        public void Parse_returns_null_database_when_absent()
        {
            _inspector.Parse("Server=x;User Id=sa;Password=p").Database.Should().BeNull();
        }

        [Test]
        public void Parse_throws_on_a_keyword_the_provider_does_not_support()
        {
            // Host is a PostgreSQL keyword, not a SQL Server one.
            var act = () => _inspector.Parse("Server=x;Database=d;Host=nope");
            act.Should().Throw<Exception>();
        }

        [Test]
        public void Parse_host_last_wins_when_Server_precedes_Data_Source()
        {
            _inspector.Parse("Server=first;Data Source=second;Database=d").Host.Should().Be("second");
        }

        [Test]
        public void Parse_host_last_wins_when_Data_Source_precedes_Server()
        {
            _inspector.Parse("Data Source=first;Server=second;Database=d").Host.Should().Be("second");
        }

        [Test]
        public void Parse_username_last_wins_when_UserId_precedes_UID()
        {
            _inspector.Parse("Server=x;Database=d;User Id=first;UID=second").Username.Should().Be("second");
        }

        [Test]
        public void Parse_username_last_wins_when_UID_precedes_UserId()
        {
            _inspector.Parse("Server=x;Database=d;UID=first;User Id=second").Username.Should().Be("second");
        }

        [Test]
        public void ApplyEndpointOverride_sets_host_comma_port_and_preserves_everything_else()
        {
            var result = _inspector.ApplyEndpointOverride(
                "Server=dms-mssql;Database=edfi;User Id=sa;Password=s3cr3tP@ss;TrustServerCertificate=true",
                "127.0.0.1",
                1435
            );

            var reparsed = _inspector.Parse(result);
            reparsed.Host.Should().Be("127.0.0.1,1435");
            reparsed.Database.Should().Be("edfi");
            reparsed.Username.Should().Be("sa");

            result.Should().Contain("s3cr3tP@ss");
            // The exact provider canonicalizes the keyword to "Trust Server Certificate"; the option survives.
            result.Should().Contain("Trust Server Certificate");
        }
    }

    [TestFixture]
    public class EngineCanonicalization
    {
        [TestCase("postgresql", "postgresql")]
        [TestCase("PostgreSQL", "postgresql")]
        [TestCase("mssql", "mssql")]
        [TestCase("MSSQL", "mssql")]
        public void Canonicalizes_supported_engines_case_insensitively(string input, string expected)
        {
            ConnectionInspectors.CanonicalizeEngine(input).Should().Be(expected);
        }

        [TestCase("mysql")]
        [TestCase(" mssql ")]
        [TestCase("")]
        public void Returns_null_for_an_unsupported_or_whitespace_padded_engine(string input)
        {
            ConnectionInspectors.CanonicalizeEngine(input).Should().BeNull();
        }

        [Test]
        public void ForEngine_returns_the_matching_inspector_or_null()
        {
            ConnectionInspectors.ForEngine("PostgreSQL").Should().BeOfType<PgsqlConnectionInspector>();
            ConnectionInspectors.ForEngine("mssql").Should().BeOfType<MssqlConnectionInspector>();
            ConnectionInspectors.ForEngine("mysql").Should().BeNull();
        }
    }
}
