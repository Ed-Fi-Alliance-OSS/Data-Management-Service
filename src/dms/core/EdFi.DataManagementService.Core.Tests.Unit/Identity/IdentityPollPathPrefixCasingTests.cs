// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using System.Text.Json.Nodes;
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
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Core.Validation;
using EdFi.DataManagementService.Identity;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Polly;

namespace EdFi.DataManagementService.Core.Tests.Unit.Identity;

/// <summary>
/// Pins ApiService.ComputeIdentityPollPathPrefix's case-insensitive match against the literal
/// "/identity/v2/identities" route segment (DMS-1515 task 20, design.md D9). In the real frontend
/// pipeline that segment is always canonical lowercase by the time it reaches Core, because
/// BuildIdentityTemplatePath (AspNetCoreFrontend) never copies request text into it - it always emits
/// the literal constant. These tests instead drive the real IApiService facade directly with a
/// FrontendRequest.Path built the way a caller that bypasses the frontend's template building would
/// (for example a differently-cased ".../Identity/V2/Identities/..." segment), which is exactly the
/// shape ComputeIdentityPollPathPrefix's own doc comment calls out as the only way its fallback branch
/// is reached. Before the fix, an Ordinal search for the lowercase literal fails against that input,
/// so the whole tenant/route-qualifier prefix ahead of the segment is silently dropped from the poll
/// Location instead of merely losing its casing. Built over a real ApiService, following
/// IdentityPipelineOrderingTests's BuildApiService shape, with only the identity provider plugin
/// boundary, claim set, and application context faked.
/// </summary>
internal static class IdentityPollPathPrefixCasingTestSupport
{
    public const string BearerToken = "poll-prefix-casing-token";
    public const string ClaimSetName = "PollPrefixCasingClaimSet";
    public const string ClientId = "poll-prefix-casing-client";

    /// <summary>
    /// A stub provider whose next call returns a fixed, pre-configured result, so each fixture drives
    /// one identity operation without a real backing store.
    /// </summary>
    public sealed class ScriptedIdentityService : IIdentityService
    {
        public required IdentityCapabilities Capabilities { get; init; }
        public IdentityAsyncResult? NextAsyncResult { get; set; }
        public IdentityResult? NextResult { get; set; }

