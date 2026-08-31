// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// Without these the provider source-position adapter, which only the provider extensions register,
/// would be missing at runtime rather than at registration time.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcControlServiceRegistration")]
public class Given_CdcControlServiceCollectionExtensionsTests
{
    [Test]
    public void It_resolves_the_postgresql_source_position_adapter_for_the_postgresql_datastore()
    {
        using TempCdcControlStateRoot stateRoot = new();
        using ServiceProvider serviceProvider = BuildServiceProvider("postgresql", stateRoot);

        serviceProvider
            .GetRequiredService<ICdcProviderSourcePositionAdapter>()
            .Provider.Should()
            .Be(CoreCdc.CdcProvider.Postgresql);
    }

    [Test]
    public void It_resolves_the_sql_server_source_position_adapter_for_the_mssql_datastore()
    {
        using TempCdcControlStateRoot stateRoot = new();
        using ServiceProvider serviceProvider = BuildServiceProvider("mssql", stateRoot);

        serviceProvider
            .GetRequiredService<ICdcProviderSourcePositionAdapter>()
            .Provider.Should()
            .Be(CoreCdc.CdcProvider.SqlServer);
    }

    [TestCase("postgresql")]
    [TestCase("mssql")]
    public void It_resolves_every_composed_control_plane_dependency(string datastore)
    {
        using TempCdcControlStateRoot stateRoot = new();
        using ServiceProvider serviceProvider = BuildServiceProvider(datastore, stateRoot);
        using IServiceScope scope = serviceProvider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICdcConnectorTemplateService>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ICdcProviderSetupService>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ICdcBindingLifecycleService>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ICdcProviderSourcePositionAdapter>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ICdcConnectClient>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ICdcConnectorLagReader>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ICdcConnectorObservationMapper>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ICdcProjectionCorrelationCollector>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ICdcProviderSetupInputsFactory>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ICdcSetupController>().Should().NotBeNull();
    }

    [TestCase("postgresql")]
    [TestCase("mssql")]
    public void It_binds_control_options_from_the_document_cache_cdc_section(string datastore)
    {
        using TempCdcControlStateRoot stateRoot = new();
        using ServiceProvider serviceProvider = BuildServiceProvider(datastore, stateRoot);

        CdcControlOptions options = serviceProvider.GetRequiredService<IOptions<CdcControlOptions>>().Value;

        options.DeploymentKey.Should().Be("deployment");
        options.InstanceKey.Should().Be("instance");
        options.MaxRecordBytes.Should().Be(4_194_304);
        options.DurabilityProfile.Should().Be(CdcControlOptions.LocalDurabilityProfile);
    }

    [TestCase("")]
    [TestCase("sqlite")]
    [TestCase("POSTGRES")]
    public void It_rejects_an_unrecognized_datastore_rather_than_registering_a_partial_graph(string datastore)
    {
        IServiceCollection services = new ServiceCollection();

        Action registration = () => services.AddDmsCdcControl(BuildConfiguration(datastore, "ignored"));

        registration
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*AppSettings:Datastore*postgresql*mssql*");
    }

    [Test]
    public void It_rejects_a_missing_datastore_section()
    {
        IServiceCollection services = new ServiceCollection();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        Action registration = () => services.AddDmsCdcControl(configuration);

        registration.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void It_rejects_a_null_configuration()
    {
        IServiceCollection services = new ServiceCollection();

        Action registration = () => services.AddDmsCdcControl(null!);

        registration.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void It_fails_options_validation_when_the_cdc_section_is_absent()
    {
        using TempCdcControlStateRoot stateRoot = new();
        IServiceCollection services = new ServiceCollection();
        services.AddLogging();
        services.Configure<CdcBindingStateStoreOptions>(options => options.RootPath = stateRoot.Path);
        services.AddDmsCdcControl(BuildConfiguration("postgresql", stateRoot.Path, includeCdcSection: false));

        using ServiceProvider serviceProvider = services.BuildServiceProvider();

        Action resolution = () => _ = serviceProvider.GetRequiredService<IOptions<CdcControlOptions>>().Value;

        resolution.Should().Throw<OptionsValidationException>();
    }

    private static ServiceProvider BuildServiceProvider(string datastore, TempCdcControlStateRoot stateRoot)
    {
        IServiceCollection services = new ServiceCollection();

        services.AddLogging();
        services.Configure<CdcBindingStateStoreOptions>(options => options.RootPath = stateRoot.Path);

        // The guarded new-empty activation the setup controller invokes, and the relational mapping-set
        // services the provider-setup inputs are derived from, are registered by the host with the
        // DocumentCache runtime services for its datastore, as the administrative CLI does, rather than
        // by the CDC control plane.
        services.AddSingleton(A.Fake<IDocumentCacheGuardedNewEmptyActivationCommand>());
        services.AddSingleton(A.Fake<IMappingSetProvider>());
        services.AddDmsCdcControl(BuildConfiguration(datastore, stateRoot.Path));

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private static IConfiguration BuildConfiguration(
        string datastore,
        string stateRootPath,
        bool includeCdcSection = true
    )
    {
        Dictionary<string, string?> settings = new(StringComparer.Ordinal)
        {
            ["AppSettings:Datastore"] = datastore,
            ["CdcBindingStateStore:RootPath"] = stateRootPath,
        };

        if (includeCdcSection)
        {
            foreach (KeyValuePair<string, string?> setting in ValidCdcSettings())
            {
                settings.Add(setting.Key, setting.Value);
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static Dictionary<string, string?> ValidCdcSettings()
    {
        string section = CdcControlOptions.SectionName;

        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{section}:DeploymentKey"] = "deployment",
            [$"{section}:InstanceKey"] = "instance",
            [$"{section}:TopicPrefix"] = "edfi.documents.instance",
            [$"{section}:SetupPrincipal"] = "setup_principal",
            [$"{section}:Generation"] = "1",
            [$"{section}:PartitionCount"] = "3",
            [$"{section}:KafkaBootstrapServers"] = "localhost:9092",
            [$"{section}:ConnectBaseUri"] = "http://localhost:8083",
            [$"{section}:ConnectWorkerKey"] = "worker",
            [$"{section}:ConnectOffsetStorageTopic"] = "connect-offsets",
            [$"{section}:DurabilityProfile"] = CdcControlOptions.LocalDurabilityProfile,
            [$"{section}:MaxRecordBytes"] = "4194304",
            [$"{section}:DmsBaseUrl"] = "http://localhost:8080",
            [$"{section}:DmsBearerToken"] = "token",
        };
    }

    private sealed class TempCdcControlStateRoot : IDisposable
    {
        public TempCdcControlStateRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"dms-cdc-control-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temporary directory must never fail a test run.
            }
        }
    }
}
