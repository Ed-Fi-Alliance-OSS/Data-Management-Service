// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[Parallelizable]
public class WebApplicationBuilderExtensionsTests
{
    private static IServiceCollection CreateServiceCollection(
        string datastore,
        Dictionary<string, string?>? additionalConfiguration = null,
        Action<IServiceCollection>? configureServicesBeforeAddServices = null
    )
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });

        builder.Configuration.Sources.Clear();
        var configuration = new Dictionary<string, string?>
        {
            ["AppSettings:Datastore"] = datastore,
            ["AppSettings:MaskRequestBodyInLogs"] = "false",
            // This helper is the valid baseline; focused tests override a single setting when they
            // intend it to be invalid.
            ["AppSettings:MaximumPageSize"] = "500",
            ["AppSettings:DefaultPartitionCount"] = "10",
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
        configureServicesBeforeAddServices?.Invoke(builder.Services);

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

    private static void AssertSingleDownstreamPublicationHistoryProvider<TImplementation>(
        IServiceCollection services
    )
        where TImplementation : class, IDocumentCacheDownstreamPublicationHistoryProvider
    {
        ServiceDescriptor descriptor = services
            .Should()
            .ContainSingle(descriptor =>
                descriptor.ServiceType == typeof(IDocumentCacheDownstreamPublicationHistoryProvider)
            )
            .Subject;

        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType.Should().Be(typeof(TImplementation));
        descriptor.ImplementationFactory.Should().BeNull();
        descriptor.ImplementationInstance.Should().BeNull();
    }

    [TestFixture]
    [Parallelizable]
    public class Given_DocumentCache_Configuration : WebApplicationBuilderExtensionsTests
    {
        [Test]
        public void It_registers_DocumentCacheOptions_with_startup_validation()
        {
            IServiceCollection services = CreateServiceCollection("postgresql");

            services
                .Should()
                .ContainSingle(descriptor =>
                    descriptor.ServiceType == typeof(IValidateOptions<DocumentCacheOptions>)
                    && descriptor.ImplementationType == typeof(DocumentCacheOptionsValidator)
                );
            services
                .Should()
                .Contain(descriptor =>
                    descriptor.ServiceType.FullName == "Microsoft.Extensions.Options.IStartupValidator"
                );
        }

        [Test]
        public void It_binds_DocumentCacheOptions_from_DataManagement_DocumentCache()
        {
            using ServiceProvider serviceProvider = CreateServices(
                "postgresql",
                new Dictionary<string, string?>
                {
                    ["DataManagement:DocumentCache:Targets:0:TenantKey"] = "TenantA",
                    ["DataManagement:DocumentCache:Targets:0:DataStoreId"] = "7",
                    ["DataManagement:DocumentCache:ReadAcceleration:Enabled"] = "true",
                    ["DataManagement:DocumentCache:ReadAcceleration:DirectFillTimeout"] = "00:00:00.125",
                    ["DataManagement:DocumentCache:Projector:PollInterval"] = "00:00:07",
                    ["DataManagement:DocumentCache:Projector:PageSize"] = "25",
                    ["DataManagement:DocumentCache:Projector:MaxConcurrentTargets"] = "4",
                    ["DataManagement:DocumentCache:Projector:FailureBackoff"] = "00:01:15",
                    ["DataManagement:DocumentCache:Projector:BaselineHighWaterMark"] = "2500",
                    ["DataManagement:DocumentCache:Administration:WorkflowTimeout"] = "12:00:00",
                }
            );

            DocumentCacheOptions options = serviceProvider
                .GetRequiredService<IOptions<DocumentCacheOptions>>()
                .Value;

            options.Targets.Should().ContainSingle();
            options.Targets[0].TenantKey.Should().Be("TenantA");
            options.Targets[0].DataStoreId.Should().Be(7);
            options.ReadAcceleration.Enabled.Should().BeTrue();
            options.ReadAcceleration.DirectFillTimeout.Should().Be(TimeSpan.FromMilliseconds(125));
            options.Projector.PollInterval.Should().Be(TimeSpan.FromSeconds(7));
            options.Projector.PageSize.Should().Be(25);
            options.Projector.MaxConcurrentTargets.Should().Be(4);
            options
                .Projector.FailureBackoff.Should()
                .Be(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(15)));
            options.Projector.BaselineHighWaterMark.Should().Be(2500);
            options.Administration.WorkflowTimeout.Should().Be(TimeSpan.FromHours(12));
        }

        [Test]
        public void It_fails_options_validation_for_malformed_DocumentCacheOptions()
        {
            using ServiceProvider serviceProvider = CreateServices(
                "postgresql",
                new Dictionary<string, string?> { ["DataManagement:DocumentCache:Projector:PageSize"] = "0" }
            );

            Action act = () => serviceProvider.GetRequiredService<IStartupValidator>().Validate();

            act.Should()
                .Throw<OptionsValidationException>()
                .Which.Failures.Should()
                .Contain("Projector:PageSize must be positive.");
        }

        [Test]
        public void It_creates_sanitized_startup_diagnostics()
        {
            DocumentCacheOptions options = new()
            {
                Targets =
                [
                    new DocumentCacheTargetOptions { TenantKey = "TenantName", DataStoreId = 1 },
                    new DocumentCacheTargetOptions { TenantKey = "", DataStoreId = 2 },
                ],
                ReadAcceleration = new DocumentCacheReadAccelerationOptions { Enabled = true },
            };

            DocumentCacheStartupDiagnosticSnapshot snapshot = DocumentCacheStartupDiagnostics.CreateSnapshot(
                options
            );

            snapshot.TargetCount.Should().Be(2);
            snapshot.ConfiguredTargets.Should().Equal("TenantName:1", "(default):2");
            snapshot.ReadAccelerationEnabled.Should().BeTrue();
            snapshot.DirectFillTimeout.Should().Be(TimeSpan.FromMilliseconds(250));
            snapshot.PollInterval.Should().Be(TimeSpan.FromSeconds(5));
            snapshot.PageSize.Should().Be(100);
            snapshot.MaxConcurrentTargets.Should().Be(2);
            snapshot.FailureBackoff.Should().Be(TimeSpan.FromSeconds(30));
            snapshot.BaselineHighWaterMark.Should().Be(1000);
            snapshot.WorkflowTimeout.Should().Be(TimeSpan.FromHours(24));
        }

        [Test]
        [Category("DocumentCacheServiceRegistration")]
        public void It_registers_the_DocumentCache_projection_supervisor_as_the_only_hosted_service()
        {
            using ServiceProvider serviceProvider = CreateServices("postgresql");

            DocumentCacheProjectionSupervisor supervisor =
                serviceProvider.GetRequiredService<DocumentCacheProjectionSupervisor>();

            serviceProvider
                .GetRequiredService<IDocumentCacheProjectionSupervisor>()
                .Should()
                .BeSameAs(supervisor);
            serviceProvider
                .GetServices<IHostedService>()
                .OfType<DocumentCacheProjectionSupervisor>()
                .Should()
                .ContainSingle()
                .Which.Should()
                .BeSameAs(supervisor);
        }

        [Test]
        [Category("DocumentCacheServiceRegistration")]
        public void It_registers_the_data_store_provider_with_DocumentCache_refresh_notification()
        {
            using ServiceProvider serviceProvider = CreateServices("postgresql");

            serviceProvider
                .GetRequiredService<IDataStoreProvider>()
                .Should()
                .BeOfType<DocumentCacheRefreshNotifyingDataStoreProvider>();
            serviceProvider.GetRequiredService<ConfigurationServiceDataStoreProvider>().Should().NotBeNull();
        }

        [Test]
        [Category("DocumentCacheServiceRegistration")]
        public void It_registers_the_DocumentCache_projection_observation_provider_and_sink()
        {
            using ServiceProvider serviceProvider = CreateServices("postgresql");

            DocumentCacheProjectionObservationStore store =
                serviceProvider.GetRequiredService<DocumentCacheProjectionObservationStore>();

            serviceProvider
                .GetRequiredService<IDocumentCacheProjectionObservationProvider>()
                .Should()
                .BeSameAs(store);
            serviceProvider
                .GetRequiredService<IDocumentCacheProjectionObservationSink>()
                .Should()
                .BeSameAs(store);
        }
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
        public void It_resolves_the_postgresql_document_cache_physical_source_fingerprint_reader()
        {
            using var serviceProvider = CreateServices("postgresql");

            var fingerprintReader =
                serviceProvider.GetRequiredService<IDocumentCachePhysicalSourceFingerprintReader>();

            fingerprintReader.Should().BeOfType<PostgresqlDocumentCachePhysicalSourceFingerprintReader>();
        }

        [Test]
        public void It_resolves_the_postgresql_document_cache_provider_prerequisite_validator()
        {
            using var serviceProvider = CreateServices("postgresql");

            var validator = serviceProvider.GetRequiredService<IDocumentCacheProviderPrerequisiteValidator>();

            validator.Should().BeOfType<PostgresqlDocumentCacheProviderPrerequisiteValidator>();
        }

        [Test]
        [Category("DocumentCacheTargetContext")]
        public void It_resolves_the_DocumentCacheTargetContext_postgresql_lifecycle_reader()
        {
            using var serviceProvider = CreateServices("postgresql");

            var reader = serviceProvider.GetRequiredService<IDocumentCacheLifecycleReader>();

            reader.Should().BeOfType<PostgresqlDocumentCacheLifecycleReader>();
        }

        [Test]
        [Category("DocumentCacheTargetContext")]
        public void It_resolves_the_DocumentCacheTargetContext_builder_with_postgresql_provider()
        {
            using var serviceProvider = CreateServices("postgresql");

            serviceProvider
                .GetRequiredService<IDocumentCacheTargetContextBuilder>()
                .Should()
                .BeOfType<DocumentCacheTargetContextBuilder>();
            serviceProvider
                .GetRequiredService<DocumentCacheProcessProviderToken>()
                .ProviderToken.Should()
                .Be(RelationalProviderToken.Postgresql);
        }

        [Test]
        [Category("DocumentCacheTargetRegistry")]
        public void It_resolves_the_DocumentCacheTarget_registry_with_postgresql_provider()
        {
            using var serviceProvider = CreateServices("postgresql");

            serviceProvider
                .GetRequiredService<IDocumentCacheTargetRegistry>()
                .Should()
                .BeOfType<DocumentCacheTargetRegistry>();
        }

        [Test]
        [Category("DocumentCacheDiagnostics")]
        public void It_resolves_the_DocumentCache_diagnostic_snapshot_provider_with_postgresql_provider()
        {
            using var serviceProvider = CreateServices("postgresql");

            serviceProvider
                .GetRequiredService<IDocumentCacheDiagnosticSnapshotProvider>()
                .Should()
                .BeOfType<DocumentCacheDiagnosticSnapshotProvider>();
        }

        [Test]
        [Category("DownstreamPublicationHistory")]
        public void It_resolves_the_default_DocumentCache_downstream_publication_history_provider_with_postgresql_provider()
        {
            IServiceCollection services = CreateServiceCollection("postgresql");
            AssertSingleDownstreamPublicationHistoryProvider<DocumentCacheUnknownDownstreamPublicationHistoryProvider>(
                services
            );

            using ServiceProvider serviceProvider = services.BuildServiceProvider();

            serviceProvider
                .GetRequiredService<IDocumentCacheDownstreamPublicationHistoryProvider>()
                .Should()
                .BeOfType<DocumentCacheUnknownDownstreamPublicationHistoryProvider>();
        }

        [Test]
        [Category("DownstreamPublicationHistory")]
        public void It_preserves_a_custom_DocumentCache_downstream_publication_history_provider_registered_before_startup()
        {
            IServiceCollection services = CreateServiceCollection(
                "postgresql",
                configureServicesBeforeAddServices: serviceCollection =>
                    serviceCollection.AddSingleton<
                        IDocumentCacheDownstreamPublicationHistoryProvider,
                        CustomDocumentCacheDownstreamPublicationHistoryProvider
                    >()
            );
            AssertSingleDownstreamPublicationHistoryProvider<CustomDocumentCacheDownstreamPublicationHistoryProvider>(
                services
            );

            using ServiceProvider serviceProvider = services.BuildServiceProvider();

            serviceProvider
                .GetRequiredService<IDocumentCacheDownstreamPublicationHistoryProvider>()
                .Should()
                .BeOfType<CustomDocumentCacheDownstreamPublicationHistoryProvider>();
        }

        [Test]
        [Category("DocumentCacheServiceRegistration")]
        public void It_resolves_the_postgresql_DocumentCache_projection_and_administrative_adapters()
        {
            using var serviceProvider = CreateServices("postgresql");
            using var scope = serviceProvider.CreateScope();

            serviceProvider
                .GetRequiredService<IDocumentProjectionWorkPager>()
                .Should()
                .BeOfType<PostgresqlDocumentProjectionWorkPager>();
            serviceProvider
                .GetRequiredService<IDocumentCacheAdministrativeMutex>()
                .Should()
                .BeOfType<PostgresqlDocumentCacheAdministrativeMutex>();
            serviceProvider
                .GetRequiredService<IDocumentCacheAdministrativePrimitives>()
                .Should()
                .BeOfType<DocumentCacheAdministrativePrimitives>()
                .Which.ProviderToken.Should()
                .Be(RelationalProviderToken.Postgresql);
            serviceProvider
                .GetRequiredService<IDocumentCacheProjectionDrainPageProcessor>()
                .Should()
                .BeOfType<DocumentCacheProjectionDrainPageProcessor>();
            scope
                .ServiceProvider.GetRequiredService<IDocumentCacheSessionBoundWriter>()
                .Should()
                .BeOfType<PostgresqlDocumentCacheWriter>();
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
        public void It_resolves_the_mssql_document_cache_physical_source_fingerprint_reader()
        {
            using var serviceProvider = CreateServices("mssql");

            var fingerprintReader =
                serviceProvider.GetRequiredService<IDocumentCachePhysicalSourceFingerprintReader>();

            fingerprintReader.Should().BeOfType<MssqlDocumentCachePhysicalSourceFingerprintReader>();
        }

        [Test]
        public void It_resolves_the_mssql_document_cache_provider_prerequisite_validator()
        {
            using var serviceProvider = CreateServices("mssql");

            var validator = serviceProvider.GetRequiredService<IDocumentCacheProviderPrerequisiteValidator>();

            validator.Should().BeOfType<MssqlDocumentCacheProviderPrerequisiteValidator>();
        }

        [Test]
        [Category("DocumentCacheTargetContext")]
        public void It_resolves_the_DocumentCacheTargetContext_mssql_lifecycle_reader()
        {
            using var serviceProvider = CreateServices("mssql");

            var reader = serviceProvider.GetRequiredService<IDocumentCacheLifecycleReader>();

            reader.Should().BeOfType<MssqlDocumentCacheLifecycleReader>();
        }

        [Test]
        [Category("DocumentCacheTargetContext")]
        public void It_resolves_the_DocumentCacheTargetContext_builder_with_sqlserver_provider()
        {
            using var serviceProvider = CreateServices("mssql");

            serviceProvider
                .GetRequiredService<IDocumentCacheTargetContextBuilder>()
                .Should()
                .BeOfType<DocumentCacheTargetContextBuilder>();
            serviceProvider
                .GetRequiredService<DocumentCacheProcessProviderToken>()
                .ProviderToken.Should()
                .Be(RelationalProviderToken.SqlServer);
        }

        [Test]
        [Category("DocumentCacheTargetRegistry")]
        public void It_resolves_the_DocumentCacheTarget_registry_with_sqlserver_provider()
        {
            using var serviceProvider = CreateServices("mssql");

            serviceProvider
                .GetRequiredService<IDocumentCacheTargetRegistry>()
                .Should()
                .BeOfType<DocumentCacheTargetRegistry>();
        }

        [Test]
        [Category("DocumentCacheDiagnostics")]
        public void It_resolves_the_DocumentCache_diagnostic_snapshot_provider_with_sqlserver_provider()
        {
            using var serviceProvider = CreateServices("mssql");

            serviceProvider
                .GetRequiredService<IDocumentCacheDiagnosticSnapshotProvider>()
                .Should()
                .BeOfType<DocumentCacheDiagnosticSnapshotProvider>();
        }

        [Test]
        [Category("DownstreamPublicationHistory")]
        public void It_resolves_the_default_DocumentCache_downstream_publication_history_provider_with_sqlserver_provider()
        {
            IServiceCollection services = CreateServiceCollection("mssql");
            AssertSingleDownstreamPublicationHistoryProvider<DocumentCacheUnknownDownstreamPublicationHistoryProvider>(
                services
            );

            using ServiceProvider serviceProvider = services.BuildServiceProvider();

            serviceProvider
                .GetRequiredService<IDocumentCacheDownstreamPublicationHistoryProvider>()
                .Should()
                .BeOfType<DocumentCacheUnknownDownstreamPublicationHistoryProvider>();
        }

        [Test]
        [Category("DocumentCacheServiceRegistration")]
        public void It_resolves_the_mssql_DocumentCache_projection_and_administrative_adapters()
        {
            using var serviceProvider = CreateServices("mssql");
            using var scope = serviceProvider.CreateScope();

            serviceProvider
                .GetRequiredService<IDocumentProjectionWorkPager>()
                .Should()
                .Match<IDocumentProjectionWorkPager>(pager =>
                    pager.GetType().Name == "MssqlDocumentProjectionWorkPager"
                );
            serviceProvider
                .GetRequiredService<IDocumentCacheAdministrativeMutex>()
                .Should()
                .Match<IDocumentCacheAdministrativeMutex>(mutex =>
                    mutex.GetType().Name == "MssqlDocumentCacheAdministrativeMutex"
                );
            serviceProvider
                .GetRequiredService<IDocumentCacheAdministrativePrimitives>()
                .Should()
                .BeOfType<DocumentCacheAdministrativePrimitives>()
                .Which.ProviderToken.Should()
                .Be(RelationalProviderToken.SqlServer);
            serviceProvider
                .GetRequiredService<IDocumentCacheProjectionDrainPageProcessor>()
                .Should()
                .BeOfType<DocumentCacheProjectionDrainPageProcessor>();
            scope
                .ServiceProvider.GetRequiredService<IDocumentCacheSessionBoundWriter>()
                .Should()
                .Match<IDocumentCacheSessionBoundWriter>(writer =>
                    writer.GetType().Name == "MssqlDocumentCacheWriter"
                );
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

    private sealed class CustomDocumentCacheDownstreamPublicationHistoryProvider
        : IDocumentCacheDownstreamPublicationHistoryProvider
    {
        public Task<DocumentCacheDownstreamPublicationHistoryObservation> ObserveAsync(
            DocumentCacheTargetKey targetKey,
            DocumentCachePhysicalSourceFingerprint? currentPhysicalSourceFingerprint,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }
}
