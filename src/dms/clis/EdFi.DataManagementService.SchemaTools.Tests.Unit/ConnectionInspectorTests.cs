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

        [Test]
        public void ClassifyEndpoint_reports_a_single_TCP_host_with_its_port()
        {
            var endpoint = _inspector.ClassifyEndpoint("Host=dms-postgresql;Port=5432;Database=d");
            endpoint.Kind.Should().Be(ConnectionEndpointKinds.SingleHost);
            endpoint.Protocol.Should().Be(ConnectionEndpointProtocols.Tcp);
            endpoint.Host.Should().Be("dms-postgresql");
            endpoint.Port.Should().Be(5432);
            endpoint.Instance.Should().BeNull();
            endpoint.HasAlternateRouting.Should().BeFalse();
        }

        [Test]
        public void ClassifyEndpoint_uses_the_provider_default_port_when_absent()
        {
            _inspector.ClassifyEndpoint("Host=dms-postgresql;Database=d").Port.Should().Be(5432);
        }

        [Test]
        public void ClassifyEndpoint_reports_missing_when_no_host_is_specified()
        {
            var endpoint = _inspector.ClassifyEndpoint("Username=u;Database=d");
            endpoint.Kind.Should().Be(ConnectionEndpointKinds.Missing);
            endpoint.Host.Should().BeNull();
            endpoint.Port.Should().BeNull();
        }

        [Test]
        public void ClassifyEndpoint_reports_multi_host_for_a_comma_separated_host_list()
        {
            // PostgreSQL's own failover/load-balancing form is not a single local endpoint.
            var endpoint = _inspector.ClassifyEndpoint("Host=primary,standby;Database=d");
            endpoint.Kind.Should().Be(ConnectionEndpointKinds.MultiHost);
            endpoint.Host.Should().BeNull();
            endpoint.Port.Should().BeNull();
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

        [Test]
        public void ClassifyEndpoint_reports_a_single_host_with_the_port_split_from_the_data_source()
        {
            var endpoint = _inspector.ClassifyEndpoint(
                "Server=dms-mssql,1433;Database=d;User Id=sa;Password=p;TrustServerCertificate=true"
            );
            endpoint.Kind.Should().Be(ConnectionEndpointKinds.SingleHost);
            endpoint.Host.Should().Be("dms-mssql");
            endpoint.Port.Should().Be(1433);
            endpoint.Instance.Should().BeNull();
            endpoint.HasAlternateRouting.Should().BeFalse();
        }

        [Test]
        public void ClassifyEndpoint_canonicalizes_an_omitted_single_host_port_to_1433()
        {
            var endpoint = _inspector.ClassifyEndpoint("Server=dms-mssql;Database=d;User Id=sa;Password=p");
            endpoint.Kind.Should().Be(ConnectionEndpointKinds.SingleHost);
            endpoint.Host.Should().Be("dms-mssql");
            endpoint.Port.Should().Be(1433);
        }

        [Test]
        public void ClassifyEndpoint_treats_a_dedicated_admin_connection_as_a_non_local_unsupported_shape()
        {
            var endpoint = _inspector.ClassifyEndpoint(
                "Server=admin:dms-mssql;Database=d;User Id=sa;Password=p"
            );
            endpoint.Kind.Should().Be(ConnectionEndpointKinds.Unsupported);
            endpoint.Protocol.Should().Be(ConnectionEndpointProtocols.Admin);
            endpoint.Host.Should().BeNull();
        }

        [Test]
        public void ClassifyEndpoint_strips_the_tcp_protocol_prefix()
        {
            var endpoint = _inspector.ClassifyEndpoint(
                "Server=tcp:dms-mssql,1433;Database=d;User Id=sa;Password=p"
            );
            endpoint.Kind.Should().Be(ConnectionEndpointKinds.SingleHost);
            endpoint.Protocol.Should().Be(ConnectionEndpointProtocols.Tcp);
            endpoint.Host.Should().Be("dms-mssql");
            endpoint.Port.Should().Be(1433);
        }

        [Test]
        public void ClassifyEndpoint_reports_a_named_instance()
        {
            var endpoint = _inspector.ClassifyEndpoint(
                "Server=dms-mssql\\SQLEXPRESS;Database=d;User Id=sa;Password=p"
            );
            endpoint.Kind.Should().Be(ConnectionEndpointKinds.NamedInstance);
            endpoint.Host.Should().Be("dms-mssql");
            endpoint.Instance.Should().Be("SQLEXPRESS");
        }

        [Test]
        public void ClassifyEndpoint_flags_alternate_routing_when_a_failover_partner_is_present()
        {
            var endpoint = _inspector.ClassifyEndpoint(
                "Server=dms-mssql,1433;Failover Partner=remote-mssql;Database=d;User Id=sa;Password=p"
            );
            // A locally named primary can redirect to a remote physical server.
            endpoint.HasAlternateRouting.Should().BeTrue();
            endpoint.Host.Should().Be("dms-mssql");
        }

        [Test]
        public void ClassifyEndpoint_reports_missing_when_no_server_is_specified()
        {
            var endpoint = _inspector.ClassifyEndpoint("Database=d;User Id=sa;Password=p");
            endpoint.Kind.Should().Be(ConnectionEndpointKinds.Missing);
            endpoint.Host.Should().BeNull();
        }
    }

    /// <summary>
    /// The finite SQL Server data-source grammar interpreted by <see cref="SqlServerEndpointClassifier"/>
    /// (SqlClient supplies the data-source value; this component splits it). Non-TCP transports remain
    /// coherent, valid classifications - just not single local TCP endpoints.
    /// </summary>
    [TestFixture]
    public class SqlServerDataSourceGrammar
    {
        [TestCase("np:dms-mssql", ConnectionEndpointProtocols.NamedPipes)]
        [TestCase("lpc:dms-mssql", ConnectionEndpointProtocols.SharedMemory)]
        [TestCase("via:dms-mssql", ConnectionEndpointProtocols.Unknown)]
        [TestCase("admin:dms-mssql,1433", ConnectionEndpointProtocols.Admin)]
        public void A_non_TCP_protocol_is_a_coherent_unsupported_shape(string dataSource, string protocol)
        {
            // The protocol is retained (never erased into tcp), and no host/port coordinates are invented, so a
            // later locality check rejects it as a non-single-local-TCP shape.
            var endpoint = SqlServerEndpointClassifier.Classify(dataSource, hasAlternateRouting: false);
            endpoint.Kind.Should().Be(ConnectionEndpointKinds.Unsupported);
            endpoint.Protocol.Should().Be(protocol);
            endpoint.Host.Should().BeNull();
            endpoint.Port.Should().BeNull();
        }

        [TestCase("dms-mssql,")]
        [TestCase("dms-mssql,abc")]
        [TestCase("dms-mssql,1433,extra")]
        [TestCase("dms-mssql,70000")]
        public void A_malformed_or_ambiguous_port_suffix_is_unsupported(string dataSource)
        {
            // An empty, non-numeric, out-of-range, or multi-comma port cannot be a TCP endpoint; it must not
            // collapse to a null port that is indistinguishable from a bare host.
            var endpoint = SqlServerEndpointClassifier.Classify(dataSource, hasAlternateRouting: false);
            endpoint.Kind.Should().Be(ConnectionEndpointKinds.Unsupported);
            endpoint.Protocol.Should().Be(ConnectionEndpointProtocols.Unknown);
            endpoint.Host.Should().BeNull();
            endpoint.Port.Should().BeNull();
        }

        [TestCase("dms-mssql\\")]
        [TestCase("dms-mssql\\,1433")]
        public void A_backslash_delimiter_with_no_instance_is_unsupported(string dataSource)
        {
            // An empty instance after the backslash is malformed routing syntax, not a bare host; it must not
            // collapse into the local single-host identity (host + default port 1433).
            var endpoint = SqlServerEndpointClassifier.Classify(dataSource, hasAlternateRouting: false);
            endpoint.Kind.Should().Be(ConnectionEndpointKinds.Unsupported);
            endpoint.Host.Should().BeNull();
            endpoint.Port.Should().BeNull();
            endpoint.Instance.Should().BeNull();
        }

        [Test]
        public void An_empty_data_source_is_missing()
        {
            var endpoint = SqlServerEndpointClassifier.Classify("   ", hasAlternateRouting: false);
            endpoint.Kind.Should().Be(ConnectionEndpointKinds.Missing);
            endpoint.Protocol.Should().Be(ConnectionEndpointProtocols.Default);
        }

        [Test]
        public void A_named_instance_with_an_explicit_port_reports_all_three_coordinates()
        {
            var endpoint = SqlServerEndpointClassifier.Classify(
                "dms-mssql\\SQLEXPRESS,1444",
                hasAlternateRouting: false
            );
            endpoint.Kind.Should().Be(ConnectionEndpointKinds.NamedInstance);
            endpoint.Host.Should().Be("dms-mssql");
            endpoint.Instance.Should().Be("SQLEXPRESS");
            endpoint.Port.Should().Be(1444);
        }

        [Test]
        public void A_bare_host_defaults_the_protocol_and_canonicalizes_the_port_to_1433()
        {
            var endpoint = SqlServerEndpointClassifier.Classify("dms-mssql", hasAlternateRouting: false);
            endpoint.Kind.Should().Be(ConnectionEndpointKinds.SingleHost);
            endpoint.Protocol.Should().Be(ConnectionEndpointProtocols.Default);
            endpoint.Host.Should().Be("dms-mssql");
            endpoint.Port.Should().Be(1433);
        }

        [Test]
        public void Alternate_routing_is_carried_through_regardless_of_shape()
        {
            SqlServerEndpointClassifier
                .Classify("dms-mssql,1433", hasAlternateRouting: true)
                .HasAlternateRouting.Should()
                .BeTrue();
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
