// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Identity;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

/// <summary>
/// Drives the identity async find/results round trip through the real IApiService and Core identity
/// pipelines, with only the plugin boundary (IIdentityService) and the CMS providers faked. Proves the
/// emitted Location is fetchable end to end (B6) and that a provider-supplied token DMS cannot safely
/// compose into a poll path is refused before the 202 rather than surfacing a transport 414 on the
/// follow-up request.
/// </summary>
[TestFixture]
[NonParallelizable]
public class IdentityLocationRoundTripTests
{
    private const string ClaimSetName = "IdentityRoundTrip-ClaimSet";
    private const string ClientId = "identity-round-trip-client";
    private const string Tenant = "tenant-a";

    private static WebApplicationFactory<Program> CreateFactory(
        RecordingIdentityService identityService,
        int? maxRequestLineSize = null
    )
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:EnableIdentityManagement"] = "true",
                            ["AppSettings:MultiTenancy"] = "true",
                            ["AppSettings:RouteQualifierSegments"] = "districtId,schoolYear",
                        }
                    );
                }
            );
            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);

                var dataStoreProvider = A.Fake<IDataStoreProvider>();
                A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                    .Returns(new List<string> { Tenant });
                services.AddTransient(_ => dataStoreProvider);

                var jwtValidationService = A.Fake<IJwtValidationService>();
                var principal = new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim("client_id", ClientId)], "test")
                );
                var clientAuthorizations = new ClientAuthorizations(
                    TokenId: "round-trip-token",
                    ClientId: ClientId,
                    ClaimSetName: ClaimSetName,
                    EducationOrganizationIds: [],
                    NamespacePrefixes: [],
                    DataStoreIds: []
                );
                A.CallTo(() =>
                        jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                            A<string>._,
                            A<CancellationToken>._
                        )
                    )
                    .Returns(
                        Task.FromResult(
                            ((ClaimsPrincipal?)principal, (ClientAuthorizations?)clientAuthorizations)
                        )
                    );
                A.CallTo(() =>
                        jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                            A<string>._,
                            A<int>._,
                            A<CancellationToken>._
                        )
                    )
                    .Returns(
                        Task.FromResult(
                            ((ClaimsPrincipal?)principal, (ClientAuthorizations?)clientAuthorizations)
                        )
                    );

                var applicationContextProvider = A.Fake<IApplicationContextProvider>();
                var applicationContextResult = new ApplicationContextResult.Success(
                    new ApplicationContext(
                        Id: 1,
                        ApplicationId: 1,
                        ClientId: ClientId,
                        ClientUuid: Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        DataStoreIds: [],
                        CreatorOwnershipTokenId: null,
                        OwnershipTokenIds: []
                    )
                );
                A.CallTo(() =>
                        applicationContextProvider.GetApplicationByClientIdAsync(
                            A<string>._,
                            A<string?>._,
                            A<CancellationToken>._
                        )
                    )
                    .Returns(applicationContextResult);

                services.RemoveAll<IJwtValidationService>();
                services.RemoveAll<IApplicationContextProvider>();
                services.RemoveAll<IClaimSetProvider>();

                services.AddSingleton(jwtValidationService);
                services.AddSingleton(applicationContextProvider);
                services.AddSingleton<IClaimSetProvider>(new IdentityGrantingClaimSetProvider());

                // IIdentityService is registered scoped (not singleton) per B6, with the real
                // IApiService and identity pipelines resolving it once per request from the
                // request's own scope, exactly as production does.
                services.Replace(ServiceDescriptor.Scoped<IIdentityService>(_ => identityService));

                if (maxRequestLineSize is int limit)
                {
                    services.Configure<KestrelServerOptions>(options =>
                        options.Limits.MaxRequestLineSize = limit
                    );
                }
            });
        });
    }

    private static HttpRequestMessage FindRequest() =>
        new(HttpMethod.Post, "/tenant-a/255901/2026/identity/v2/identities/find")
        {
            Content = new StringContent("""["605943412"]""", Encoding.UTF8, "application/json"),
        };

    [Test]
    public async Task It_follows_the_emitted_location_and_the_fake_receives_the_same_token()
    {
        var identityService = new RecordingIdentityService("round-trip-token-abc123");
        await using var factory = CreateFactory(identityService);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "round-trip-token"
        );

        using var findRequest = FindRequest();
        var findResponse = await client.SendAsync(findRequest);

        findResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        findResponse.Headers.Location.Should().NotBeNull();
        findResponse
            .Headers.Location!.ToString()
            .Should()
            .Be(
                "http://localhost/tenant-a/255901/2026/identity/v2/identities/results/round-trip-token-abc123"
            );

        var resultsResponse = await client.GetAsync(findResponse.Headers.Location);

        resultsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        identityService
            .ResultsTokensReceived.Should()
            .ContainSingle()
            .Which.Should()
            .Be("round-trip-token-abc123");
    }

    [Test]
    public async Task An_oversized_token_is_refused_before_the_202_with_no_location_and_no_414_anywhere()
    {
        // Legal on its own (well under the 1024-character escaped ceiling and free of reserved
        // characters), but overflows the small request-line budget this host is configured with -
        // proving the composed-path check, not the character-ceiling check, is what fires here.
        string oversizedToken = new('a', 200);
        var identityService = new RecordingIdentityService(oversizedToken);
        await using var factory = CreateFactory(identityService, maxRequestLineSize: 80);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "round-trip-token"
        );

        using var findRequest = FindRequest();
        var findResponse = await client.SendAsync(findRequest);

        findResponse.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        findResponse.Headers.Location.Should().BeNull();
        identityService.ResultsTokensReceived.Should().BeEmpty();
    }

    private sealed class IdentityGrantingClaimSetProvider : IClaimSetProvider
    {
        public Task<IList<ClaimSet>> GetAllClaimSets(
            string? tenant = null,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult<IList<ClaimSet>>([
                new ClaimSet(
                    Name: ClaimSetName,
                    ResourceClaims:
                    [
                        new ResourceClaim(
                            Name: $"{Conventions.EdFiOdsServiceClaimBaseUri}/identity",
                            Action: "Read",
                            AuthorizationStrategies:
                            [
                                new AuthorizationStrategy(
                                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                                ),
                            ]
                        ),
                        new ResourceClaim(
                            Name: $"{Conventions.EdFiOdsServiceClaimBaseUri}/identity",
                            Action: "Create",
                            AuthorizationStrategies:
                            [
                                new AuthorizationStrategy(
                                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                                ),
                            ]
                        ),
                    ]
                ),
            ]);
        }
    }

    /// <summary>
    /// The plugin boundary double: hands back a fixed request token from FindAsync and records every
    /// token ResultsAsync is called with, so the test can assert the exact token that reached the
    /// Location header is the one redeemed on the follow-up poll.
    /// </summary>
    private sealed class RecordingIdentityService(string token) : IIdentityService
    {
        public List<string> ResultsTokensReceived { get; } = [];

        public IdentityCapabilities Capabilities => IdentityCapabilities.Find | IdentityCapabilities.Results;

        public Task<IdentityResult> CreateAsync(
            JsonObject request,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Not exercised by this round trip.");

        public Task<IdentityResult> GetByIdAsync(
            string uniqueId,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Not exercised by this round trip.");

        public Task<IdentityAsyncResult> FindAsync(
            IReadOnlyList<string> uniqueIds,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new IdentityAsyncResult { Status = IdentityResultStatus.Success, RequestToken = token }
            );

        public Task<IdentityAsyncResult> SearchAsync(
            IReadOnlyList<JsonObject> requests,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Not exercised by this round trip.");

        public Task<IdentityResult> ResultsAsync(
            string requestToken,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        )
        {
            ResultsTokensReceived.Add(requestToken);
            return Task.FromResult(
                new IdentityResult
                {
                    Status = IdentityResultStatus.Success,
                    Payload = new JsonObject
                    {
                        ["Status"] = "Complete",
                        ["SearchResponses"] = new JsonArray(),
                    },
                }
            );
        }
    }
}
