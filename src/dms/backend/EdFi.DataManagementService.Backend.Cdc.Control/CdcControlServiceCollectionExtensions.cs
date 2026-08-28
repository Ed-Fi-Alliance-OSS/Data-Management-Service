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
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Backend.Cdc.Control;

public static class CdcControlServiceCollectionExtensions
{
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

        services.AddCdcConnectorTemplates();
        services.AddCdcProviderSetup();
        services.TryAddSingleton(BuildAdminClient);
        services.TryAddSingleton<ICdcKafkaAdmin, CdcKafkaAdminAdapter>();

        services.AddHttpClient(CdcConnectRestAdapter.HttpClientName);
        services.TryAddSingleton<ICdcConnectClient, CdcConnectRestAdapter>();

        services.AddHttpClient(CdcConnectorJolokiaLagReader.HttpClientName);
        services.TryAddSingleton<ICdcConnectorLagReader, CdcConnectorJolokiaLagReader>();

        // Scoped because it composes the connector-template service, which the template library
        // registers as a scoped service.
        services.TryAddScoped<ICdcConnectorObservationMapper, CdcConnectorObservationMapper>();

        string datastore = configuration.GetSection(DatastoreSectionName).Value ?? string.Empty;

        if (string.Equals(datastore, PostgresqlDatastore, StringComparison.OrdinalIgnoreCase))
        {
            services.AddPostgresqlDmsCdcControlPlane();
            return services;
        }

        if (string.Equals(datastore, MssqlDatastore, StringComparison.OrdinalIgnoreCase))
        {
            services.AddMssqlDmsCdcControlPlane();
            return services;
        }

        throw new InvalidOperationException(
            $"{DatastoreSectionName} must be one of: {PostgresqlDatastore}, {MssqlDatastore}"
        );
    }

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
