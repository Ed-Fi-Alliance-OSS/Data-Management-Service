// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Identity;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.ResourceLoadOrder;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Validation;
using EdFi.DataManagementService.Identity;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Polly;

namespace EdFi.DataManagementService.Core.Tests.Unit.Identity;

/// <summary>
/// Proves A15: cancelling an identity request while it is blocked inside one of the four CMS lookups
/// (token acquisition, tenant retrieval, application lookup, claim retrieval) propagates
/// <see cref="OperationCanceledException" /> out of the <see cref="ApiService" /> facade, never invokes
/// the request-scoped <see cref="IIdentityService" />, and never lets a concurrent live caller of the
/// same lookup share the cancelled caller's fate. Built over a real <see cref="ApiService" /> and a
/// real chain of CMS providers (<see cref="ConfigurationServiceTokenHandler" />,
/// <see cref="ConfigurationServiceDataStoreProvider" />, <see cref="ConfigurationServiceApplicationProvider" />
/// / <see cref="CachedApplicationContextProvider" />, <see cref="ConfigurationServiceClaimSetProvider" />
/// / <see cref="CachedClaimSetProvider" />) over a gated transport, following
/// <see cref="ApiServiceWriteCancellationTests" />'s ServiceCollection shape and the blocking-handler
/// precedents in <see cref="Configuration.ConfigurationServiceDataStoreProviderTests" /> and
/// <see cref="Security.ConfigurationServiceClaimSetProviderHeaderTests" />.
/// </summary>
[TestFixture]
public class IdentityCmsCancellationTests
{
    private const string BearerToken = "identity-cms-cancellation-token";
    private const string ClientId = "identity-cms-cancellation-client";
    private const string TenantName = "IdentityCmsCancellationTenant";
    private const string ClaimSetName = "IdentityCmsCancellationClaimSet";
    private const string CmsBaseAddress = "https://cms.example.com/";
    private static readonly Guid _stableClientUuid = Guid.Parse("22222222-2222-4222-8222-222222222222");

    [Test]
    public async Task Cancellation_propagates_when_blocked_at_token_acquisition() =>
        await AssertCancellationPropagatesAndPairedWaiterCompletes("connect/token");

    [Test]
    public async Task Cancellation_propagates_when_blocked_at_tenant_retrieval() =>
        await AssertCancellationPropagatesAndPairedWaiterCompletes("v3/tenants/");

    [Test]
    public async Task Cancellation_propagates_when_blocked_at_application_lookup() =>
        await AssertCancellationPropagatesAndPairedWaiterCompletes("v3/apiClients/");

    [Test]
    public async Task Cancellation_propagates_when_blocked_at_claim_retrieval() =>
        await AssertCancellationPropagatesAndPairedWaiterCompletes("v3/authorizationMetadata");

    /// <summary>
    /// Drives one gate scenario end to end: a cancelled caller observes propagation and never touches
    /// the identity service, then a paired live waiter on the same lookup completes normally once the
    /// gate opens.
    /// </summary>
    private static async Task AssertCancellationPropagatesAndPairedWaiterCompletes(string gateUrlSubstring)
    {
        (ApiService apiService, GatedCmsHandler handler, IIdentityService identityService) = BuildApiService(
            gateUrlSubstring
        );

        using var cancelledCallerSource = new CancellationTokenSource();

        Task<IFrontendResponse> cancelledCallerTask = apiService.IdentityGetById(
            BuildFrontendRequest("uid-cancelled"),
            "uid-cancelled",
            cancelledCallerSource.Token
        );

        await handler.GateReached.WaitAsync(TimeSpan.FromSeconds(5));

        // Started while the gate is held, so this paired request joins the same in-flight CMS lookup
        // (or queues behind the same per-key lock) rather than starting an independent one.
        Task<IFrontendResponse> liveWaiterTask = apiService.IdentityGetById(
            BuildFrontendRequest("uid-live"),
            "uid-live",
            CancellationToken.None
        );

        // A short real-time settle so the live waiter has genuinely joined before the gate is cancelled
        // and released, matching the interleaving-wait precedent in CachedApplicationContextProviderTests
        // and IdentityTenantSnapshotTests.
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        await cancelledCallerSource.CancelAsync();

        Func<Task> cancelledAct = async () => await cancelledCallerTask;
        await cancelledAct.Should().ThrowAsync<OperationCanceledException>();

        // The cancelled caller never reached the capability gate, so the identity service was never
        // touched - not even its Capabilities getter.
        A.CallTo(identityService).MustNotHaveHappened();

        handler.ReleaseGate();

        IFrontendResponse liveResponse = await liveWaiterTask.WaitAsync(TimeSpan.FromSeconds(5));

        liveResponse.Should().NotBeNull();
        A.CallTo(() => identityService.Capabilities).MustHaveHappened();
    }

