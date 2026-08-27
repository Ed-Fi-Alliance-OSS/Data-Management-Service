// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
public class ApplicationContextRequirementMiddlewareTests
{
    private const string ClientId = "client-123";
    private const string Tenant = "Tenant-A";
    private static readonly ApplicationContext _applicationContext = new(
        Id: 1,
        ApplicationId: 2,
        ClientId,
        ClientUuid: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        DataStoreIds: [3],
        CreatorOwnershipTokenId: 31415,
        OwnershipTokenIds: [2718]
    );

    [Test]
    public async Task It_requires_application_context_for_POST_regardless_of_strategies()
    {
        var provider = CreateProvider(new ApplicationContextResult.Success(_applicationContext));
        RequestInfo requestInfo = CreateRequestInfo(RequestMethod.POST, provider);
        var nextCalled = false;

        await CreateMiddleware()
            .Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

        nextCalled.Should().BeTrue();
        requestInfo.ApplicationContext.Should().BeSameAs(_applicationContext);
        A.CallTo(() => provider.GetApplicationByClientIdAsync(ClientId, Tenant))
            .MustHaveHappenedOnceExactly();
    }

    [TestCase("GET")]
    [TestCase("PUT")]
    [TestCase("DELETE")]
    public async Task It_requires_application_context_for_OwnershipBased_resource_actions(string methodName)
    {
        var provider = CreateProvider(new ApplicationContextResult.Success(_applicationContext));
        RequestMethod method = Enum.Parse<RequestMethod>(methodName);
        RequestInfo requestInfo = CreateRequestInfo(method, provider);
        requestInfo.ResourceActionAuthStrategies = ["NoFurtherAuthorizationRequired", "OwnershipBased"];
        var nextCalled = false;

        await CreateMiddleware()
            .Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

        nextCalled.Should().BeTrue();
        requestInfo.ApplicationContext.Should().BeSameAs(_applicationContext);
        A.CallTo(() => provider.GetApplicationByClientIdAsync(ClientId, Tenant))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_does_not_resolve_application_context_for_an_unrelated_operation()
    {
        var provider = CreateProvider(new ApplicationContextResult.Success(_applicationContext));
        RequestInfo requestInfo = CreateRequestInfo(RequestMethod.GET, provider);
        requestInfo.ResourceActionAuthStrategies = ["NoFurtherAuthorizationRequired"];
        var nextCalled = false;

        await CreateMiddleware()
            .Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

        nextCalled.Should().BeTrue();
        requestInfo.ApplicationContext.Should().BeNull();
        A.CallTo(() => provider.GetApplicationByClientIdAsync(A<string>._, A<string?>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_returns_the_deferred_profile_failure_when_context_is_not_required()
    {
        var provider = CreateProvider(new ApplicationContextResult.Success(_applicationContext));
        RequestInfo requestInfo = CreateRequestInfo(RequestMethod.GET, provider);
        var deferredResponse = new FrontendResponse(
            StatusCode: 406,
            Body: new JsonObject { ["status"] = 406 },
            Headers: [],
            ContentType: "application/problem+json"
        );
        requestInfo.DeferredProfileContextFailureResponse = deferredResponse;

        await CreateMiddleware().Execute(requestInfo, TestHelper.NullNext);

        requestInfo.FrontendResponse.Should().BeSameAs(deferredResponse);
        A.CallTo(() => provider.GetApplicationByClientIdAsync(A<string>._, A<string?>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_maps_required_NotFound_to_generic_401_before_a_profile_fallback()
    {
        var provider = CreateProvider(new ApplicationContextResult.NotFound());
        RequestInfo requestInfo = CreateRequestInfo(RequestMethod.GET, provider);
        requestInfo.ResourceActionAuthStrategies = ["OwnershipBased"];
        requestInfo.DeferredProfileContextFailureResponse = new FrontendResponse(
            StatusCode: 406,
            Body: new JsonObject { ["ownershipTokenId"] = 31415 },
            Headers: []
        );

        await CreateMiddleware().Execute(requestInfo, TestHelper.NullNext);

        requestInfo.FrontendResponse.StatusCode.Should().Be(401);
        TestHelper.AssertUnauthorizedProblemDetails(
            requestInfo.FrontendResponse,
            "Unable to resolve application context for the authenticated client."
        );
        requestInfo.FrontendResponse.Body!.ToJsonString().Should().NotContain("31415");
        requestInfo.FrontendResponse.Body!.ToJsonString().Should().NotContain("ownershipTokenId");
    }

    [Test]
    public async Task It_maps_required_Unavailable_to_generic_503_without_ownership_values()
    {
        var provider = CreateProvider(new ApplicationContextResult.Unavailable());
        RequestInfo requestInfo = CreateRequestInfo(RequestMethod.DELETE, provider);
        requestInfo.ResourceActionAuthStrategies = ["OwnershipBased"];

        await CreateMiddleware().Execute(requestInfo, TestHelper.NullNext);

        requestInfo.FrontendResponse.StatusCode.Should().Be(503);
        requestInfo.FrontendResponse.Body!["type"]!
            .GetValue<string>()
            .Should()
            .Be("urn:ed-fi:api:service-unavailable");
        requestInfo.FrontendResponse.Body!.ToJsonString().Should().NotContain("31415");
        requestInfo.FrontendResponse.Body!.ToJsonString().Should().NotContain("2718");
        requestInfo.FrontendResponse.Body!.ToJsonString().Should().NotContain("ownershipTokenId");
    }

    [Test]
    public async Task It_reuses_the_scoped_profile_lookup_and_gives_required_401_precedence()
    {
        var configurationProvider = A.Fake<IConfigurationServiceApplicationProvider>();
        A.CallTo(() => configurationProvider.GetApplicationByClientIdAsync(ClientId, Tenant))
            .Returns(new ApplicationContextResult.NotFound());
        var cachedProvider = new CachedApplicationContextProvider(
            configurationProvider,
            CreateHybridCache(),
            new CacheSettings { ApplicationContextCacheExpirationSeconds = 600 },
            NullLogger<CachedApplicationContextProvider>.Instance
        );
        RequestInfo requestInfo = CreateRequestInfo(RequestMethod.GET, cachedProvider);
        requestInfo.FrontendRequest = requestInfo.FrontendRequest with
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept"] = "application/vnd.ed-fi.student.testprofile.readable+json",
            },
        };
        var handlerCalled = false;
        var profileMiddleware = new ProfileResolutionMiddleware(
            A.Fake<IProfileService>(),
            NullLogger<ProfileResolutionMiddleware>.Instance
        );

        await profileMiddleware.Execute(
            requestInfo,
            async () =>
            {
                requestInfo.ResourceActionAuthStrategies = ["OwnershipBased"];
                await CreateMiddleware()
                    .Execute(
                        requestInfo,
                        () =>
                        {
                            handlerCalled = true;
                            return Task.CompletedTask;
                        }
                    );
            }
        );

        handlerCalled.Should().BeFalse();
        requestInfo.DeferredProfileContextFailureResponse!.StatusCode.Should().Be(406);
        requestInfo.FrontendResponse.StatusCode.Should().Be(401);
        A.CallTo(() => configurationProvider.GetApplicationByClientIdAsync(ClientId, Tenant))
            .MustHaveHappenedOnceExactly();
    }

    private static ApplicationContextRequirementMiddleware CreateMiddleware() =>
        new(NullLogger<ApplicationContextRequirementMiddleware>.Instance);

    private static IApplicationContextProvider CreateProvider(ApplicationContextResult result)
    {
        var provider = A.Fake<IApplicationContextProvider>();
        A.CallTo(() => provider.GetApplicationByClientIdAsync(ClientId, Tenant)).Returns(result);
        return provider;
    }

    private static RequestInfo CreateRequestInfo(RequestMethod method, IApplicationContextProvider provider)
    {
        IServiceProvider scopedServiceProvider = new ServiceCollection()
            .AddSingleton(provider)
            .BuildServiceProvider();
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Form: null,
            Headers: [],
            QueryParameters: [],
            TraceId: new TraceId("application-context-requirement"),
            RouteQualifiers: [],
            Tenant: Tenant
        );

        return new RequestInfo(frontendRequest, method, scopedServiceProvider)
        {
            ClientAuthorizations = new ClientAuthorizations(
                TokenId: "token-id",
                ClientId,
                ClaimSetName: "claim-set",
                EducationOrganizationIds: [],
                NamespacePrefixes: [],
                DataStoreIds: []
            ),
        };
    }

    private static HybridCache CreateHybridCache()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }
}
