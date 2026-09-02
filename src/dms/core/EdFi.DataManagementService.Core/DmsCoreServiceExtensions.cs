// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.ResourceLoadOrder;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Telemetry;
using EdFi.DataManagementService.Core.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Telemetry;
using Serilog;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// The DMS service extensions to be registered to a Frontend DI container
/// </summary>
public static class DmsCoreServiceExtensions
{
    /// <summary>
    /// The DMS default service configuration
    /// </summary>
    public static IServiceCollection AddDmsDefaultConfiguration(
        this IServiceCollection services,
        Serilog.ILogger logger,
        IConfigurationSection circuitBreakerConfiguration,
        IConfigurationSection deadlockRetryConfiguration,
        bool maskRequestBodyInLogs
    )
    {
        DeadlockRetrySettings retrySettings = new();
        deadlockRetryConfiguration.Bind(retrySettings);
        ValidateDeadlockRetrySettings(retrySettings);

        // Bound once and registered so the pipeline that opens the circuit and the middleware that
        // reports it as a retriable 503 quote the same break duration.
        CircuitBreakerSettings breakerSettings = new();
        circuitBreakerConfiguration.Bind(breakerSettings);
        breakerSettings.Validate();
        foreach (string tuningWarning in breakerSettings.GetTuningWarnings())
        {
            logger.Warning("Circuit breaker configuration: {TuningWarning}", tuningWarning);
        }

        services.AddSingleton(breakerSettings);

        // The two validation caches read the clock to expire derivative verdicts. TryAdd so a test or
        // a caller that has already supplied a controlled clock keeps theirs.
        services.TryAddSingleton(TimeProvider.System);

        services
            // API Schema services
            .AddSingleton<IApiSchemaValidator, ApiSchemaValidator>()
            .AddSingleton<ApiSchemaProvider>()
            .AddSingleton<IApiSchemaProvider>(provider => provider.GetRequiredService<ApiSchemaProvider>())
            .AddSingleton<ICompiledSchemaCache, CompiledSchemaCache>()
            // Effective schema provider (initialized at startup)
            .AddSingleton<EffectiveApiSchemaProvider>()
            .AddSingleton<IEffectiveApiSchemaProvider>(provider =>
                provider.GetRequiredService<EffectiveApiSchemaProvider>()
            )
            .AddSingleton<EffectiveSchemaSetBuilder>()
            .AddSingleton<IEffectiveSchemaSetProvider, EffectiveSchemaSetProvider>()
            .AddSingleton<IEffectiveSchemaBootstrapper, EffectiveSchemaBootstrapper>()
            // Startup orchestration
            .AddSingleton<DmsStartupOrchestrator>()
            .AddSingleton<IDmsStartupTask, ValidateDatabaseFingerprintReaderRegistrationTask>()
            .AddSingleton<IDmsStartupTask, ValidateResourceKeyRowReaderRegistrationTask>()
            .AddSingleton<IDmsStartupTask, LoadAndBuildEffectiveSchemaTask>()
            .AddSingleton<IDmsStartupTask, BackendMappingInitializationTask>()
            .AddSingleton<IDmsStartupTask, ValidateStartupInstancesTask>()
            .AddSingleton<IDmsStartupTask, WarmUpOidcMetadataTask>()
            .AddSingleton<IDmsStartupTask, CacheClaimSetsTask>()
            // Startup components
            .AddSingleton<IApiSchemaInputNormalizer, ApiSchemaInputNormalizer>()
            .AddSingleton<IEffectiveSchemaHashProvider, EffectiveSchemaHashProvider>()
            .AddSingleton<IResourceKeySeedProvider, ResourceKeySeedProvider>()
            .AddSingleton<IBackendMappingInitializer, MissingBackendMappingInitializer>()
            // Core services
            .AddSingleton<IApiService, ApiService>()
            .AddSingleton<IDataModelInfoProvider, DataModelInfoProvider>()
            .AddSingleton<IDocumentLinkSlugResolver, DocumentLinkSlugResolver>()
            .AddTransient<IDocumentValidator, DocumentValidator>()
            .AddTransient<IMatchingDocumentUuidsValidator, MatchingDocumentUuidsValidator>()
            .AddTransient<IEqualityConstraintValidator, EqualityConstraintValidator>()
            .AddTransient<IDecimalValidator, DecimalValidator>()
            .AddSingleton<
                IResourceDependencyGraphTransformer,
                PersonAuthorizationDependencyGraphTransformer
            >()
            .AddSingleton<ICoreProjectNameProvider, CoreProjectNameProvider>()
            .AddSingleton<IResourceDependencyGraphFactory, ResourceDependencyGraphFactory>()
            .AddSingleton<IResourceDependencyGraphMLFactory, ResourceDependencyGraphMLFactory>()
            .AddSingleton<IResourceLoadOrderTransformer, PersonAuthorizationLoadOrderTransformer>()
            .AddSingleton<ResourceLoadOrderCalculator>()
            .AddResiliencePipeline("backendResiliencePipeline", backendResiliencePipeline)
            .AddSingleton(retrySettings)
            .AddScoped<IDataStoreSelection, DataStoreSelection>()
            .AddScoped<IApplicationContextProvider, CachedApplicationContextProvider>()
            .AddSingleton<IConfigurationServiceApplicationProvider, ConfigurationServiceApplicationProvider>()
            .AddSingleton<IDatabaseFingerprintReader, MissingDatabaseFingerprintReader>()
            // Both validation caches read the clock for derivative expiry and read CacheSettings for
            // the bounded TTL. CacheSettings is registered by AddDmsConfigurationServiceDataStoreProvider,
            // which every composition path calls; because these are resolved lazily rather than at
            // registration, the order of the two calls does not matter.
            .AddSingleton<DatabaseFingerprintProvider>()
            .AddSingleton<ResolveDataStoreMiddleware>()
            // The pipeline steps construct SelectEffectiveDataStoreTargetMiddleware themselves,
            // because its routing policy differs per pipeline; only its response seam is registered.
            .AddSingleton<
                IEffectiveTargetSelectionResponseFactory,
                DefaultEffectiveTargetSelectionResponseFactory
            >()
            .AddSingleton<ValidateDatabaseFingerprintMiddleware>()
            // Resource key validation
            .AddSingleton<IResourceKeyRowReader, MissingResourceKeyRowReader>()
            .AddSingleton<IResourceKeyValidator, ResourceKeyValidator>()
            .AddSingleton<ResourceKeyValidationCacheProvider>()
            .AddSingleton<ValidateResourceKeySeedMiddleware>()
            // Mapping set resolution
            .AddSingleton<ResolveMappingSetMiddleware>()
            .AddSingleton<IProfileCmsProvider, ConfigurationServiceProfileProvider>()
            .AddSingleton<IProfileService, CachedProfileService>()
            .AddSingleton<IReadableProfileProjector, ReadableProfileProjector>()
            .AddSingleton<IProfileDataValidator, ProfileDataValidator>()
            .AddTransient<ProfileResolutionMiddleware>()
            .AddTransient<ProfileWritePipelineMiddleware>()
            .AddSingleton<ITokenInfoRelationalMappingSetResolver, TokenInfoRelationalMappingSetResolver>()
            .AddSingleton<GetTokenInfoHandler>()
            .AddSingleton<AvailableChangeVersionsHandler>()
            // Collection-read observability
            .AddSingleton<ICollectionPagingTelemetry, CollectionPagingTelemetry>();

        return services;

        void backendResiliencePipeline(ResiliencePipelineBuilder builder)
        {
            var loggerFactory = LoggerFactory.Create(loggingBuilder => loggingBuilder.AddSerilog(logger));
            var cbFailureLogger = loggerFactory.CreateLogger("CircuitBreakerFailureDetection");
            var cbLogger = loggerFactory.CreateLogger("CircuitBreaker");
            var retryLogger = loggerFactory.CreateLogger("DeadlockRetry");

            TelemetryOptions telemetryOptions = new() { LoggerFactory = loggerFactory };

            if (maskRequestBodyInLogs)
            {
                telemetryOptions.ResultFormatter = (context, result) =>
                {
                    return result switch
                    {
                        GetResult.GetSuccess getSuccess => new GetResult.GetSuccess(
                            getSuccess.DocumentUuid,
                            "REDACTED",
                            getSuccess.LastModifiedDate,
                            getSuccess.LastModifiedTraceId
                        ),
                        // Copied rather than rebuilt, so a member added to the result later cannot be
                        // dropped from the logged copy by omission here.
                        QueryResult.QuerySuccess querySuccess => querySuccess with
                        {
                            EdfiDocs = new JsonArray("REDACTED"),
                        },
                        _ => result,
                    };
                };
            }

            CircuitBreakerStrategyOptions optionsUnknownFailure = new()
            {
                FailureRatio = breakerSettings.FailureRatio,
                SamplingDuration = TimeSpan.FromSeconds(breakerSettings.SamplingDurationSeconds),
                MinimumThroughput = breakerSettings.MinimumThroughput,
                BreakDuration = TimeSpan.FromSeconds(breakerSettings.BreakDurationSeconds),
                ShouldHandle = new PredicateBuilder().HandleResult(result =>
                {
                    bool shouldHandle = Utility.IsUnknownFailureResult(result);

                    if (shouldHandle)
                    {
                        cbFailureLogger.LogWarning(
                            "Circuit breaker detected failure: {FailureType} - {FailureDetails}",
                            result.GetType().Name,
                            result.ToString()
                        );
                    }

                    return shouldHandle;
                }),
                OnOpened = args =>
                {
                    cbLogger.LogWarning(
                        "Circuit breaker opened due to failure threshold being reached. "
                            + "Check the CircuitBreakerFailureDetection logs above for specific failure details."
                    );
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    cbLogger.LogInformation("Circuit breaker closed - normal operation resumed");
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = args =>
                {
                    cbLogger.LogInformation("Circuit breaker half-opened - testing if service has recovered");
                    return ValueTask.CompletedTask;
                },
            };

            RetryStrategyOptions retryOptions = new()
            {
                BackoffType = DelayBackoffType.Exponential,
                MaxRetryAttempts = retrySettings.MaxRetryAttempts,
                Delay = TimeSpan.FromMilliseconds(retrySettings.BaseDelayMilliseconds),
                UseJitter = retrySettings.UseJitter,
                ShouldHandle = new PredicateBuilder().HandleResult(Utility.IsRetryableResult),
                OnRetry = Utility.CreateOnRetryHandler(retryLogger, retrySettings.MaxRetryAttempts),
            };

            // Pipeline ordering (outermost → innermost): CircuitBreaker → Retry → Execute.
            // Retry wraps the full repository call (including connection/transaction lifecycle)
            // because deadlock recovery requires replaying the entire transaction,
            // not just the failing SQL statement.
            builder.ConfigureTelemetry(telemetryOptions);

            builder.AddCircuitBreaker(optionsUnknownFailure);

            // MaxRetryAttempts = 0 disables retries (useful for debugging).
            // Polly v8 requires MaxRetryAttempts >= 1, so we skip adding the strategy.
            if (retrySettings.MaxRetryAttempts > 0)
            {
                builder.AddRetry(retryOptions);
            }
        }
    }

    /// <summary>
    /// Adds the shared DocumentCache option binding and validation used by hosted DMS and tools.
    /// </summary>
    public static IServiceCollection AddDmsDocumentCacheOptions(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (
            !services.Any(descriptor =>
                descriptor.ServiceType == typeof(IConfigureOptions<DocumentCacheOptions>)
            )
        )
        {
            services
                .AddOptions<DocumentCacheOptions>()
                .Bind(configuration.GetSection(DocumentCacheOptions.SectionName))
                .ValidateOnStart();
        }

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<DocumentCacheOptions>,
                DocumentCacheOptionsValidator
            >()
        );

        return services;
    }

