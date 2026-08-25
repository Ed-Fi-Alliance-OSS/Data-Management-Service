// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    public async Task It_validates_document_cache_options_before_runtime_schema_initialization()
    {
        IServiceCollection services = new ServiceCollection();
        CountingEffectiveSchemaBootstrapper bootstrapper = new();

        services.AddLogging();
        services.AddDocumentCacheAdminRuntimeServices(
            CreateConfiguration(
                DocumentCacheAdminCommandSurface.PostgresqlDatastoreOptionValue,
                new Dictionary<string, string?> { ["DataManagement:DocumentCache:Projector:PageSize"] = "0" }
            ),
            new LoggerConfiguration().CreateLogger(),
            DocumentCacheTargetKey.Create(string.Empty, 1)
        );
        services.Replace(ServiceDescriptor.Singleton<IEffectiveSchemaBootstrapper>(bootstrapper));

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();

        Func<Task> initialize = () => DocumentCacheAdminRuntimeInitializer.InitializeAsync(serviceProvider);

        await initialize
            .Should()
            .ThrowAsync<OptionsValidationException>()
            .WithMessage("*Projector:PageSize must be positive*");
        bootstrapper.InitializeCount.Should().Be(0);
    }

    [Test]
    public void It_applies_datastore_overrides_before_runtime_provider_selection()
    {
        string settingsPath = CreateSettingsFile(_ => { }, datastore: "unsupported");

        try
        {
            var parseResult = DocumentCacheAdminCommandSurface
                .CreateRootCommand()
                .Parse([
                    DocumentCacheAdminCommandSurface.StatusCommandName,
                    DocumentCacheAdminCommandSurface.SettingsOptionName,
                    settingsPath,
                    DocumentCacheAdminCommandSurface.DatastoreOptionName,
                    DocumentCacheAdminCommandSurface.SqlServerDatastoreOptionValue,
                    DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                    "7",
                ]);
            parseResult.Errors.Should().BeEmpty();

            using ServiceProvider serviceProvider = BuildAdminServiceProvider(
                DocumentCacheAdminConfiguration.Build(parseResult),
                DocumentCacheTargetKey.Create(string.Empty, 7)
            );

            serviceProvider
                .GetRequiredService<DocumentCacheProcessProviderToken>()
                .ProviderToken.Value.Should()
                .Be(RelationalProviderToken.SqlServerValue);
            serviceProvider
                .GetRequiredService<IDatabaseFingerprintReader>()
                .GetType()
                .Name.Should()
                .Be("MssqlDatabaseFingerprintReader");
        }
        finally
        {
            TryDelete(settingsPath);
        }
    }

    [Test]
    public void It_applies_status_timeout_overrides_before_document_cache_options_validation()
    {
        string settingsPath = CreateSettingsFile(documentCacheSettings =>
        {
            documentCacheSettings["Status"] = new JsonObject
            {
                ["StatusObservationTimeout"] = "00:00:00",
                ["EndpointTimeout"] = "00:00:00",
            };
        });

        try
        {
            var parseResult = DocumentCacheAdminCommandSurface
                .CreateRootCommand()
                .Parse([
                    DocumentCacheAdminCommandSurface.StatusCommandName,
                    DocumentCacheAdminCommandSurface.SettingsOptionName,
                    settingsPath,
                    DocumentCacheAdminCommandSurface.DatastoreOptionName,
                    DocumentCacheAdminCommandSurface.PostgresqlDatastoreOptionValue,
                    DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                    "7",
                    DocumentCacheAdminCommandSurface.StatusObservationTimeoutSecondsOptionName,
                    "2",
                    DocumentCacheAdminCommandSurface.StatusTimeoutSecondsOptionName,
                    "6",
                ]);
            parseResult.Errors.Should().BeEmpty();

            using ServiceProvider serviceProvider = BuildAdminServiceProvider(
                DocumentCacheAdminConfiguration.Build(parseResult),
                DocumentCacheTargetKey.Create(string.Empty, 7)
            );

            DocumentCacheOptions options = serviceProvider
                .GetRequiredService<IOptions<DocumentCacheOptions>>()
                .Value;

            options.Status.StatusObservationTimeout.Should().Be(TimeSpan.FromSeconds(2));
            options.Status.EndpointTimeout.Should().Be(TimeSpan.FromSeconds(6));
        }
        finally
        {
            TryDelete(settingsPath);
        }
    }

    [Test]
    public void It_applies_command_timeout_overrides_before_document_cache_options_validation()
    {
        string settingsPath = CreateSettingsFile(documentCacheSettings =>
        {
            documentCacheSettings["Administration"] = new JsonObject { ["WorkflowTimeout"] = "00:00:00" };
        });

        try
        {
            var parseResult = DocumentCacheAdminCommandSurface
                .CreateRootCommand()
                .Parse([
                    DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
                    DocumentCacheAdminCommandSurface.SettingsOptionName,
                    settingsPath,
                    DocumentCacheAdminCommandSurface.DatastoreOptionName,
                    DocumentCacheAdminCommandSurface.PostgresqlDatastoreOptionValue,
                    DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                    "7",
                    DocumentCacheAdminCommandSurface.ConfirmOptionName,
                    "onlineCacheRebuild",
                    DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
                    "12",
                ]);
            parseResult.Errors.Should().BeEmpty();

            using ServiceProvider serviceProvider = BuildAdminServiceProvider(
                DocumentCacheAdminConfiguration.Build(parseResult),
                DocumentCacheTargetKey.Create(string.Empty, 7)
            );

            DocumentCacheOptions options = serviceProvider
                .GetRequiredService<IOptions<DocumentCacheOptions>>()
                .Value;

            options.Administration.WorkflowTimeout.Should().Be(TimeSpan.FromSeconds(12));
        }
        finally
        {
            TryDelete(settingsPath);
        }
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

    [Test]
    public void It_ignores_malformed_configured_targets_before_final_document_cache_options_validation()
    {
        IServiceCollection services = new ServiceCollection();
        DocumentCacheTargetKey invocationTarget = DocumentCacheTargetKey.Create("TenantA", 7);

        services.AddLogging();
        services.AddDocumentCacheAdminRuntimeServices(
            CreateConfiguration(
                DocumentCacheAdminCommandSurface.PostgresqlDatastoreOptionValue,
                new Dictionary<string, string?>
                {
                    ["DataManagement:DocumentCache:Targets:0:TenantKey"] = " BadTenant",
                    ["DataManagement:DocumentCache:Targets:0:DataStoreId"] = "0",
                    ["DataManagement:DocumentCache:Targets:1:TenantKey"] = "OtherTenant",
                    ["DataManagement:DocumentCache:Targets:1:DataStoreId"] = "not-a-number",
                }
            ),
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
    }

    private static IConfiguration CreateConfiguration(
        string datastore,
        Dictionary<string, string?>? overrides = null
    )
    {
        Dictionary<string, string?> settings = new()
        {
            ["AppSettings:Datastore"] = datastore,
            ["AppSettings:DefaultPartitionCount"] = "10",
            ["ConfigurationServiceSettings:BaseUrl"] = "https://cms.example.org",
            ["ConfigurationServiceSettings:ClientId"] = "client-id",
            ["ConfigurationServiceSettings:ClientSecret"] = "client-secret",
            ["ConfigurationServiceSettings:Scope"] = "scope",
            ["ConfigurationServiceSettings:EncryptionKey"] =
                "TestEncryptionKey123456789012345678901234567890",
        };

        if (overrides is not null)
        {
            foreach ((string key, string? value) in overrides)
            {
                settings[key] = value;
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

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

    private static ServiceProvider BuildAdminServiceProvider(
        IConfiguration configuration,
        DocumentCacheTargetKey invocationTarget
    )
    {
        IServiceCollection services = new ServiceCollection();
        services.AddLogging();
        services.AddDocumentCacheAdminRuntimeServices(
            configuration,
            new LoggerConfiguration().CreateLogger(),
            invocationTarget
        );

        return services.BuildServiceProvider();
    }

    private static string CreateSettingsFile(
        Action<JsonObject> configureDocumentCacheSettings,
        string datastore = DocumentCacheAdminCommandSurface.PostgresqlDatastoreOptionValue
    )
    {
        JsonObject documentCacheSettings = new();
        configureDocumentCacheSettings(documentCacheSettings);

        JsonObject settings = new()
        {
            ["AppSettings"] = new JsonObject
            {
                ["Datastore"] = datastore,
                ["DefaultPartitionCount"] = 10,
                ["UseApiSchemaPath"] = false,
            },
            ["ConfigurationServiceSettings"] = new JsonObject
            {
                ["BaseUrl"] = "https://cms.example.org",
                ["ClientId"] = "document-cache-admin-service-registration-test",
                ["ClientSecret"] = "client-secret",
                ["Scope"] = "edfi_admin_api/full_access",
                ["EncryptionKey"] = "TestEncryptionKey123456789012345678901234567890",
            },
            ["DataManagement"] = new JsonObject { ["DocumentCache"] = documentCacheSettings },
        };

        string settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}-document-cache-admin-settings.json"
        );
        File.WriteAllText(settingsPath, settings.ToJsonString());
        return settingsPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort temp-file cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp-file cleanup.
        }
    }

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

    private sealed class CountingEffectiveSchemaBootstrapper : IEffectiveSchemaBootstrapper
    {
        public int InitializeCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            InitializeCount++;
            return Task.CompletedTask;
        }
    }
}
