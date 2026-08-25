// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("ServiceRegistration")]
public sealed class Given_DocumentCacheAdminServiceRegistration
{
    [TestCase("postgresql", "Postgresql", "PostgreSQL", RelationalProviderToken.PostgresqlValue)]
    [TestCase("mssql", "Mssql", "SQL Server", RelationalProviderToken.SqlServerValue)]
    public void It_builds_the_non_web_document_cache_runtime_graph(
        string datastore,
        string implementationPrefix,
        string displayName,
        string expectedProviderToken
    )
    {
        IServiceCollection services = new ServiceCollection();

        services.AddLogging();
        services.AddDocumentCacheAdminRuntimeServices(
            CreateConfiguration(datastore),
            new LoggerConfiguration().CreateLogger(),
            DocumentCacheTargetKey.Create(string.Empty, 1)
        );

        services
            .Should()
            .NotContain(descriptor => descriptor.ServiceType == typeof(IHostedService), displayName);
        services
            .Should()
            .NotContain(descriptor =>
                descriptor.ServiceType.FullName != null
                && descriptor.ServiceType.FullName.Contains("Kestrel", StringComparison.Ordinal)
            );

        using ServiceProvider serviceProvider = services.BuildServiceProvider();

        serviceProvider.GetServices<IHostedService>().Should().BeEmpty(displayName);
        serviceProvider
            .GetRequiredService<IDataStoreProvider>()
            .Should()
            .BeOfType<ConfigurationServiceDataStoreProvider>(displayName);
        serviceProvider
            .GetRequiredService<IConnectionStringProvider>()
            .Should()
            .BeOfType<DmsConnectionStringProvider>(displayName);
        serviceProvider
            .GetRequiredService<DocumentCacheProcessProviderToken>()
            .ProviderToken.Value.Should()
            .Be(expectedProviderToken, displayName);
        serviceProvider
            .GetRequiredService<IDocumentCacheTargetRegistry>()
            .Should()
            .BeOfType<DocumentCacheTargetRegistry>(displayName);
        serviceProvider
            .GetRequiredService<IDocumentCacheTargetContextBuilder>()
            .Should()
            .BeOfType<DocumentCacheTargetContextBuilder>(displayName);
        serviceProvider
            .GetRequiredService<IDocumentCacheDiagnosticSnapshotProvider>()
            .Should()
            .BeOfType<DocumentCacheDiagnosticSnapshotProvider>(displayName);
        serviceProvider.GetRequiredService<IDocumentCacheStatusService>().Should().NotBeNull(displayName);
        serviceProvider
            .GetRequiredService<IDocumentCacheAdminMutatingCommandDispatcher>()
            .Should()
            .BeOfType<DocumentCacheAdminMutatingCommandDispatcher>(displayName);

        DocumentCacheProjectionSupervisor supervisor =
            serviceProvider.GetRequiredService<DocumentCacheProjectionSupervisor>();
        serviceProvider
            .GetRequiredService<IDocumentCacheProjectionSupervisor>()
            .Should()
            .BeSameAs(supervisor, displayName);

        serviceProvider
            .GetRequiredService<IDatabaseFingerprintReader>()
            .GetType()
            .Name.Should()
            .Be($"{implementationPrefix}DatabaseFingerprintReader", displayName);
        serviceProvider
            .GetRequiredService<IResourceKeyRowReader>()
            .GetType()
            .Name.Should()
            .Be($"{implementationPrefix}ResourceKeyRowReader", displayName);

        ResolveBackendService(serviceProvider, "IDocumentCacheAdministrativeCommandRunner")
            .GetType()
            .Name.Should()
            .Be("DocumentCacheAdministrativeCommandRunner", displayName);
        ResolveBackendService(serviceProvider, "IDocumentCacheAdministrativeMutex")
            .GetType()
            .Name.Should()
            .Be($"{implementationPrefix}DocumentCacheAdministrativeMutex", displayName);
        ResolveBackendService(serviceProvider, "IDocumentCacheGuardedNewEmptyActivationCommand")
            .GetType()
            .Name.Should()
            .Be("DocumentCacheGuardedNewEmptyActivationCommand", displayName);

        serviceProvider
            .GetRequiredService<IOptions<DocumentCacheOptions>>()
            .Value.GetTargetKeys()
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(DocumentCacheTargetKey.Create(string.Empty, 1));
        serviceProvider.GetRequiredService<IOptions<AppSettings>>().Value.MaximumPageSize.Should().Be(0);
    }

    [Test]
    public void It_treats_the_invocation_target_as_the_only_document_cache_target()
    {
        IServiceCollection services = new ServiceCollection();
        DocumentCacheTargetKey invocationTarget = DocumentCacheTargetKey.Create("TenantA", 7);

        services.AddLogging();
        services.AddDocumentCacheAdminRuntimeServices(
            CreateConfigurationWithConfiguredTargets(),
            new LoggerConfiguration().CreateLogger(),
            invocationTarget
        );

        using ServiceProvider serviceProvider = services.BuildServiceProvider();

        serviceProvider
            .GetRequiredService<IOptions<DocumentCacheOptions>>()
            .Value.GetTargetKeys()
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(invocationTarget);
        serviceProvider.GetRequiredService<IDocumentCacheAdminTargetResolver>().Should().NotBeNull();
    }

    private static IConfiguration CreateConfiguration(string datastore) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppSettings:Datastore"] = datastore,
                    ["AppSettings:DefaultPartitionCount"] = "10",
                    ["ConfigurationServiceSettings:BaseUrl"] = "https://cms.example.org",
                    ["ConfigurationServiceSettings:ClientId"] = "client-id",
                    ["ConfigurationServiceSettings:ClientSecret"] = "client-secret",
                    ["ConfigurationServiceSettings:Scope"] = "scope",
                    ["ConfigurationServiceSettings:EncryptionKey"] =
                        "TestEncryptionKey123456789012345678901234567890",
                }
            )
            .Build();

    private static IConfiguration CreateConfigurationWithConfiguredTargets() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppSettings:Datastore"] = "postgresql",
                    ["AppSettings:DefaultPartitionCount"] = "10",
                    ["ConfigurationServiceSettings:BaseUrl"] = "https://cms.example.org",
                    ["ConfigurationServiceSettings:ClientId"] = "client-id",
                    ["ConfigurationServiceSettings:ClientSecret"] = "client-secret",
                    ["ConfigurationServiceSettings:Scope"] = "scope",
                    ["ConfigurationServiceSettings:EncryptionKey"] =
                        "TestEncryptionKey123456789012345678901234567890",
                    ["DataManagement:DocumentCache:Targets:0:TenantKey"] = "ConfiguredTenant",
                    ["DataManagement:DocumentCache:Targets:0:DataStoreId"] = "1",
                    ["DataManagement:DocumentCache:Targets:1:TenantKey"] = "OtherTenant",
                    ["DataManagement:DocumentCache:Targets:1:DataStoreId"] = "2",
                }
            )
            .Build();

    private static object ResolveBackendService(IServiceProvider serviceProvider, string serviceTypeName)
    {
        Type serviceType =
            typeof(DocumentCacheProjectionSupervisor).Assembly.GetType(
                $"EdFi.DataManagementService.Backend.{serviceTypeName}"
            )
            ?? throw new InvalidOperationException(
                $"Backend service type '{serviceTypeName}' was not found."
            );

        return serviceProvider.GetRequiredService(serviceType);
    }
}
