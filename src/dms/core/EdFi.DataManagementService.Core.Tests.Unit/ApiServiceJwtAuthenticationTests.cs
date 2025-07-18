// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.ResourceLoadOrder;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Validation;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Polly;

namespace EdFi.DataManagementService.Core.Tests.Unit;

[TestFixture]
public class ApiServiceJwtAuthenticationTests
{
    [Test]
    public void Should_Always_Include_JWT_Middleware()
    {
        // Arrange
        var services = new ServiceCollection();

        // Register JWT authentication options
        services.Configure<JwtAuthenticationOptions>(options => { });

        // Register JWT middleware and validation service
        services.AddTransient<JwtAuthenticationMiddleware>();
        services.AddTransient<IJwtValidationService>(sp => A.Fake<IJwtValidationService>());
        services.AddTransient<ILogger<JwtAuthenticationMiddleware>>(_ =>
            NullLogger<JwtAuthenticationMiddleware>.Instance
        );

        var serviceProvider = services.BuildServiceProvider();

        // Create an instance of ApiService to test GetCommonInitialSteps
        var apiService = new ApiService(
            A.Fake<IApiSchemaProvider>(),
            A.Fake<IDocumentStoreRepository>(),
            A.Fake<IClaimSetProvider>(),
            A.Fake<IDocumentValidator>(),
            A.Fake<IQueryHandler>(),
            A.Fake<IMatchingDocumentUuidsValidator>(),
            A.Fake<IEqualityConstraintValidator>(),
            A.Fake<IDecimalValidator>(),
            NullLogger<ApiService>.Instance,
            Options.Create(
                new AppSettings { AllowIdentityUpdateOverrides = "", MaskRequestBodyInLogs = false }
            ),
            A.Fake<IAuthorizationServiceFactory>(),
            ResiliencePipeline.Empty,
            A.Fake<ResourceLoadOrderCalculator>(),
            A.Fake<IUploadApiSchemaService>(),
            serviceProvider
        );

        // Act - Use reflection to call the private GetCommonInitialSteps method
        var getCommonInitialStepsMethod = typeof(ApiService).GetMethod(
            "GetCommonInitialSteps",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        var steps = (List<IPipelineStep>)getCommonInitialStepsMethod!.Invoke(apiService, null)!;

        // Assert
        steps.Should().HaveCount(2); // CoreExceptionLoggingMiddleware + JwtAuthenticationMiddleware
        steps[1].Should().BeOfType<JwtAuthenticationMiddleware>();
    }

    [Test]
    public void When_JWT_Middleware_Not_Registered_Should_Throw_On_Pipeline_Creation()
    {
        // Arrange
        var services = new ServiceCollection();

        // Register JWT authentication options
        services.Configure<JwtAuthenticationOptions>(options => { });

        // DO NOT register JWT middleware - this simulates misconfiguration

        var serviceProvider = services.BuildServiceProvider();

        // Create an instance of ApiService to test GetCommonInitialSteps
        var apiService = new ApiService(
            A.Fake<IApiSchemaProvider>(),
            A.Fake<IDocumentStoreRepository>(),
            A.Fake<IClaimSetProvider>(),
            A.Fake<IDocumentValidator>(),
            A.Fake<IQueryHandler>(),
            A.Fake<IMatchingDocumentUuidsValidator>(),
            A.Fake<IEqualityConstraintValidator>(),
            A.Fake<IDecimalValidator>(),
            NullLogger<ApiService>.Instance,
            Options.Create(
                new AppSettings { AllowIdentityUpdateOverrides = "", MaskRequestBodyInLogs = false }
            ),
            A.Fake<IAuthorizationServiceFactory>(),
            ResiliencePipeline.Empty,
            A.Fake<ResourceLoadOrderCalculator>(),
            A.Fake<IUploadApiSchemaService>(),
            serviceProvider
        );

        // Act & Assert - Should throw InvalidOperationException
        var getCommonInitialStepsMethod = typeof(ApiService).GetMethod(
            "GetCommonInitialSteps",
            BindingFlags.NonPublic | BindingFlags.Instance
        );

        Action act = () => getCommonInitialStepsMethod!.Invoke(apiService, null);
        act.Should()
            .Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*JwtAuthenticationMiddleware*");
    }
}
