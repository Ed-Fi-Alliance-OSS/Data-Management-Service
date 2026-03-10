// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[Parallelizable]
public class WebApplicationBuilderExtensionsTests
{
    private static ServiceProvider CreateServices(string datastore)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });

        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AppSettings:Datastore"] = datastore,
                ["AppSettings:QueryHandler"] = "postgresql",
                ["AppSettings:MaskRequestBodyInLogs"] = "false",
                ["ConfigurationServiceSettings:BaseUrl"] = "https://example.org",
                ["ConfigurationServiceSettings:ClientId"] = "client-id",
                ["ConfigurationServiceSettings:ClientSecret"] = "client-secret",
                ["ConfigurationServiceSettings:Scope"] = "scope",
            }
        );

        builder.AddServices();

        return builder.Services.BuildServiceProvider();
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Postgresql_Datastore : WebApplicationBuilderExtensionsTests
    {
        [Test]
        public void It_resolves_the_postgresql_fingerprint_reader()
        {
            using var serviceProvider = CreateServices("postgresql");

            var fingerprintReader = serviceProvider.GetRequiredService<IDatabaseFingerprintReader>();

            fingerprintReader.Should().BeOfType<PostgresqlDatabaseFingerprintReader>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Mssql_Datastore : WebApplicationBuilderExtensionsTests
    {
        [Test]
        public void It_resolves_the_mssql_fingerprint_reader()
        {
            using var serviceProvider = CreateServices("mssql");

            var fingerprintReader = serviceProvider.GetRequiredService<IDatabaseFingerprintReader>();

            fingerprintReader.Should().BeOfType<MssqlDatabaseFingerprintReader>();
        }
    }
}
