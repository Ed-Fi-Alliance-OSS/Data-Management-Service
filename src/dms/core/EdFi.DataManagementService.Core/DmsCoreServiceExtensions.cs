// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.ResourceLoadOrder;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.AuthorizationFilters;
using EdFi.DataManagementService.Core.Security.AuthorizationValidation;
using EdFi.DataManagementService.Core.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
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
        IConfigurationSection circuitBreakerConfiguration
    )
    {
        services
            .AddSingleton<IApiSchemaProvider, ApiSchemaFileLoader>()
            .AddSingleton<IJsonSchemaForApiSchemaProvider, JsonSchemaForApiSchemaProvider>()
            .AddSingleton<IApiSchemaValidator, ApiSchemaValidator>()
            .AddSingleton<IApiService, ApiService>()
            .AddTransient<IDocumentValidator, DocumentValidator>()
            .AddTransient<IMatchingDocumentUuidsValidator, MatchingDocumentUuidsValidator>()
            .AddTransient<IEqualityConstraintValidator, EqualityConstraintValidator>()
            .AddTransient<IDecimalValidator, DecimalValidator>()
            .AddSingleton<IAuthorizationServiceFactory, NamedAuthorizationServiceFactory>()
            .AddSingleton<
                IResourceDependencyGraphTransformer,
                PersonAuthorizationDependencyGraphTransformer
            >()
            .AddSingleton<IResourceLoadOrderTransformer, PersonAuthorizationLoadOrderTransformer>()
            .AddSingleton<ResourceLoadOrderCalculator>()
            .AddTransient<NoFurtherAuthorizationRequiredValidator>()
            .AddTransient<NamespaceBasedValidator>()
            .AddTransient<RelationshipsWithEdOrgsOnlyValidator>()
            .AddTransient<RelationshipsWithEdOrgsAndPeopleValidator>()
            .AddTransient<RelationshipsWithStudentsOnlyValidator>()
            .AddTransient<RelationshipsWithEdOrgsOnlyFiltersProvider>()
            .AddTransient<RelationshipsWithEdOrgsAndPeopleFiltersProvider>()
            .AddTransient<NoFurtherAuthorizationRequiredFiltersProvider>()
            .AddTransient<RelationshipsWithStudentsOnlyFiltersProvider>()
            .AddTransient<NamespaceBasedFiltersProvider>()
            .AddResiliencePipeline("backendResiliencePipeline", backendResiliencePipeline);

        return services;

        void backendResiliencePipeline(ResiliencePipelineBuilder builder)
        {
            TelemetryOptions telemetryOptions = new()
            {
                LoggerFactory = LoggerFactory.Create(builder => builder.AddSerilog(logger)),
            };

            CircuitBreakerSettings breakerSettings = new();
            circuitBreakerConfiguration.Bind(breakerSettings);

            CircuitBreakerStrategyOptions optionsUnknownFailure = new()
            {
                FailureRatio = breakerSettings.FailureRatio,
                SamplingDuration = TimeSpan.FromSeconds(breakerSettings.SamplingDurationSeconds),
                MinimumThroughput = breakerSettings.MinimumThroughput,
                BreakDuration = TimeSpan.FromSeconds(breakerSettings.BreakDurationSeconds),
                ShouldHandle = new PredicateBuilder().HandleResult(result =>
                {
                    return result switch
                    {
                        DeleteResult.UnknownFailure => true,
                        GetResult.UnknownFailure => true,
                        QueryResult.UnknownFailure => true,
                        UpdateResult.UnknownFailure => true,
                        UpsertResult.UnknownFailure => true,
                        _ => false,
                    };
                }),
                // Add circuit breaker event handlers for more detailed logging
                OnOpened = args =>
                {
                    var cbLogger = LoggerFactory
                        .Create(b => b.AddSerilog(logger))
                        .CreateLogger("CircuitBreaker");
                    cbLogger.LogWarning("Circuit breaker opened due to failure threshold being reached");
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    var cbLogger = LoggerFactory
                        .Create(b => b.AddSerilog(logger))
                        .CreateLogger("CircuitBreaker");
                    cbLogger.LogInformation("Circuit breaker closed - normal operation resumed");
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = args =>
                {
                    var cbLogger = LoggerFactory
                        .Create(b => b.AddSerilog(logger))
                        .CreateLogger("CircuitBreaker");
                    cbLogger.LogInformation("Circuit breaker half-opened - testing if service has recovered");
                    return ValueTask.CompletedTask;
                },
            };
            builder
                .ConfigureTelemetry(telemetryOptions)
                .AddCircuitBreaker(optionsUnknownFailure)
                .AddRetry(
                    new()
                    {
                        BackoffType = DelayBackoffType.Exponential,
                        MaxRetryAttempts = 4,
                        Delay = TimeSpan.FromMilliseconds(500),
                        ShouldHandle = new PredicateBuilder().HandleResult(result =>
                        {
                            return result switch
                            {
                                DeleteResult.DeleteFailureWriteConflict => true,
                                GetResult.GetFailureRetryable => true,
                                QueryResult.QueryFailureRetryable => true,
                                UpdateResult.UpdateFailureWriteConflict => true,
                                UpsertResult.UpsertFailureWriteConflict => true,
                                _ => false,
                            };
                        }),
                        // Add event handlers for retry-specific logging
                        OnRetry = args =>
                        {
                            var retryLogger = LoggerFactory
                                .Create(b => b.AddSerilog(logger))
                                .CreateLogger("RetryStrategy");

                            if (args.Outcome.Exception != null)
                            {
                                retryLogger.LogWarning(
                                    args.Outcome.Exception,
                                    "Retry attempt {AttemptNumber} due to exception. Delay: {Delay}ms. Exception: {ExceptionType} - {ExceptionMessage}",
                                    args.AttemptNumber,
                                    args.RetryDelay.TotalMilliseconds,
                                    args.Outcome.Exception.GetType().Name,
                                    args.Outcome.Exception.Message
                                );
                            }
                            else
                            {
                                retryLogger.LogWarning(
                                    "Retry attempt {AttemptNumber} due to result failure. Delay: {Delay}ms. Outcome: {Outcome}",
                                    args.AttemptNumber,
                                    args.RetryDelay.TotalMilliseconds,
                                    args.Outcome.Result?.ToString()
                                );
                            }

                            return ValueTask.CompletedTask;
                        },
                    }
                )
                .Build();
        }
    }
}
