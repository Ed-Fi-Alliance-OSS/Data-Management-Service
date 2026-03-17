// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Startup;
using FakeItEasy;
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
}