    /// <summary>
    /// Builds a real ApiService over a ServiceCollection carrying every service the identity pipeline
    /// resolves while running, wired to a real CMS provider chain over a gated transport (the shape
    /// ApiServiceWriteCancellationTests.BuildApiService uses for the write pipelines).
    /// </summary>
    private static (
        ApiService ApiService,
        GatedCmsHandler Handler,
        IIdentityService IdentityService
    ) BuildApiService(string gateUrlSubstring)
    {
        var identityService = A.Fake<IIdentityService>();
        A.CallTo(() => identityService.Capabilities).Returns(IdentityCapabilities.None);

        var handler = new GatedCmsHandler(
            gateUrlSubstring,
            request => BuildCannedResponse(request, ClientId, TenantName, ClaimSetName)
        );

        var responseHandler = new ConfigurationServiceResponseHandler(
            NullLogger<ConfigurationServiceResponseHandler>.Instance
        )
        {
            InnerHandler = handler,
        };
        var httpClient = new HttpClient(responseHandler) { BaseAddress = new Uri(CmsBaseAddress) };
        var apiClient = new ConfigurationServiceApiClient(httpClient);
        var configurationServiceContext = new ConfigurationServiceContext(
            "dms-client",
            "dms-secret",
            "fullaccess"
        );
        var cacheSettings = new CacheSettings();

        var cacheServices = new ServiceCollection();
        cacheServices.AddMemoryCache();
        cacheServices.AddHybridCache();
        IServiceProvider cacheServiceProvider = cacheServices.BuildServiceProvider();
        HybridCache hybridCache = cacheServiceProvider.GetRequiredService<HybridCache>();
        IMemoryCache memoryCache = cacheServiceProvider.GetRequiredService<IMemoryCache>();

        var tokenHandler = new ConfigurationServiceTokenHandler(
            hybridCache,
            cacheSettings,
            apiClient,
            NullLogger<ConfigurationServiceTokenHandler>.Instance
        );

        var dataStoreProvider = new ConfigurationServiceDataStoreProvider(
            apiClient,
            tokenHandler,
            configurationServiceContext,
            NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
            A.Fake<IConnectionStringDecryptionService>()
        );

        var applicationProvider = new ConfigurationServiceApplicationProvider(
            apiClient,
            tokenHandler,
            configurationServiceContext,
            NullLogger<ConfigurationServiceApplicationProvider>.Instance
        );

        var cachedApplicationContextProvider = new CachedApplicationContextProvider(
            applicationProvider,
            hybridCache,
            cacheSettings,
            NullLogger<CachedApplicationContextProvider>.Instance
        );

        var claimSetProvider = new ConfigurationServiceClaimSetProvider(
            apiClient,
            tokenHandler,
            configurationServiceContext
        );

        var cachedClaimSetProvider = new CachedClaimSetProvider(
            claimSetProvider,
            memoryCache,
            cacheSettings,
            NullLogger<CachedClaimSetProvider>.Instance
        );

        var jwtValidationService = A.Fake<IJwtValidationService>();
        A.CallTo(() =>
                jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                    BearerToken,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<(ClaimsPrincipal?, ClientAuthorizations?)>(
                    (new ClaimsPrincipal(new ClaimsIdentity()), BuildClientAuthorizations())
                )
            );

        var services = new ServiceCollection();
        services.AddTransient<JwtAuthenticationMiddleware>();
        services.AddSingleton(jwtValidationService);
        services.AddTransient<ILogger<JwtAuthenticationMiddleware>>(_ =>
            NullLogger<JwtAuthenticationMiddleware>.Instance
        );
        services.AddScoped<IApplicationContextProvider>(_ => cachedApplicationContextProvider);
        services.AddScoped<IIdentityService>(_ => identityService);

        var appSettingsOptions = Options.Create(
            new AppSettings
            {
                AllowIdentityUpdateOverrides = "",
                MaskRequestBodyInLogs = false,
                MultiTenancy = true,
            }
        );
        services.AddSingleton(appSettingsOptions);

        IServiceProvider serviceProvider = services.BuildServiceProvider();

        var lifetime = A.Fake<IHostApplicationLifetime>();
        A.CallTo(() => lifetime.ApplicationStopping).Returns(CancellationToken.None);

        var identityTenantSnapshot = new IdentityTenantSnapshot(
            dataStoreProvider,
            TimeProvider.System,
            lifetime,
            NullLogger<IdentityTenantSnapshot>.Instance
        );

        var apiService = new ApiService(
            A.Fake<IApiSchemaProvider>(),
            A.Fake<IEffectiveApiSchemaProvider>(),
            cachedClaimSetProvider,
            A.Fake<IDocumentValidator>(),
            A.Fake<IMatchingDocumentUuidsValidator>(),
            A.Fake<IEqualityConstraintValidator>(),
            A.Fake<IDecimalValidator>(),
            NullLogger<ApiService>.Instance,
            NullLoggerFactory.Instance,
            appSettingsOptions,
            ResiliencePipeline.Empty,
            A.Fake<ResourceLoadOrderCalculator>(),
            serviceProvider,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            cachedClaimSetProvider,
            A.Fake<IResourceDependencyGraphMLFactory>(),
            A.Fake<IProfileService>(),
            new CircuitBreakerSettings(),
            identityTenantSnapshot
        );

        return (apiService, handler, identityService);
    }

