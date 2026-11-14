// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Repositories;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.ManagedDataStores;

[TestFixture]
public class Given_managed_endpoint_canonical_acceptance
{
    private static bool Accepts(ManagedDataStoreEndpoint endpoint)
    {
        ProtectedDatabaseNames protectedNames = ProtectedDatabaseNames.Create("cms", []);
        return new ManagedDataStoreCreateCommand(
            "Store",
            DmsDatabaseTemplate.Minimal,
            ManagedDataStoreNameFormatter.Format("Store", DmsDatabaseTemplate.Minimal, protectedNames),
            Guid.NewGuid(),
            endpoint
        ).IsValid(protectedNames);
    }

    [Test]
    public void It_rejects_noncanonical_or_malformed_network_hosts(
        [Values("postgresql", "mssql")] string provider,
        [Values(
            "DB.EXAMPLE",
            "2001:0db8:0:0:0:0:0:1",
            "[2001:db8::1]",
            "127.1",
            "127.000.000.001",
            "db.example,other",
            "db.example/path",
            "db.example:5432",
            "db.example,1433",
            "db.example\\school",
            "db_example",
            "déb.example",
            "db example",
            "/var/run/postgresql",
            "@socket",
            "",
            " db.example",
            "db.example\n"
        )]
            string host
    )
    {
        Accepts(
                new(
                    provider,
                    "tcp",
                    host,
                    provider == "postgresql" ? 5432 : 1433,
                    null,
                    provider == "postgresql" ? "postgres" : "master"
                )
            )
            .Should()
            .BeFalse();
    }

    [TestCase("SCHOOL")]
    [TestCase("School_1")]
    [TestCase("school,1433")]
    [TestCase("school/path")]
    [TestCase("school\\nested")]
    [TestCase("school name")]
    [TestCase("")]
    public void It_rejects_noncanonical_or_malformed_named_instances(string instance) =>
        Accepts(new("mssql", "tcp", "db.example", null, instance, "master")).Should().BeFalse();

    [TestCase("relative/path")]
    [TestCase("db.example")]
    [TestCase("/var/run,other")]
    [TestCase(" /var/run")]
    [TestCase("@socket\t")]
    public void It_rejects_socket_descriptors_the_provider_cannot_produce(string host) =>
        Accepts(new("postgresql", "unix", host, 5432, null, "postgres")).Should().BeFalse();

    [TestCase("Host=DB.example.test")]
    [TestCase("Host=db.example.test:5544")]
    [TestCase("Host=[2001:db8::1]:5544")]
    [TestCase("Host=2001:0db8:0:0:0:0:0:1;Port=5544")]
    [TestCase("Host=[2001:db8::1]")]
    [TestCase("Host=127.1")]
    [TestCase("Host=127.000.000.001")]
    [TestCase("Host=db.example.")]
    [TestCase("Host=localhost")]
    [TestCase("Host=/Var/Run/postgresql")]
    [TestCase("Host=/var/run/postgresql")]
    [TestCase("Host=/Var/Run/[postgresql]")]
    [TestCase("Host=@Postgresql[Socket]")]
    [TestCase("Host=/Var/Run/socket:5432")]
    [TestCase("Host=@Socket:5544")]
    [TestCase("Host=/var/run/socket path")]
    public void It_accepts_the_existing_postgresql_parsers_canonical_output(string connection)
    {
        PostgresqlManagedEndpoint.TryParse(connection, out var endpoint).Should().BeTrue();
        Accepts(endpoint!).Should().BeTrue();
    }

    [TestCase("Server=tcp:DB.example.test")]
    [TestCase("Server=TCP:DB.example.test,5544")]
    [TestCase("Server=tcp:DB.example.test\\SCHOOL")]
    [TestCase("Server=tcp:DB.example.test\\School_1-test")]
    [TestCase("Server=tcp:[2001:db8::1],1433")]
    [TestCase("Server=tcp:[2001:0db8:0:0:0:0:0:1],1433")]
    [TestCase("Server=tcp:2001:db8::1")]
    [TestCase("Server=tcp:127.1")]
    [TestCase("Server=tcp:127.000.000.001")]
    [TestCase("Server=tcp:db.example.")]
    [TestCase("Server=tcp:localhost")]
    public void It_accepts_the_existing_sql_server_parsers_canonical_output(string connection)
    {
        MssqlManagedEndpoint.TryParse(connection, out var endpoint).Should().BeTrue();
        Accepts(endpoint!).Should().BeTrue();
    }

    [TestCase("/Var/Run/postgresql", "/var/run/postgresql")]
    [TestCase("@Postgresql[Socket]", "@postgresql[Socket]")]
    public void It_keeps_case_sensitive_socket_paths_distinct(string left, string right)
    {
        PostgresqlManagedEndpoint.TryParse($"Host={left}", out var first).Should().BeTrue();
        PostgresqlManagedEndpoint.TryParse($"Host={right}", out var second).Should().BeTrue();
        Accepts(first!).Should().BeTrue();
        Accepts(second!).Should().BeTrue();
        first!.CanonicalEncoding.Should().NotBe(second!.CanonicalEncoding);
    }
}
