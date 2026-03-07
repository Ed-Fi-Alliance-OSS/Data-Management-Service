// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.ResourceLoadOrder;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.AuthorizationFilters;
using EdFi.DataManagementService.Core.Security.AuthorizationValidation;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            // Startup orchestration
            .AddSingleton<DmsStartupOrchestrator>()
            .AddSingleton<IDmsStartupTask, LoadAndBuildEffectiveSchemaTask>()
            .AddSingleton<IDmsStartupTask, BackendMappingInitializationTask>()
            // Startup components
            .AddSingleton<IApiSchemaInputNormalizer, ApiSchemaInputNormalizer>()
            .AddSingleton<IEffectiveSchemaHashProvider, EffectiveSchemaHashProvider>()
            .AddSingleton<IResourceKeySeedProvider, ResourceKeySeedProvider>()
            .AddSingleton<IBackendMappingInitializer, NoOpBackendMappingInitializer>()
            // Core services
            .AddSingleton<IApiService, ApiService>()
            .AddSingleton<IDataModelInfoProvider, DataModelInfoProvider>()
            .AddTransient<IDocumentValidator, DocumentValidator>()
            .AddTransient<IMatchingDocumentUuidsValidator, MatchingDocumentUuidsValidator>()
            .AddTransient<IEqualityConstraintValidator, EqualityConstraintValidator>()
            .AddTransient<IDecimalValidator, DecimalValidator>()
            .AddSingleton<IAuthorizationServiceFactory, NamedAuthorizationServiceFactory>()
            .AddSingleton<
                IResourceDependencyGraphTransformer,
                PersonAuthorizationDependencyGraphTransformer
            >()
            .AddSingleton<ICoreProjectNameProvider, CoreProjectNameProvider>()
            .AddSingleton<IResourceDependencyGraphFactory, ResourceDependencyGraphFactory>()
            .AddSingleton<IResourceDependencyGraphMLFactory, ResourceDependencyGraphMLFactory>()
            .AddSingleton<IResourceLoadOrderTransformer, PersonAuthorizationLoadOrderTransformer>()
            .AddSingleton<ResourceLoadOrderCalculator>()
            .AddTransient<NoFurtherAuthorizationRequiredValidator>()
            .AddTransient<NamespaceBasedValidator>()
            .AddTransient<RelationshipsWithEdOrgsOnlyValidator>()
            .AddTransient<RelationshipsWithEdOrgsAndPeopleValidator>()
            .AddTransient<RelationshipsWithStudentsOnlyValidator>()
            .AddTransient<RelationshipsWithStudentsOnlyThroughResponsibilityValidator>()
            .AddTransient<RelationshipsWithEdOrgsOnlyFiltersProvider>()
            .AddTransient<RelationshipsWithEdOrgsAndPeopleFiltersProvider>()
            .AddTransient<NoFurtherAuthorizationRequiredFiltersProvider>()
            .AddTransient<RelationshipsWithStudentsOnlyFiltersProvider>()
            .AddTransient<RelationshipsWithStudentsOnlyThroughResponsibilityFiltersProvider>()
            .AddTransient<NamespaceBasedFiltersProvider>()
            .AddResiliencePipeline("backendResiliencePipeline", backendResiliencePipeline)
            .AddScoped<IDmsInstanceSelection, DmsInstanceSelection>()
            .AddScoped<IApplicationContextProvider, CachedApplicationContextProvider>()
            .AddSingleton<IConfigurationServiceApplicationProvider, ConfigurationServiceApplicationProvider>()
            .AddSingleton<IDatabaseFingerprintReader, NullDatabaseFingerprintReader>()
            .AddSingleton<DatabaseFingerprintProvider>()
            .AddSingleton<ResolveDmsInstanceMiddleware>()
            .AddSingleton<ValidateDatabaseFingerprintMiddleware>()
            .AddSingleton<IProfileCmsProvider, ConfigurationServiceProfileProvider>()
            .AddSingleton<IProfileService, CachedProfileService>()
            .AddSingleton<IProfileResponseFilter, ProfileResponseFilter>()
            .AddSingleton<IProfileCreatabilityValidator, ProfileCreatabilityValidator>()
            .AddSingleton<IProfileDataValidator, ProfileDataValidator>()
            .AddTransient<ProfileResolutionMiddleware>()
            .AddTransient<ProfileFilteringMiddleware>()
            .AddTransient<ProfileWriteValidationMiddleware>()
            .AddSingleton<GetTokenInfoHandler>();

        return services;

        void backendResiliencePipeline(ResiliencePipelineBuilder builder)
        {
            CircuitBreakerSettings breakerSettings = new();
            circuitBreakerConfiguration.Bind(breakerSettings);

            DeadlockRetrySettings retrySettings = new();
            deadlockRetryConfiguration.Bind(retrySettings);
            ValidateDeadlockRetrySettings(retrySettings);

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
                        QueryResult.QuerySuccess querySuccess => new QueryResult.QuerySuccess(
                            new JsonArray("REDACTED"),
                            querySuccess.TotalCount
                        ),
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
                    bool shouldHandle = result switch
                    {
                        DeleteResult.UnknownFailure => true,
                        GetResult.UnknownFailure => true,
                        QueryResult.UnknownFailure => true,
                        UpdateResult.UnknownFailure => true,
                        UpsertResult.UnknownFailure => true,
                        _ => false,
                    };

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
    /// Adds JWT authentication services to the DMS Core
    /// </summary>
    public static IServiceCollection AddJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        // Configure JWT authentication options
        services.Configure<JwtAuthenticationOptions>(configuration.GetSection("JwtAuthentication"));

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
        services.AddTransient<JwtAuthenticationMiddleware>();
        services.AddTransient<JwtRoleAuthenticationMiddleware>();

        return services;
    }

    internal static void ValidateDeadlockRetrySettings(DeadlockRetrySettings settings)
    {
        if (settings.MaxRetryAttempts < 0)
        {
            throw new InvalidOperationException("DeadlockRetry:MaxRetryAttempts must be >= 0");
        }

        if (settings.BaseDelayMilliseconds < 1)
        {
            throw new InvalidOperationException("DeadlockRetry:BaseDelayMilliseconds must be >= 1");
        }
    }
}