    private static ClientAuthorizations BuildClientAuthorizations() =>
        new(
            TokenId: "identity-cms-cancellation-token-id",
            ClientId: ClientId,
            ClaimSetName: ClaimSetName,
            EducationOrganizationIds: [],
            NamespacePrefixes: [],
            DataStoreIds: []
        );

    private static FrontendRequest BuildFrontendRequest(string uniqueId) =>
        new(
            Path: $"/identity/v2/identities/{uniqueId}",
            Body: null,
            Form: null,
            Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Bearer {BearerToken}",
            },
            QueryParameters: [],
            TraceId: new TraceId($"identity-cms-cancellation-{uniqueId}"),
            RouteQualifiers: [],
            Tenant: TenantName
        );

    /// <summary>
    /// The canned success body for every CMS call this suite's identity request makes, keyed by path.
    /// Every call that is not gated answers immediately with this, so only the one chosen lookup ever
    /// blocks.
    /// </summary>
    private static HttpResponseMessage BuildCannedResponse(
        HttpRequestMessage request,
        string clientId,
        string tenant,
        string claimSetName
    )
    {
        string path = request.RequestUri?.AbsolutePath ?? string.Empty;

        if (path.Contains("connect/token", StringComparison.Ordinal))
        {
            return JsonResponse(
                new
                {
                    access_token = "cms-test-token",
                    token_type = "bearer",
                    expires_in = 300,
                }
            );
        }

        if (path.Contains("v3/tenants/", StringComparison.Ordinal))
        {
            return JsonResponse(new[] { new { Id = 1L, Name = tenant } });
        }

        if (path.Contains("v3/apiClients/", StringComparison.Ordinal))
        {
            return JsonResponse(
                new
                {
                    id = 1L,
                    applicationId = 1L,
                    clientId,
                    clientUuid = _stableClientUuid,
                    dataStoreIds = Array.Empty<long>(),
                    ownershipTokenIds = Array.Empty<short>(),
                }
            );
        }

        if (path.Contains("v3/authorizationMetadata", StringComparison.Ordinal))
        {
            return JsonResponse(
                new[]
                {
                    new
                    {
                        claimSetName,
                        claims = new[]
                        {
                            new
                            {
                                name = $"{Conventions.EdFiOdsServiceClaimBaseUri}/identity",
                                authorizationId = 1,
                            },
                        },
                        authorizations = new[]
                        {
                            new
                            {
                                id = 1,
                                actions = new[]
                                {
                                    new
                                    {
                                        name = "Create",
                                        authorizationStrategies = new[]
                                        {
                                            new
                                            {
                                                name = AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                                            },
                                        },
                                    },
                                    new
                                    {
                                        name = "Read",
                                        authorizationStrategies = new[]
                                        {
                                            new
                                            {
                                                name = AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                }
            );
        }

        throw new InvalidOperationException(
            $"IdentityCmsCancellationTests received an unexpected CMS request to {path}."
        );
    }

    private static HttpResponseMessage JsonResponse(object body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

    /// <summary>
    /// A CMS transport double that blocks every request whose path contains a chosen gate substring
    /// until the test releases it, and answers every other CMS request immediately from a canned
    /// responder. Modeled on BlockingDataStoreHttpMessageHandler /
    /// BlockingTenantsHttpMessageHandler (ConfigurationServiceDataStoreProviderTests) and
    /// GatedRecordingHandler (ConfigurationServiceClaimSetProviderHeaderTests), generalized to a single
    /// caller-chosen gate and an explicit release so a paired live waiter can be proven to complete
    /// normally once it opens.
    /// </summary>
    private sealed class GatedCmsHandler(
        string gateUrlSubstring,
        Func<HttpRequestMessage, HttpResponseMessage> respond
    ) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gateReached = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _gateReleased = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        /// <summary>Completes the first time a request matching the gate substring arrives.</summary>
        public Task GateReached => _gateReached.Task;

        /// <summary>Releases every request currently blocked at the gate, and every later one.</summary>
        public void ReleaseGate() => _gateReleased.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.Contains(gateUrlSubstring, StringComparison.Ordinal))
            {
                _gateReached.TrySetResult();
                await _gateReleased.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return respond(request);
        }
    }
}
