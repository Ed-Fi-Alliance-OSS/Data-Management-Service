// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
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

    /// <summary>
    /// The E18 offline commands reject for as long as the DocumentCache runtime's own default
    /// provider answers with the unknown status. That default is registered conditionally, so a host
    /// only gets durable CDC evidence when the control plane is registered ahead of it.
    /// </summary>
    [TestCase("postgresql")]
    [TestCase("mssql")]
    public void It_supplies_the_downstream_publication_history_provider_ahead_of_the_document_cache_default(
        string datastore
    )
    {
        using TempCdcControlStateRoot stateRoot = new();
        IServiceCollection services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(A.Fake<IDocumentCacheGuardedNewEmptyActivationCommand>());
        services.AddSingleton(A.Fake<IMappingSetProvider>());

        services.AddDmsCdcControl(BuildConfiguration(datastore, stateRoot.Path));
        if (datastore == "postgresql")
        {
            services.AddPostgresqlReferenceResolver();
        }
        else
        {
            services.AddMssqlReferenceResolver();
        }

        services
            .Single(descriptor =>
                descriptor.ServiceType
                == typeof(Core.DocumentCache.IDocumentCacheDownstreamPublicationHistoryProvider)
            )
            .ImplementationType.Should()
            .Be<CdcDownstreamPublicationHistoryProvider>();
    }

    /// <summary>
    /// The admin client is librdkafka; the connector's security properties are the Java Kafka client's,
    /// for the client the rendered connector runs inside the Connect worker. Handing the connector's to
    /// this one makes it throw on an unknown property name as it is built. Each dictionary is bound to
    /// the deployment separately, and only the admin one belongs to this process.
    /// </summary>
    [Test]
    public void It_binds_the_two_kafka_security_property_sets_separately()
    {
        using TempCdcControlStateRoot stateRoot = new();
        string section = CdcControlOptions.SectionName;
        IServiceCollection services = new ServiceCollection();

        services.AddLogging();
        services.Configure<CdcBindingStateStoreOptions>(options => options.RootPath = stateRoot.Path);
        services.AddSingleton(A.Fake<IDocumentCacheGuardedNewEmptyActivationCommand>());
        services.AddSingleton(A.Fake<IMappingSetProvider>());

        Dictionary<string, string?> settings = new(StringComparer.Ordinal)
        {
            ["AppSettings:Datastore"] = "postgresql",
            ["CdcBindingStateStore:RootPath"] = stateRoot.Path,
            [$"{section}:KafkaClientSecurityProperties:sasl.jaas.config"] = "jaas",
            [$"{section}:KafkaAdminClientSecurityProperties:sasl.username"] = "cdc-control",
        };
        foreach (KeyValuePair<string, string?> setting in ValidCdcSettings())
        {
            settings.Add(setting.Key, setting.Value);
        }

        services.AddDmsCdcControl(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        using ServiceProvider serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
        CdcControlOptions options = serviceProvider.GetRequiredService<IOptions<CdcControlOptions>>().Value;

        options.KafkaClientSecurityProperties.Should().ContainKey("sasl.jaas.config");
        options.KafkaClientSecurityProperties.Should().NotContainKey("sasl.username");
        options.KafkaAdminClientSecurityProperties.Should().ContainKey("sasl.username");
        options.KafkaAdminClientSecurityProperties.Should().NotContainKey("sasl.jaas.config");
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
            [$"{section}:ConnectorDatabasePrincipal"] = "connector_principal",
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