        public Task<IdentityResult> CreateAsync(
            JsonObject request,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Not exercised by these tests.");

        public Task<IdentityResult> GetByIdAsync(
            string uniqueId,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Not exercised by these tests.");

        public Task<IdentityAsyncResult> FindAsync(
            IReadOnlyList<string> uniqueIds,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => Task.FromResult(NextAsyncResult!);

        public Task<IdentityAsyncResult> SearchAsync(
            IReadOnlyList<JsonObject> requests,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => Task.FromResult(NextAsyncResult!);

        public Task<IdentityResult> ResultsAsync(
            string requestToken,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => Task.FromResult(NextResult!);
    }

    public static ApiService BuildApiService(IIdentityService identityService)
    {
        var services = new ServiceCollection();

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

        services.AddTransient<JwtAuthenticationMiddleware>();
        services.AddSingleton(jwtValidationService);
        services.AddTransient<ILogger<JwtAuthenticationMiddleware>>(_ =>
            NullLogger<JwtAuthenticationMiddleware>.Instance
        );

        var applicationContextProvider = A.Fake<IApplicationContextProvider>();
        A.CallTo(() =>
                applicationContextProvider.GetApplicationByClientIdAsync(
                    ClientId,
                    A<string?>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<ApplicationContextResult>(
                    new ApplicationContextResult.Success(
                        new ApplicationContext(1, 1, ClientId, Guid.NewGuid(), [], null, [])
                    )
                )
            );
        services.AddScoped(_ => applicationContextProvider);

        services.AddScoped(_ => identityService);

        var claimSetProvider = A.Fake<IClaimSetProvider>();
        A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IList<ClaimSet>>([BuildAuthorizingClaimSet("Read")]));

        var appSettingsOptions = Options.Create(
            new AppSettings
            {
                AllowIdentityUpdateOverrides = "",
                MaskRequestBodyInLogs = false,
                MultiTenancy = false,
            }
        );
        services.AddSingleton(appSettingsOptions);

        var serviceProvider = services.BuildServiceProvider();

        var identityTenantSnapshot = new IdentityTenantSnapshot(
            A.Fake<IDataStoreProvider>(),
            TimeProvider.System,
            A.Fake<IHostApplicationLifetime>(),
            NullLogger<IdentityTenantSnapshot>.Instance
        );

        return new ApiService(
            A.Fake<IApiSchemaProvider>(),
            A.Fake<IEffectiveApiSchemaProvider>(),
            claimSetProvider,
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
            A.Fake<CachedClaimSetProvider>(),
            A.Fake<IResourceDependencyGraphMLFactory>(),
            A.Fake<IProfileService>(),
            new CircuitBreakerSettings(),
            identityTenantSnapshot
        );
    }

    public static ClientAuthorizations BuildClientAuthorizations() =>
        new(
            TokenId: "poll-prefix-casing-token-id",
            ClientId: ClientId,
            ClaimSetName: ClaimSetName,
            EducationOrganizationIds: [],
            NamespacePrefixes: [],
            DataStoreIds: []
        );

    public static ClaimSet BuildAuthorizingClaimSet(string action) =>
        new(
            ClaimSetName,
            [
                new ResourceClaim(
                    $"{Conventions.EdFiOdsServiceClaimBaseUri}/identity",
                    action,
                    [
                        new AuthorizationStrategy(
                            AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                        ),
                    ]
                ),
            ]
        );

    public static FrontendRequest BuildFrontendRequest(string path, JsonNode? parsedBody) =>
        new(
            Path: path,
            Body: null,
            Form: null,
            Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Bearer {BearerToken}",
            },
            QueryParameters: [],
            TraceId: new TraceId("identity-poll-prefix-casing"),
            RouteQualifiers: [],
            Tenant: null,
            ParsedBody: parsedBody
        );
}

/// <summary>
/// A find request whose FrontendRequest.Path carries a differently-cased identity route segment
/// (".../Identity/V2/Identities/find" rather than the canonical lowercase text) must still resolve the
/// segment boundary, so the tenant prefix ahead of it ("/tenantA") reaches the 202's Location instead
/// of being dropped.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_A_Find_Request_Whose_Path_Has_A_Differently_Cased_Identity_Segment
{
    private IFrontendResponse _response = null!;

    [SetUp]
    public async Task SetUp()
    {
        var provider = new IdentityPollPathPrefixCasingTestSupport.ScriptedIdentityService
        {
            Capabilities = IdentityCapabilities.Find | IdentityCapabilities.Results,
            NextAsyncResult = new IdentityAsyncResult
            {
                Status = IdentityResultStatus.Success,
                RequestToken = "poll-prefix-token-1",
            },
        };
        var apiService = IdentityPollPathPrefixCasingTestSupport.BuildApiService(provider);

        _response = await apiService.IdentityFind(
            IdentityPollPathPrefixCasingTestSupport.BuildFrontendRequest(
                "/tenantA/Identity/V2/Identities/find",
                new JsonArray("605943412")
            ),
            CancellationToken.None
        );
    }

    [Test]
    public void It_returns_202_accepted()
    {
        _response.StatusCode.Should().Be(202);
    }

    [Test]
    public void It_keeps_the_tenant_prefix_in_the_Location_path()
    {
        _response
            .LocationHeaderPath.Should()
            .Be("/tenantA/Identity/V2/Identities/results/poll-prefix-token-1");
    }
}

/// <summary>
/// The same case-insensitive segment match as the find scenario above, driven through
/// IApiService.IdentitySearch instead.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_A_Search_Request_Whose_Path_Has_A_Differently_Cased_Identity_Segment
{
    private IFrontendResponse _response = null!;

    [SetUp]
    public async Task SetUp()
    {
        var provider = new IdentityPollPathPrefixCasingTestSupport.ScriptedIdentityService
        {
            Capabilities = IdentityCapabilities.Search | IdentityCapabilities.Results,
            NextAsyncResult = new IdentityAsyncResult
            {
                Status = IdentityResultStatus.Success,
                RequestToken = "poll-prefix-token-2",
            },
        };
        var apiService = IdentityPollPathPrefixCasingTestSupport.BuildApiService(provider);

        _response = await apiService.IdentitySearch(
            IdentityPollPathPrefixCasingTestSupport.BuildFrontendRequest(
                "/tenantA/IDENTITY/v2/IDENTITIES/search",
                new JsonArray(new JsonObject())
            ),
            CancellationToken.None
        );
    }

    [Test]
    public void It_returns_202_accepted()
    {
        _response.StatusCode.Should().Be(202);
    }

    [Test]
    public void It_keeps_the_tenant_prefix_in_the_Location_path()
    {
        _response
            .LocationHeaderPath.Should()
            .Be("/tenantA/IDENTITY/v2/IDENTITIES/results/poll-prefix-token-2");
    }
}

/// <summary>
/// A results poll whose FrontendRequest.Path carries a differently-cased identity route segment and
/// whose provider reports Incomplete must still keep the tenant prefix on the 200's Location, matching
/// the find/search scenarios above.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_An_Incomplete_Results_Poll_Whose_Path_Has_A_Differently_Cased_Identity_Segment
{
    private IFrontendResponse _response = null!;

    [SetUp]
    public async Task SetUp()
    {
        var provider = new IdentityPollPathPrefixCasingTestSupport.ScriptedIdentityService
        {
            Capabilities = IdentityCapabilities.Results,
            NextResult = new IdentityResult
            {
                Status = IdentityResultStatus.Incomplete,
                Payload = new JsonObject { ["status"] = "InProgress" },
            },
        };
        var apiService = IdentityPollPathPrefixCasingTestSupport.BuildApiService(provider);

        _response = await apiService.IdentityResults(
            IdentityPollPathPrefixCasingTestSupport.BuildFrontendRequest(
                "/tenantA/Identity/V2/Identities/results/{token}",
                parsedBody: null
            ),
            "poll-prefix-token-9",
            CancellationToken.None
        );
    }

    [Test]
    public void It_returns_200_ok()
    {
        _response.StatusCode.Should().Be(200);
    }

    [Test]
    public void It_keeps_the_tenant_prefix_in_the_Location_path()
    {
        _response
            .LocationHeaderPath.Should()
            .Be("/tenantA/Identity/V2/Identities/results/poll-prefix-token-9");
    }
}
