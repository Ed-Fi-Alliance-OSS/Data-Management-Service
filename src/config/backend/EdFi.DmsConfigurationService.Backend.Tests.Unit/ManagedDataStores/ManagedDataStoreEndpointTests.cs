// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.ManagedDataStores;

[TestFixture]
public class Given_Managed_Data_Store_Endpoints
{
    [TestCase("postgresql", "postgresql")]
    [TestCase("mssql", "sqlserver")]
    public void It_maps_provisioner_tokens_explicitly(string input, string expected) =>
        ManagedDataStoreEndpoint.CatalogProvider(input).Should().Be(expected);

    [TestCase("sqlserver")]
    [TestCase("MSSQL")]
    [TestCase("Postgresql")]
    [TestCase("")]
    public void It_refuses_unsupported_provider_tokens(string provider)
    {
        Action map = () => ManagedDataStoreEndpoint.CatalogProvider(provider);
        map.Should().Throw<ArgumentException>().WithMessage("Unsupported managed target provider.*");
    }

    [TestCase("Host=DB.example.test", "Host=db.example.test;Port=5432")]
    [TestCase("Host=db.example.test:5544", "Host=db.example.test;Port=5544")]
    [TestCase("Host=[2001:db8::1]:5544", "Host=2001:0db8:0:0:0:0:0:1;Port=5544")]
    [TestCase("Host=[2001:db8::1]", "Host=2001:db8::1")]
    public void It_normalizes_supported_postgresql_locators(string left, string right)
    {
        PostgresqlManagedEndpoint.TryParse(left, out var first).Should().BeTrue();
        PostgresqlManagedEndpoint.TryParse(right, out var second).Should().BeTrue();
        first.Should().Be(second);
        first!.Version.Should().Be(1);
        first.Provider.Should().Be("postgresql");
        first.MaintenanceDatabase.Should().Be("postgres");
    }

    [TestCase("Server=tcp:DB.example.test", "Data Source=tcp:db.example.test,1433")]
    [TestCase("Server=TCP:DB.example.test,5544", "Server=tcp:db.example.test,5544")]
    [TestCase("Server=tcp:DB.example.test\\SCHOOL", "Server=tcp:db.example.test\\school")]
    [TestCase("Server=tcp:[2001:db8::1],1433", "Server=tcp:[2001:0db8:0:0:0:0:0:1],1433")]
    [TestCase("Server=tcp:[2001:db8::1]", "Server=tcp:[2001:db8::1],1433")]
    public void It_normalizes_supported_sql_server_locators(string left, string right)
    {
        MssqlManagedEndpoint.TryParse(left, out var first).Should().BeTrue();
        MssqlManagedEndpoint.TryParse(right, out var second).Should().BeTrue();
        first.Should().Be(second);
        first!.Provider.Should().Be("mssql");
        first.MaintenanceDatabase.Should().Be("master");
    }

    [TestCase("Host=a,b")]
    [TestCase("Host=a,")]
    [TestCase("Host=")]
    [TestCase("Host=:5432")]
    [TestCase("Host=a:0")]
    [TestCase("Host=a:65536")]
    [TestCase("Host=a;Port=0")]
    [TestCase("Host=${secret}")]
    [TestCase("Host=db;UnknownKeyword=secret")]
    [TestCase("Host=[db.example.test]")]
    [TestCase("Host=[db.example.test]:5432")]
    [TestCase("Host=[127.0.0.1]")]
    [TestCase("Host=[127.0.0.1]:5432")]
    public void It_safely_refuses_unverifiable_postgresql_endpoints(string input)
    {
        PostgresqlManagedEndpoint.TryParse(input, out var endpoint).Should().BeFalse();
        endpoint.Should().BeNull();
    }

