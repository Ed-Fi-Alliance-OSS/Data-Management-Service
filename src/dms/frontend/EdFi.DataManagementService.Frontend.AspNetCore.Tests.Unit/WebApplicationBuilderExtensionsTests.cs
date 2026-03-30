// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using EdFi.DataManagementService.Old.Postgresql;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using CoreAppSettings = EdFi.DataManagementService.Core.Configuration.AppSettings;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[Parallelizable]
public class WebApplicationBuilderExtensionsTests
{
    private static ServiceProvider CreateServices(
        string datastore,
        Dictionary<string, string?>? additionalConfiguration = null
    )
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });

        builder.Configuration.Sources.Clear();
        var configuration = new Dictionary<string, string?>
        {
            ["AppSettings:Datastore"] = datastore,
            ["AppSettings:QueryHandler"] = "postgresql",
            ["AppSettings:MaskRequestBodyInLogs"] = "false",
            ["ConfigurationServiceSettings:BaseUrl"] = "https://example.org",
            ["ConfigurationServiceSettings:ClientId"] = "client-id",
            ["ConfigurationServiceSettings:ClientSecret"] = "client-secret",
            ["ConfigurationServiceSettings:Scope"] = "scope",
        };

        if (additionalConfiguration is not null)
        {
            foreach (var configurationEntry in additionalConfiguration)
            {
                configuration[configurationEntry.Key] = configurationEntry.Value;
            }
        }

        builder.Configuration.AddInMemoryCollection(configuration);

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

        [Test]
        public void It_keeps_only_the_legacy_postgresql_repository_surface_when_relational_backend_is_disabled()
        {
            using var serviceProvider = CreateServices("postgresql");
            using var scope = serviceProvider.CreateScope();

            scope
                .ServiceProvider.GetServices<IDocumentStoreRepository>()
                .Should()
                .ContainSingle()
                .Which.Should()
                .BeOfType<PostgresqlDocumentStoreRepository>();
            scope
                .ServiceProvider.GetServices<IQueryHandler>()
                .Should()
                .ContainSingle()
                .Which.Should()
                .BeOfType<PostgresqlDocumentStoreRepository>();
        }

        [Test]
        public void It_does_not_register_relational_reference_resolution_services_by_default()
        {
            using var serviceProvider = CreateServices("postgresql");
            using var scope = serviceProvider.CreateScope();

            scope.ServiceProvider.GetService<IReferenceResolver>().Should().BeNull();
            scope.ServiceProvider.GetService<IReferenceResolverAdapterFactory>().Should().BeNull();
            scope.ServiceProvider.GetService<IReferenceResolverAdapter>().Should().BeNull();
            scope.ServiceProvider.GetService<IRelationalCommandExecutor>().Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Postgresql_Datastore_With_Relational_Backend_Enabled
        : WebApplicationBuilderExtensionsTests
    {
        [Test]
        public void It_replaces_the_legacy_repository_surface_with_the_relational_repository()
        {
            using var serviceProvider = CreateServices(
                "postgresql",
                new Dictionary<string, string?> { ["AppSettings:UseRelationalBackend"] = "true" }
            );
            using var scope = serviceProvider.CreateScope();

            scope
                .ServiceProvider.GetServices<IDocumentStoreRepository>()
                .Should()
                .ContainSingle()
                .Which.Should()
                .BeOfType<RelationalDocumentStoreRepository>();
            scope
                .ServiceProvider.GetServices<IQueryHandler>()
                .Should()
                .ContainSingle()
                .Which.Should()
                .BeOfType<RelationalDocumentStoreRepository>();
        }

        [Test]
        public void It_registers_the_postgresql_relational_runtime_composition_surface()
        {
            using var serviceProvider = CreateServices(
                "postgresql",
                new Dictionary<string, string?> { ["AppSettings:UseRelationalBackend"] = "true" }
            );
            using var scope = serviceProvider.CreateScope();

            scope
                .ServiceProvider.GetRequiredService<IReferenceResolver>()
                .Should()
                .BeOfType<ReferenceResolver>();
            scope
                .ServiceProvider.GetRequiredService<IReferenceResolverAdapterFactory>()
                .Should()
                .BeOfType<PostgresqlReferenceResolverAdapterFactory>();
            scope
                .ServiceProvider.GetRequiredService<IReferenceResolverAdapter>()
                .Should()
                .BeOfType<PostgresqlReferenceResolverAdapter>();
            scope
                .ServiceProvider.GetRequiredService<IRelationalCommandExecutor>()
                .Should()
                .BeOfType<PostgresqlRelationalCommandExecutor>();
            scope
                .ServiceProvider.GetRequiredService<IMappingSetProvider>()
                .Should()
                .BeOfType<MappingSetProvider>();
            scope
                .ServiceProvider.GetServices<IRuntimeMappingSetCompiler>()
                .Should()
                .ContainSingle()
                .Which.Dialect.Should()
                .Be(SqlDialect.Pgsql);
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

    [TestFixture]
    [Parallelizable]
    public class Given_An_Mssql_Datastore_With_Relational_Backend_Enabled
        : WebApplicationBuilderExtensionsTests
    {
        [Test]
        public void It_registers_the_mssql_relational_runtime_composition_surface()
        {
            using var serviceProvider = CreateServices(
                "mssql",
                new Dictionary<string, string?> { ["AppSettings:UseRelationalBackend"] = "true" }
            );
            using var scope = serviceProvider.CreateScope();

            scope
                .ServiceProvider.GetServices<IDocumentStoreRepository>()
                .Should()
                .ContainSingle()
                .Which.Should()
                .BeOfType<RelationalDocumentStoreRepository>();
            scope
                .ServiceProvider.GetServices<IQueryHandler>()
                .Should()
                .ContainSingle()
                .Which.Should()
                .BeOfType<RelationalDocumentStoreRepository>();
            scope
                .ServiceProvider.GetRequiredService<IReferenceResolver>()
                .Should()
                .BeOfType<ReferenceResolver>();
            scope
                .ServiceProvider.GetRequiredService<IReferenceResolverAdapterFactory>()
                .Should()
                .Match<IReferenceResolverAdapterFactory>(factory =>
                    factory.GetType().Name == "MssqlReferenceResolverAdapterFactory"
                );
            scope
                .ServiceProvider.GetRequiredService<IReferenceResolverAdapter>()
                .Should()
                .Match<IReferenceResolverAdapter>(adapter =>
                    adapter.GetType().Name == "MssqlReferenceResolverAdapter"
                );
            scope
                .ServiceProvider.GetRequiredService<IRelationalCommandExecutor>()
                .Should()
                .Match<IRelationalCommandExecutor>(executor =>
                    executor.GetType().Name == "MssqlRelationalCommandExecutor"
                );
            scope
                .ServiceProvider.GetRequiredService<IMappingSetProvider>()
                .Should()
                .BeOfType<MappingSetProvider>();
            scope
                .ServiceProvider.GetServices<IRuntimeMappingSetCompiler>()
                .Should()
                .ContainSingle()
                .Which.Dialect.Should()
                .Be(SqlDialect.Mssql);
            scope
                .ServiceProvider.GetRequiredService<IDatabaseFingerprintReader>()
                .Should()
                .BeOfType<MssqlDatabaseFingerprintReader>();
            scope
                .ServiceProvider.GetRequiredService<IResourceKeyRowReader>()
                .Should()
                .BeOfType<MssqlResourceKeyRowReader>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Core_App_Settings_Are_Bound_For_Startup : WebApplicationBuilderExtensionsTests
    {
        [Test]
        public void It_defaults_validate_provisioned_mappings_on_startup_to_false_when_unset()
        {
            using var serviceProvider = CreateServices("postgresql");

            var appSettings = serviceProvider.GetRequiredService<IOptions<CoreAppSettings>>().Value;

            appSettings.ValidateProvisionedMappingsOnStartup.Should().BeFalse();
            appSettings.UseRelationalBackend.Should().BeFalse();
        }

        [Test]
        public void It_allows_validate_provisioned_mappings_on_startup_to_be_enabled_explicitly()
        {
            using var serviceProvider = CreateServices(
                "postgresql",
                new Dictionary<string, string?>
                {
                    ["AppSettings:ValidateProvisionedMappingsOnStartup"] = "true",
                }
            );

            var appSettings = serviceProvider.GetRequiredService<IOptions<CoreAppSettings>>().Value;

            appSettings.ValidateProvisionedMappingsOnStartup.Should().BeTrue();
            appSettings.UseRelationalBackend.Should().BeFalse();
        }
    }
}
