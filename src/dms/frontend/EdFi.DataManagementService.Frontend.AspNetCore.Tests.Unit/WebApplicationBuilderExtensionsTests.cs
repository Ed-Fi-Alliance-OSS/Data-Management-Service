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
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using CoreAppSettings = EdFi.DataManagementService.Core.Configuration.AppSettings;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[Parallelizable]
public class WebApplicationBuilderExtensionsTests
{
    private static IServiceCollection CreateServiceCollection(
        string datastore,
        Dictionary<string, string?>? additionalConfiguration = null
    )
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });

        builder.Configuration.Sources.Clear();
        var configuration = new Dictionary<string, string?>
        {
            ["AppSettings:Datastore"] = datastore,
            ["AppSettings:MaskRequestBodyInLogs"] = "false",
            ["ConfigurationServiceSettings:BaseUrl"] = "https://example.org",
            ["ConfigurationServiceSettings:ClientId"] = "client-id",
            ["ConfigurationServiceSettings:ClientSecret"] = "client-secret",
            ["ConfigurationServiceSettings:Scope"] = "scope",
            ["ConfigurationServiceSettings:EncryptionKey"] =
                "TestEncryptionKey123456789012345678901234567890",
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

        return builder.Services;
    }

    private static ServiceProvider CreateServices(
        string datastore,
        Dictionary<string, string?>? additionalConfiguration = null
    ) => CreateServiceCollection(datastore, additionalConfiguration).BuildServiceProvider();

    private static void AssertDirectRelationalRepositoryRegistrations(IServiceCollection services)
    {
        AssertDirectScopedRepositoryRegistration<IDocumentStoreRepository>(services);
        AssertDirectScopedRepositoryRegistration<IQueryHandler>(services);
        services
            .Should()
            .NotContain(descriptor => descriptor.ServiceType == typeof(RelationalDocumentStoreRepository));
    }

    private static void AssertDirectScopedRepositoryRegistration<TService>(IServiceCollection services)
    {
        var descriptor = services.Single(descriptor => descriptor.ServiceType == typeof(TService));

        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().Be(typeof(RelationalDocumentStoreRepository));
        descriptor.ImplementationFactory.Should().BeNull();
        descriptor.ImplementationInstance.Should().BeNull();
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
        public void It_uses_direct_typed_relational_repository_registrations()
        {
            var services = CreateServiceCollection("postgresql");

            AssertDirectRelationalRepositoryRegistrations(services);
        }

        [Test]
        public void It_registers_the_relational_repository_surface()
        {
            using var serviceProvider = CreateServices("postgresql");
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
        public void It_replaces_the_core_backend_mapping_initializer_with_the_relational_initializer()
        {
            using var serviceProvider = CreateServices("postgresql");

            serviceProvider
                .GetServices<IBackendMappingInitializer>()
                .Should()
                .ContainSingle()
                .Which.Should()
                .BeOfType<RelationalBackendMappingInitializer>();
        }

        [Test]
        public void It_registers_the_postgresql_relational_runtime_composition_surface()
        {
            using var serviceProvider = CreateServices("postgresql");
            using var scope = serviceProvider.CreateScope();

            scope
                .ServiceProvider.GetRequiredService<IReferenceResolver>()
                .Should()
                .BeOfType<ReferenceResolver>();
            scope
                .ServiceProvider.GetRequiredService<IRelationalWriteFlattener>()
                .Should()
                .BeOfType<RelationalWriteFlattener>();
            scope
                .ServiceProvider.GetRequiredService<IRelationalWriteCurrentStateLoader>()
                .Should()
                .BeOfType<RelationalWriteCurrentStateLoader>();
            scope
                .ServiceProvider.GetRequiredService<IRelationalWriteFreshnessChecker>()
                .Should()
                .BeOfType<RelationalWriteFreshnessChecker>();
            scope
                .ServiceProvider.GetRequiredService<IRelationalWritePersister>()
                .Should()
                .BeOfType<RelationalWriteNoProfilePersister>();
            scope
                .ServiceProvider.GetRequiredService<IRelationalWriteTargetLookupService>()
                .Should()
                .BeOfType<RelationalWriteTargetLookupService>();
            scope
                .ServiceProvider.GetRequiredService<IRelationalWriteTargetLookupResolver>()
                .Should()
                .BeOfType<RelationalWriteTargetLookupResolver>();
            scope
                .ServiceProvider.GetRequiredService<IRelationalWriteExecutor>()
                .Should()
                .BeOfType<DefaultRelationalWriteExecutor>();
            scope
                .ServiceProvider.GetRequiredService<IRelationalWriteSessionFactory>()
                .Should()
                .BeOfType<PostgresqlRelationalWriteSessionFactory>();
            scope
                .ServiceProvider.GetRequiredService<IDocumentHydrator>()
                .Should()
                .BeOfType<PostgresqlDocumentHydrator>();
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
                .ServiceProvider.GetRequiredService<RelationalEdOrgAuthorizationElementResolutionCache>()
                .Should()
                .NotBeNull();
            scope
                .ServiceProvider.GetRequiredService<RelationalEdOrgAuthorizationSubjectSelector>()
                .Should()
                .NotBeNull();
            scope
                .ServiceProvider.GetRequiredService<IMappingSetProvider>()
                .Should()
                .BeOfType<MappingSetProvider>();
            scope.ServiceProvider.GetRequiredService<MappingSetCompiler>().Should().NotBeNull();
            scope
                .ServiceProvider.GetRequiredService<IMappingPackStore>()
                .Should()
                .BeOfType<NoOpMappingPackStore>();
            scope
                .ServiceProvider.GetServices<IRuntimeMappingSetCompiler>()
                .Should()
                .ContainSingle()
                .Which.Dialect.Should()
                .Be(SqlDialect.Pgsql);
        }

        [Test]
        public void It_registers_only_the_postgresql_relational_token_info_lookup()
        {
            using var serviceProvider = CreateServices("postgresql");
            using var scope = serviceProvider.CreateScope();

            scope
                .ServiceProvider.GetServices<IRelationalTokenInfoEducationOrganizationLookup>()
                .Should()
                .ContainSingle()
                .Which.Should()
                .BeOfType<PostgresqlTokenInfoEducationOrganizationLookup>();
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

        [Test]
        public void It_uses_direct_typed_relational_repository_registrations()
        {
            var services = CreateServiceCollection("mssql");

            AssertDirectRelationalRepositoryRegistrations(services);
        }

        [Test]
        public void It_registers_the_mssql_relational_runtime_composition_surface()
        {
            using var serviceProvider = CreateServices("mssql");
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
                .ServiceProvider.GetRequiredService<IRelationalWriteFlattener>()
                .Should()
                .BeOfType<RelationalWriteFlattener>();
            scope
                .ServiceProvider.GetRequiredService<IRelationalWriteCurrentStateLoader>()
                .Should()
                .BeOfType<RelationalWriteCurrentStateLoader>();
            scope
                .ServiceProvider.GetRequiredService<IRelationalWriteFreshnessChecker>()
                .Should()
                .BeOfType<RelationalWriteFreshnessChecker>();
            scope
                .ServiceProvider.GetRequiredService<IRelationalWritePersister>()
                .Should()
                .BeOfType<RelationalWriteNoProfilePersister>();
            scope
                .ServiceProvider.GetRequiredService<IRelationalWriteTargetLookupService>()
                .Should()
                .BeOfType<RelationalWriteTargetLookupService>();
            scope
                .ServiceProvider.GetRequiredService<IRelationalWriteTargetLookupResolver>()
                .Should()
                .BeOfType<RelationalWriteTargetLookupResolver>();
            scope
                .ServiceProvider.GetRequiredService<IRelationalWriteExecutor>()
                .Should()
                .BeOfType<DefaultRelationalWriteExecutor>();
            scope
                .ServiceProvider.GetRequiredService<IRelationalWriteSessionFactory>()
                .Should()
                .Match<IRelationalWriteSessionFactory>(factory =>
                    factory.GetType().Name == "MssqlRelationalWriteSessionFactory"
                );
            scope
                .ServiceProvider.GetRequiredService<IDocumentHydrator>()
                .Should()
                .Match<IDocumentHydrator>(hydrator => hydrator.GetType().Name == "MssqlDocumentHydrator");
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
                .ServiceProvider.GetRequiredService<RelationalEdOrgAuthorizationElementResolutionCache>()
                .Should()
                .NotBeNull();
            scope
                .ServiceProvider.GetRequiredService<RelationalEdOrgAuthorizationSubjectSelector>()
                .Should()
                .NotBeNull();
            scope
                .ServiceProvider.GetRequiredService<IMappingSetProvider>()
                .Should()
                .BeOfType<MappingSetProvider>();
            scope.ServiceProvider.GetRequiredService<MappingSetCompiler>().Should().NotBeNull();
            scope
                .ServiceProvider.GetRequiredService<IMappingPackStore>()
                .Should()
                .BeOfType<NoOpMappingPackStore>();
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

        [Test]
        public void It_registers_the_mssql_relational_token_info_lookup()
        {
            using var serviceProvider = CreateServices("mssql");
            using var scope = serviceProvider.CreateScope();

            scope
                .ServiceProvider.GetServices<IRelationalTokenInfoEducationOrganizationLookup>()
                .Should()
                .ContainSingle()
                .Which.Should()
                .BeOfType<MssqlTokenInfoEducationOrganizationLookup>();
        }

        [Test]
        public void It_replaces_the_core_backend_mapping_initializer_with_the_relational_initializer()
        {
            using var serviceProvider = CreateServices("mssql");

            serviceProvider
                .GetServices<IBackendMappingInitializer>()
                .Should()
                .ContainSingle()
                .Which.Should()
                .BeOfType<RelationalBackendMappingInitializer>();
        }
    }
}