    [TestCase("Server=db")]
    [TestCase("Server=(local)")]
    [TestCase("Server=tcp:.")]
    [TestCase("Server=np:db")]
    [TestCase("Server=lpc:db")]
    [TestCase("Server=tcp:db\\instance,1433")]
    [TestCase("Server=tcp:db,0")]
    [TestCase("Server=tcp:db,65536")]
    [TestCase("Server=tcp:a,b")]
    [TestCase("Server=tcp:db;Failover Partner=other")]
    [TestCase("Server=tcp:db;MultiSubnetFailover=true")]
    [TestCase("Server=tcp:localhost\\SQLEXPRESS;Integrated Security=true;User Instance=true")]
    [TestCase("Server=tcp:db;ApplicationIntent=ReadOnly")]
    [TestCase("Server=tcp:db;AttachDbFilename=c:\\db.mdf")]
    [TestCase("Server=tcp:${secret}")]
    [TestCase("Server=tcp:db;UnknownKeyword=secret")]
    [TestCase("Server=tcp:[db.example.test]")]
    [TestCase("Server=tcp:[db.example.test],1433")]
    [TestCase("Server=tcp:[127.0.0.1]")]
    [TestCase("Server=tcp:[127.0.0.1],1433")]
    public void It_safely_refuses_unverifiable_sql_server_endpoints(string input)
    {
        MssqlManagedEndpoint.TryParse(input, out var endpoint).Should().BeFalse();
        endpoint.Should().BeNull();
    }

    [Test]
    public void It_preserves_socket_case_and_does_not_equate_host_aliases_ports_or_instances()
    {
        PostgresqlManagedEndpoint.TryParse("Host=/Var/Run/postgresql", out var socket).Should().BeTrue();
        socket!.Host.Should().Be("/Var/Run/postgresql");
        socket.Transport.Should().Be("unix");
        PostgresqlManagedEndpoint
            .TryParse("Host=/var/run/postgresql", out var differentSocket)
            .Should()
            .BeTrue();
        socket.Should().NotBe(differentSocket);
        MssqlManagedEndpoint.TryParse("Server=tcp:db,1433", out var first).Should().BeTrue();
        foreach (
            string source in new[]
            {
                "tcp:alias,1433",
                "tcp:db,5544",
                "tcp:db\\MSSQLSERVER",
                "tcp:db\\school",
            }
        )
        {
            MssqlManagedEndpoint.TryParse($"Server={source}", out var different).Should().BeTrue();
            first.Should().NotBe(different);
        }
    }

    [TestCase("/Var/Run/[postgresql]")]
    [TestCase("@Postgresql[Socket]")]
    public void It_preserves_brackets_inside_socket_paths(string path)
    {
        PostgresqlManagedEndpoint.TryParse($"Host={path}", out var endpoint).Should().BeTrue();
        endpoint!.Host.Should().Be(path);
        endpoint.Transport.Should().Be("unix");
    }

    [Test]
    public void It_excludes_credentials_target_database_application_name_and_timeouts()
    {
        PostgresqlManagedEndpoint
            .TryParse(
                "Host=db;Database=a;Username=alice;Password=first;Application Name=one;Timeout=3",
                out var pg
            )
            .Should()
            .BeTrue();
        PostgresqlManagedEndpoint
            .TryParse(
                "Host=db;Database=b;Username=bob;Password=second;Application Name=two;Timeout=7",
                out var rotatedPg
            )
            .Should()
            .BeTrue();
        pg.Should().Be(rotatedPg);
        pg!.CanonicalEncoding.Should().Be("[1,\"postgresql\",\"tcp\",\"db\",5432,null,\"postgres\"]");
        MssqlManagedEndpoint
            .TryParse(
                "Server=tcp:db;Database=a;User ID=alice;Password=first;Application Name=one;Connect Timeout=3",
                out var ms
            )
            .Should()
            .BeTrue();
        MssqlManagedEndpoint
            .TryParse(
                "Server=tcp:db;Database=b;User ID=bob;Password=second;Application Name=two;Connect Timeout=7",
                out var rotatedMs
            )
            .Should()
            .BeTrue();
        ms.Should().Be(rotatedMs);
        ms!.CanonicalEncoding.Should().Be("[1,\"mssql\",\"tcp\",\"db\",1433,null,\"master\"]");
    }
}
