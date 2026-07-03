// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Tests.Unit.Handler;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Core.Tests.Unit;

public static class TestHelper
{
    /// <summary>
    /// Provides a no-op awaitable Next function
    /// </summary>
    public static readonly Func<Task> NullNext = () => Task.CompletedTask;

    internal static RequestInfo RequestInfoWithRelationalMappingSet(
        string traceId = "",
        IServiceProvider? serviceProvider = null
    )
    {
        var requestInfo = No.RequestInfo(traceId, serviceProvider);
        requestInfo.MappingSet = RelationalWriteSeamFixture
            .Create()
            .CreateSupportedMappingSet(SqlDialect.Pgsql);
        return requestInfo;
    }

    /// <summary>
    /// Builds a ResourceSchema for the given endpointName on the given apiSchemaDocument
    /// </summary>
    internal static ResourceSchema BuildResourceSchema(
        ApiSchemaDocuments apiSchemaDocument,
        string endpointName,
        string projectNamespace = "ed-fi"
    )
    {
        ProjectSchema projectSchema = apiSchemaDocument.FindProjectSchemaForProjectNamespace(
            new(projectNamespace)
        )!;
        return new ResourceSchema(projectSchema.FindResourceSchemaNodeByEndpointName(new(endpointName))!);
    }

    /// <summary>
    /// Registers the resource key validation services needed by the pipeline for tests
    /// where resource key validation is not under test.
    /// </summary>
    public static void AddResourceKeyValidationServices(IServiceCollection services)
    {
        services.AddSingleton<IResourceKeyRowReader, NullResourceKeyRowReader>();
        services.AddSingleton<IResourceKeyValidator>(A.Fake<IResourceKeyValidator>());
        services.AddSingleton<ResourceKeyValidationCacheProvider>();
        services.AddSingleton<IEffectiveSchemaSetProvider>(A.Fake<IEffectiveSchemaSetProvider>());
        services.AddTransient<ValidateResourceKeySeedMiddleware>();
        services.AddTransient<ILogger<ValidateResourceKeySeedMiddleware>>(_ =>
            NullLogger<ValidateResourceKeySeedMiddleware>.Instance
        );
    }

    /// <summary>
    /// Registers the mapping set resolution services needed by the pipeline for tests
    /// where mapping set resolution is not under test.
    /// </summary>
    public static void AddMappingSetResolutionServices(IServiceCollection services)
    {
        services.AddSingleton<IMappingSetProvider>(A.Fake<IMappingSetProvider>());
        services.AddSingleton<IEnumerable<IRuntimeMappingSetCompiler>>(
            Array.Empty<IRuntimeMappingSetCompiler>()
        );
        services.AddSingleton<IEffectiveSchemaSetProvider>(A.Fake<IEffectiveSchemaSetProvider>());
        services.AddSingleton<ResolveMappingSetMiddleware>();
        services.AddTransient<ILogger<ResolveMappingSetMiddleware>>(_ =>
            NullLogger<ResolveMappingSetMiddleware>.Instance
        );
    }

    /// <summary>
    /// Asserts that a 401 response body matches the design-doc / ODS authentication
    /// problem-details contract (urn:ed-fi:api:security:authentication), carrying the
    /// given scenario message in the errors array.
    /// </summary>
    public static void AssertUnauthorizedProblemDetails(IFrontendResponse response, string expectedError)
    {
        response.Body.Should().NotBeNull();
        JsonNode body = response.Body!;

        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:security:authentication");
        body["title"]!.GetValue<string>().Should().Be("Authentication Failed");
        body["detail"]!.GetValue<string>().Should().Be("The caller could not be authenticated.");
        body["status"]!.GetValue<int>().Should().Be(401);

        body["errors"]!.AsArray().Select(error => error!.GetValue<string>()).Should().Contain(expectedError);
    }
}
