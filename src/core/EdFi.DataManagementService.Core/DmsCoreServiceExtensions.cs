// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Validation;
using Json.Schema;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.CircuitBreaker;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// The DMS service extensions to be registered to a Frontend DI container
/// </summary>
public static class DmsCoreServiceExtensions
{
    /// <summary>
    /// The DMS default service configuration
    /// </summary>
    public static IServiceCollection AddDmsDefaultConfiguration(this IServiceCollection services)
    {
        services
            .AddSingleton<IApiSchemaProvider, ApiSchemaFileLoader>()
            .AddSingleton<IApiSchemaSchemaProvider, ApiSchemaSchemaProvider>()
            .AddSingleton<IApiSchemaValidator, ApiSchemaValidator>()
            .AddSingleton<IApiService, ApiService>()
            .AddTransient<IDocumentValidator, DocumentValidator>()
            .AddTransient<IMatchingDocumentUuidsValidator, MatchingDocumentUuidsValidator>()
            .AddTransient<IEqualityConstraintValidator, EqualityConstraintValidator>()
            .AddResiliencePipeline("upsertCircuitBreaker", (builder) =>
            {
                var optionsUpsertFailure = new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.1,
                    SamplingDuration = TimeSpan.FromSeconds(10),
                    MinimumThroughput = 2,
                    BreakDuration = TimeSpan.FromSeconds(30),
                    ShouldHandle = new PredicateBuilder()
                        .HandleResult(response => response is UpsertResult.UnknownFailure),
                    OnOpened = (result) =>
                    {
                        Debug.WriteLine("OPENED");
                        return ValueTask.CompletedTask;
                    },
                    OnHalfOpened = (result) =>
                    {
                        Debug.WriteLine("HALF OPENED");
                        return ValueTask.CompletedTask;
                    },
                    OnClosed = (result) =>
                    {
                        Debug.WriteLine("CLOSED");
                        return ValueTask.CompletedTask;
                    }
                };
                builder.AddCircuitBreaker(optionsUpsertFailure);
            });

        return services;
    }
}