    /// <summary>
    /// Adds the shared CMS-backed data store provider and connection-string provider used by DMS
    /// runtime services and non-web tools.
    /// </summary>
    public static IServiceCollection AddDmsConfigurationServiceDataStoreProvider(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection configurationServiceSettings = configuration.GetSection(
            "ConfigurationServiceSettings"
        );
        Uri configurationServiceBaseAddress = ValidateConfigurationServiceBaseAddress(
            configurationServiceSettings
        );

        services.AddHybridCache();
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IConfigureOptions<CacheSettings>)))
        {
            services
                .AddOptions<CacheSettings>()
                .Bind(configuration.GetSection("CacheSettings"))
                .ValidateOnStart();
        }

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<CacheSettings>, CacheSettingsValidator>()
        );
        services.TryAddSingleton(serviceProvider =>
        {
            CacheSettings cacheSettings = serviceProvider.GetRequiredService<IOptions<CacheSettings>>().Value;

            // Read the raw value as well as the bound one. Binding leaves the property at its default
            // when the setting is absent, so the bound value alone cannot tell an absent setting from
            // one an operator explicitly set to the same number - and only one of those is worth a
            // warning.
            // Only null is checked, not blank: options binding already rejects a present-but-empty
            // value for an int property, as it does for every other expiration in this section, so a
            // blank one never reaches this line.
            string? rawExpiration = configuration.GetSection("CacheSettings")[
                "DerivativeValidationCacheExpirationSeconds"
            ];
            (int effectiveSeconds, string? warning) = DerivativeValidationCacheExpiration.Resolve(
                rawExpiration is null ? null : cacheSettings.DerivativeValidationCacheExpirationSeconds
            );

            cacheSettings.DerivativeValidationCacheExpirationSeconds = effectiveSeconds;

            if (warning is not null)
            {
                serviceProvider
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(DerivativeValidationCacheExpiration))
                    .LogWarning("{Warning}", warning);
            }

            return cacheSettings;
        });

        services.AddTransient<ConfigurationServiceResponseHandler>();
        services
            .AddHttpClient<ConfigurationServiceApiClient>(client =>
            {
                client.BaseAddress = configurationServiceBaseAddress;
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("Accept", "application/x-www-form-urlencoded");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler())
            .AddHttpMessageHandler<ConfigurationServiceResponseHandler>();

        services.TryAddSingleton(
            new ConfigurationServiceContext(
                configurationServiceSettings["ClientId"] ?? string.Empty,
                configurationServiceSettings["ClientSecret"] ?? string.Empty,
                configurationServiceSettings["Scope"] ?? string.Empty
            )
        );
        services.TryAddSingleton<IConnectionStringDecryptionService>(
            new ConnectionStringDecryptionService(
                configurationServiceSettings["EncryptionKey"] ?? string.Empty
            )
        );
        services.TryAddSingleton<ConfigurationServiceDataStoreProvider>();
        services.TryAddSingleton<IDataStoreProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<ConfigurationServiceDataStoreProvider>()
        );
        services.TryAddSingleton<IConnectionStringProvider, DmsConnectionStringProvider>();
        services.TryAddSingleton<IConfigurationServiceTokenHandler, ConfigurationServiceTokenHandler>();

        return services;
    }

    private static Uri ValidateConfigurationServiceBaseAddress(
        IConfigurationSection configurationServiceSettings
    )
    {
        string? baseUrl = configurationServiceSettings["BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                "ConfigurationServiceSettings:BaseUrl must be an absolute HTTP or HTTPS URI."
            );
        }

        if (
            !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out Uri? parsedBaseUri)
            || string.IsNullOrWhiteSpace(parsedBaseUri.Host)
        )
        {
            throw new InvalidOperationException(
                "ConfigurationServiceSettings:BaseUrl must be an absolute HTTP or HTTPS URI."
            );
        }

        if (
            !string.Equals(parsedBaseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parsedBaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidOperationException(
                "ConfigurationServiceSettings:BaseUrl must be an absolute HTTP or HTTPS URI."
            );
        }

        return new Uri($"{parsedBaseUri.AbsoluteUri.TrimEnd('/')}/");
    }

    /// <summary>
    /// Adds shared DocumentCache target resolution services without registering any background
    /// projector host.
    /// </summary>
    public static IServiceCollection AddDmsDocumentCacheTargetRegistry(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDmsDocumentCacheOptions(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<DocumentCacheProcessProviderToken>(_ =>
        {
            string? datastore = configuration.GetSection("AppSettings:Datastore").Value;
            if (
                !DocumentCacheProcessProviderToken.TryCreate(
                    datastore,
                    out DocumentCacheProcessProviderToken? providerToken
                )
            )
            {
                throw new InvalidOperationException(
                    "Unable to normalize AppSettings:Datastore for DocumentCache target resolution."
                );
            }

            return providerToken!;
        });
        services.AddSingleton<IDocumentCacheTargetContextBuilder, DocumentCacheTargetContextBuilder>();
        services.AddSingleton<IDocumentCacheTargetRegistry>(serviceProvider =>
        {
            IDataStoreProvider dataStoreProvider =
                serviceProvider.GetService<ConfigurationServiceDataStoreProvider>()
                ?? serviceProvider.GetRequiredService<IDataStoreProvider>();

            return new DocumentCacheTargetRegistry(
                dataStoreProvider,
                serviceProvider.GetRequiredService<IDocumentCacheTargetContextBuilder>(),
                serviceProvider.GetRequiredService<IOptions<DocumentCacheOptions>>(),
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetRequiredService<ILogger<DocumentCacheTargetRegistry>>()
            );
        });
        services.AddSingleton<
            IDocumentCacheDiagnosticSnapshotProvider,
            DocumentCacheDiagnosticSnapshotProvider
        >();

        return services;
    }

    /// <summary>
    /// Adds JWT authentication services to the DMS Core
    /// </summary>
    public static IServiceCollection AddJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        // Configure JWT authentication options
        services.Configure<JwtAuthenticationOptions>(configuration.GetSection("JwtAuthentication"));
        services.TryAddSingleton(TimeProvider.System);

        // Register HttpClient for OIDC metadata retrieval
        services.AddHttpClient();

        // Register singleton ConfigurationManager for OIDC metadata caching
        services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(serviceProvider =>
        {
            var options = serviceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<JwtAuthenticationOptions>>()
                .Value;

            if (string.IsNullOrEmpty(options.MetadataAddress))
            {
                throw new InvalidOperationException(
                    "JwtAuthentication:MetadataAddress must be configured for JWT authentication"
                );
            }

            var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
            HttpClient httpClient = httpClientFactory.CreateClient();

            ConfigurationManager<OpenIdConnectConfiguration> configManager = new(
                options.MetadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new Security.HttpDocumentRetriever(httpClient) { RequireHttps = options.RequireHttpsMetadata }
            )
            {
                RefreshInterval = TimeSpan.FromMinutes(options.RefreshIntervalMinutes),
                AutomaticRefreshInterval = TimeSpan.FromHours(options.AutomaticRefreshIntervalHours),
            };

            return configManager;
        });

        services.AddSingleton<IJwtValidationService, JwtValidationService>();
        services.AddSingleton<
            IDocumentCacheStatusAuthorizationService,
            DocumentCacheStatusAuthorizationService
        >();
        services.AddTransient<JwtAuthenticationMiddleware>();
        services.AddTransient<JwtRoleAuthenticationMiddleware>();

        return services;
    }

    internal static void ValidateDeadlockRetrySettings(DeadlockRetrySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.Validate();
    }
}
