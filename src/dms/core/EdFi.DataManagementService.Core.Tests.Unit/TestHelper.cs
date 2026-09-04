// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Telemetry;
using EdFi.DataManagementService.Core.Tests.Unit.Handler;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    /// A scoped service provider carrying the one service the paging middlewares resolve off the
    /// request: an <see cref="IDataStoreSelection"/> whose effective target is of the given kind.
    /// </summary>
    /// <remarks>
    /// <c>No.ServiceProvider</c> returns null for every type, so a step resolving the selection with
    /// <c>GetRequiredService</c> throws against it. A fixture that is not about which database served
    /// the request still needs a target, and <see cref="EffectiveTargetKind.Primary"/> is the one that
    /// leaves the live ordering rule — and so every expectation written before targets mattered —
    /// exactly as it was.
    /// <para>
    /// The connection string is a placeholder that only has to be non-blank:
    /// <see cref="EffectiveDataStoreTarget"/> rejects a blank one, and nothing in these fixtures opens
    /// a connection.
    /// </para>
    /// </remarks>
    internal static IServiceProvider ServiceProviderWithEffectiveTarget(
        EffectiveTargetKind kind = EffectiveTargetKind.Primary
    )
    {
        var dataStoreSelection = A.Fake<IDataStoreSelection>();
        A.CallTo(() => dataStoreSelection.GetEffectiveTarget())
            .Returns(new EffectiveDataStoreTarget(kind, "test-connection-string"));

        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IDataStoreSelection))).Returns(dataStoreSelection);

        return serviceProvider;
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
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new CacheSettings());
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
    /// Registers the collection-paging telemetry the query and partitions pipelines resolve, for tests
    /// composing those pipelines where the emitted metrics are not under test.
    /// </summary>
    public static void AddCollectionPagingTelemetry(IServiceCollection services)
    {
        services.AddSingleton<ICollectionPagingTelemetry>(NoOpCollectionPagingTelemetry.Instance);
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

        // correlationId is part of the DMS problem-details contract; assert it is present and
        // non-empty so a silent drop from the factory is caught here (E2E strips it downstream).
        body["correlationId"].Should().NotBeNull();
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();

        // The contract carries exactly the one scenario message and, unlike the other
        // problem-details factories, emits no validationErrors member.
        body["errors"]!.AsArray().Select(error => error!.GetValue<string>()).Should().Equal(expectedError);
        body["validationErrors"].Should().BeNull();
    }
}
