// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DmsConfigurationService.Backend.Mssql;
using EdFi.DmsConfigurationService.Backend.Postgresql;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// The composed host registers one engine validator and hands it out under both interfaces, so the
/// builder a stored connection string is rewritten through is the parser it was validated by.
/// </summary>
[TestFixture("postgresql", typeof(PostgresqlDataStoreConnectionStringValidator))]
[TestFixture("mssql", typeof(MssqlDataStoreConnectionStringValidator))]
public class Given_the_configured_datastore(string datastore, Type expectedValidator)
{
    private WebApplicationFactory<Program> _factory = null!;
    private IDataStoreConnectionStringValidator _validator = null!;
    private IDataStoreConnectionStringBuilderSource _builderSource = null!;
    private IDataStoreConnectionStringBuilderSource _builderSourceAgain = null!;

    [SetUp]
    public void Act()
    {
        // The datastore is read while the services are registered, so it goes through UseSetting.
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("AppSettings:Datastore", datastore);
        });
        using IServiceScope scope = _factory.Services.CreateScope();
        _validator = scope.ServiceProvider.GetRequiredService<IDataStoreConnectionStringValidator>();
        _builderSource = scope.ServiceProvider.GetRequiredService<IDataStoreConnectionStringBuilderSource>();
        _builderSourceAgain = _factory.Services.GetRequiredService<IDataStoreConnectionStringBuilderSource>();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public void It_registers_the_engine_validator() => _validator.Should().BeOfType(expectedValidator);

    [Test]
    public void It_hands_out_the_validator_instance_as_the_builder_source() =>
        _builderSource.Should().BeSameAs(_validator);

    [Test]
    public void It_hands_out_one_builder_source_for_the_life_of_the_host() =>
        _builderSourceAgain.Should().BeSameAs(_builderSource);

    [Test]
    public void It_registers_each_interface_once()
    {
        IServiceProvider services = _factory.Services;
        services.GetServices<IDataStoreConnectionStringValidator>().Should().ContainSingle();
        services.GetServices<IDataStoreConnectionStringBuilderSource>().Should().ContainSingle();
    }

    [Test]
    public void It_creates_the_engine_builder()
    {
        DbConnectionStringBuilder builder = _builderSource.CreateBuilder(
            "Password=${secret:prod/dms/ds-2026}"
        );

        builder
            .Should()
            .BeOfType(
                datastore == "postgresql"
                    ? typeof(NpgsqlConnectionStringBuilder)
                    : typeof(SqlConnectionStringBuilder)
            );
        builder["Password"].Should().Be("${secret:prod/dms/ds-2026}");
    }

    [Test]
    public void It_keeps_validating_with_the_same_engine()
    {
        // Host is a PostgreSQL keyword and not a SQL Server one, so the result names the engine.
        ConnectionStringValidationResult result = _validator.Validate("Host=db;Database=edfi");

        if (datastore == "postgresql")
        {
            result.Should().BeOfType<ConnectionStringValidationResult.Valid>();
        }
        else
        {
            result
                .Should()
                .BeOfType<ConnectionStringValidationResult.Invalid>()
                .Which.ErrorMessage.Should()
                .Be(DataStoreConnectionStringValidator.MalformedMessage);
        }
    }

    [Test]
    public void It_throws_from_the_builder_source_where_the_engine_rejects_the_text()
    {
        string rejected = datastore == "postgresql" ? "Initial Catalog=edfi" : "Host=db";

        Action act = () => _builderSource.CreateBuilder(rejected);

        act.Should().Throw<Exception>();
    }
}
