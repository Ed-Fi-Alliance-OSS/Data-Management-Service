// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

/// <summary>
/// DMS-1529: SQL Server localizes the constraint-violation messages the SQL Server repositories
/// classify, so the Configuration Service pins the session language of its SQL Server connection to
/// us_english however the connection string is configured. These fixtures boot the real host and
/// read the database options that the repositories, the OpenIddict stores, the job sessions and the
/// lock sessions all open their connections from.
/// </summary>
public class MssqlSessionLanguageStartupTests
{
    private const string GermanSqlServerConnection =
        "Data Source=127.0.0.1,1;Initial Catalog=unreachable;User ID=none;Password=none;Connect Timeout=1;Current Language=Deutsch";

    private const string PostgresqlConnection =
        "host=127.0.0.1;port=1;database=unreachable;username=none;timeout=1";

    /// <summary>
    /// The datastore and the connection go through <c>UseSetting</c>, because the datastore is read
    /// while the services are registered.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(
        string datastore,
        string databaseConnection
    ) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("AppSettings:Datastore", datastore);
            builder.UseSetting("DatabaseSettings:DatabaseConnection", databaseConnection);
        });

    private static string ResolvedDatabaseConnection(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.DatabaseConnection;

    [TestFixture]
    public class Given_a_sql_server_connection_configured_in_german_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private SqlConnectionStringBuilder _resolved = null!;

        [SetUp]
        public void Act()
        {
            _factory = CreateFactory("mssql", GermanSqlServerConnection);
            _resolved = new SqlConnectionStringBuilder(ResolvedDatabaseConnection(_factory));
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_pins_the_session_language_to_us_english() =>
            _resolved.CurrentLanguage.Should().Be("us_english");

        [Test]
        public void It_keeps_the_rest_of_the_configured_connection()
        {
            _resolved.DataSource.Should().Be("127.0.0.1,1");
            _resolved.InitialCatalog.Should().Be("unreachable");
            _resolved.UserID.Should().Be("none");
            _resolved.Password.Should().Be("none");
            _resolved.ConnectTimeout.Should().Be(1);
        }
    }

    [TestFixture]
    public class Given_a_postgresql_connection_at_startup
    {
        private WebApplicationFactory<Program> _factory = null!;
        private string _resolved = null!;

        [SetUp]
        public void Act()
        {
            _factory = CreateFactory("postgresql", PostgresqlConnection);
            _resolved = ResolvedDatabaseConnection(_factory);
        }

        [TearDown]
        public void TearDown() => _factory.Dispose();

        [Test]
        public void It_leaves_the_connection_string_unchanged() =>
            _resolved.Should().Be(PostgresqlConnection);
    }
}
