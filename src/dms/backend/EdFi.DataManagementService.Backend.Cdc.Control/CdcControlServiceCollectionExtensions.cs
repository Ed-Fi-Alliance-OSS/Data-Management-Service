// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Confluent.Kafka;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Control;

public static class CdcControlServiceCollectionExtensions
{
    /// <summary>
    /// Configuration section the durable binding state store's root path is bound from. The core control
    /// plane registers the options without a source of its own, so a host that never binds this section
    /// silently writes binding records to the built-in default root.
    /// </summary>
    public const string BindingStateStoreSectionName = $"{CdcControlOptions.SectionName}:BindingStateStore";

    private const string DatastoreSectionName = "AppSettings:Datastore";
    private const string PostgresqlDatastore = "postgresql";
    private const string MssqlDatastore = "mssql";

    /// <summary>
    /// Registers the CDC control plane: the deployment policy options, the connector-template and
    /// provider-setup services it composes, and the provider CDC control plane selected by
    /// <c>AppSettings:Datastore</c>.
    /// </summary>
    /// <remarks>
    /// The provider extensions are called rather than <c>AddDmsCdcControlPlane</c> directly. That core
    /// method registers only the binding-state services; the <c>ICdcProviderSourcePositionAdapter</c>
    /// required by the provider barrier and every source-history check is registered only by the
    /// provider extensions, each of which chains into the core method itself. Calling the core method
    /// alone compiles and then fails at resolution time.
    /// </remarks>
    public static IServiceCollection AddDmsCdcControl(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(configuration);
        services
            .AddOptions<CdcControlOptions>()
            .Bind(configuration.GetSection(CdcControlOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<CdcControlOptions>, CdcControlOptionsValidator>()
        );
        services
            .AddOptions<CoreCdc.CdcBindingStateStoreOptions>()
            .Bind(configuration.GetSection(BindingStateStoreSectionName));

        services.AddCdcConnectorTemplates();
        services.AddCdcProviderSetup();
        services.TryAddSingleton(BuildAdminClient);
        services.TryAddSingleton<ICdcKafkaAdmin, CdcKafkaAdminAdapter>();

        services.AddHttpClient(CdcConnectRestAdapter.HttpClientName);
        services.TryAddSingleton<ICdcConnectClient, CdcConnectRestAdapter>();

        services.AddHttpClient(CdcConnectorJolokiaLagReader.HttpClientName);
        services.TryAddSingleton<ICdcConnectorLagReader, CdcConnectorJolokiaLagReader>();

        // The projection correlation evidence is read from the running DMS over HTTP. Nothing here
        // resolves IDocumentCacheStatusService: no projector runs in this process, so an in-process
        // status service could only ever report that its runtime was not observed.
        services.AddHttpClient(CdcProjectionCorrelationCollector.HttpClientName);
        services.TryAddSingleton<ICdcProjectionCorrelationCollector, CdcProjectionCorrelationCollector>();

        // Scoped because it composes the connector-template service, which the template library
        // registers as a scoped service.
        services.TryAddScoped<ICdcConnectorObservationMapper, CdcConnectorObservationMapper>();

        // Reads the ORIGINAL configuration rather than the bound DocumentCache options, which an
        // administrative host replaces with its own invocation target.
        services.TryAddSingleton<CdcExplicitProjectionTargetProof>();

        // Supplies the E18 administrative gate with the durable binding evidence it has had no source
        // for. The DocumentCache runtime services register a default that always answers with the
        // unknown status, and they register it conditionally, so this registration only takes effect
        // when it runs first. A registration test pins that ordering for the shipped hosts.
        services.TryAddSingleton<
            Core.DocumentCache.IDocumentCacheDownstreamPublicationHistoryProvider,
            CdcDownstreamPublicationHistoryProvider
        >();

        services.TryAddSingleton<
            ICdcInstanceDatabaseConnectionFactory,
            CdcInstanceDatabaseConnectionFactory
        >();

        // Derives the provider-setup inputs from the authoritative effective schema rather than from
        // caller input, so no host can assert a source shape the instance database does not have. The
        // host must register the relational mapping-set services its datastore uses.
        services.TryAddSingleton<ICdcProviderSetupInputsFactory, CdcProviderSetupInputsFactory>();

        // Scoped because it composes the scoped provider-setup and template services. The host must
        // also register the DocumentCache runtime services for its datastore: the guarded new-empty
        // activation the controller invokes is registered there, alongside the projector runtime, and
        // is not the CDC control plane's to register.
        services.TryAddScoped<ICdcSetupController, CdcSetupController>();

        string datastore = configuration.GetSection(DatastoreSectionName).Value ?? string.Empty;

        if (string.Equals(datastore, PostgresqlDatastore, StringComparison.OrdinalIgnoreCase))
        {
            services.AddPostgresqlDmsCdcControlPlane();
            services.TryAddSingleton<ICdcEligibilityProbe>(serviceProvider =>
                EligibilityProbe(serviceProvider, CoreCdc.CdcProvider.Postgresql)
            );
            services.TryAddSingleton<ICdcProviderArtifactTeardown>(serviceProvider =>
                ArtifactTeardown(serviceProvider, CoreCdc.CdcProvider.Postgresql)
            );
            return services;
        }

        if (string.Equals(datastore, MssqlDatastore, StringComparison.OrdinalIgnoreCase))
        {
            services.AddMssqlDmsCdcControlPlane();
            services.TryAddSingleton<ICdcEligibilityProbe>(serviceProvider =>
                EligibilityProbe(serviceProvider, CoreCdc.CdcProvider.SqlServer)
            );
            services.TryAddSingleton<ICdcProviderArtifactTeardown>(serviceProvider =>
                ArtifactTeardown(serviceProvider, CoreCdc.CdcProvider.SqlServer)
            );
            return services;
        }

        throw new InvalidOperationException(
            $"{DatastoreSectionName} must be one of: {PostgresqlDatastore}, {MssqlDatastore}"
        );
    }

    /// <summary>
    /// Builds the eligibility probe for the datastore the deployment selected. The probe reads the
    /// instance database directly, so it is provider-specific rather than resolved through the
    /// provider extensions.
    /// </summary>
    private static CdcEligibilityProbe EligibilityProbe(
        IServiceProvider serviceProvider,
        CoreCdc.CdcProvider provider
    ) =>
        new(
            provider,
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetRequiredService<ILogger<CdcEligibilityProbe>>()
        );

    /// <summary>
    /// Builds the provider-artifact teardown for the datastore the deployment selected. Like the probe,
    /// it issues provider-specific statements against the instance database rather than resolving
    /// through the provider extensions.
    /// </summary>
    private static CdcProviderArtifactTeardown ArtifactTeardown(
        IServiceProvider serviceProvider,
        CoreCdc.CdcProvider provider
    ) => new(provider, serviceProvider.GetRequiredService<ILogger<CdcProviderArtifactTeardown>>());

    /// <summary>
    /// Builds the admin client from the deployment's bootstrap servers and Kafka client security
    /// properties. Construction is deferred to first resolution so a registration-time graph check
    /// never opens a broker connection.
    /// </summary>
    private static IAdminClient BuildAdminClient(IServiceProvider serviceProvider)
    {
        CdcControlOptions options = serviceProvider.GetRequiredService<IOptions<CdcControlOptions>>().Value;

        AdminClientConfig config = new() { BootstrapServers = options.KafkaBootstrapServers };
        foreach (
            KeyValuePair<string, string> property in options.ToKafkaClientSecurityProperties().Properties
        )
        {
            config.Set(property.Key, property.Value);
        }

        return new AdminClientBuilder(config).Build();
    }
}
