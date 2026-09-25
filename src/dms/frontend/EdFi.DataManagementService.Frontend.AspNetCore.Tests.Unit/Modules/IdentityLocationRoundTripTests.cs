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
using EdFi.DataManagementService.Core.Response;
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
        IIdentityService identityService,
        int? maxRequestLineSize = null,
        bool multiTenancy = true,
        string routeQualifierSegments = "districtId,schoolYear",
        string? pathBase = null
    )
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    Dictionary<string, string?> inMemorySettings = new()
                    {
                        ["AppSettings:EnableIdentityManagement"] = "true",
                        ["AppSettings:MultiTenancy"] = multiTenancy ? "true" : "false",
                        ["AppSettings:RouteQualifierSegments"] = routeQualifierSegments,
                    };
                    if (pathBase is not null)
                    {
                        inMemorySettings["AppSettings:PathBase"] = pathBase;
                    }
                    configuration.AddInMemoryCollection(inMemorySettings);
                }
            );
            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);

                var dataStoreProvider = A.Fake<IDataStoreProvider>();
                A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                    .Returns(new List<string> { Tenant });
                // Startup loads data stores for single-tenancy through this same overload with a
                // null tenant (Program.cs InitializeDataStoresForSingleTenancy); an empty result is
                // fatal (Program.cs throws, and the real IStartupProcessExit this host never
                // replaces then calls Environment.Exit, taking the whole test process down with it).
                A.CallTo(() => dataStoreProvider.LoadDataStores(A<string?>.Ignored, A<CancellationToken>._))
                    .Returns(
                        new List<DataStore> { new(1, "Test", "TestInstance", "test-connection-string", []) }
                    );
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

    [TestFixture]
    public class Given_A_Find_Request_Followed_By_Its_Emitted_Location
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _findResponse = null!;
        private HttpResponseMessage _resultsResponse = null!;
        private RecordingIdentityService _identityService = null!;

        [SetUp]
        public async Task Setup()
        {
            _identityService = new RecordingIdentityService("round-trip-token-abc123");
            _factory = CreateFactory(_identityService);
            _client = _factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
            );
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                "round-trip-token"
            );

            using var findRequest = new HttpRequestMessage(
                HttpMethod.Post,
                "/tenant-a/255901/2026/identity/v2/identities/find"
            )
            {
                Content = new StringContent("""["605943412"]""", Encoding.UTF8, "application/json"),
            };
            _findResponse = await _client.SendAsync(findRequest);

            _resultsResponse = await _client.GetAsync(_findResponse.Headers.Location);
        }

        [TearDown]
        public async Task TearDown()
        {
            _findResponse.Dispose();
            _resultsResponse.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_returns_202_accepted_for_the_find_request()
        {
            _findResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        [Test]
        public void It_emits_the_expected_results_location()
        {
            _findResponse.Headers.Location.Should().NotBeNull();
            _findResponse
                .Headers.Location!.ToString()
                .Should()
                .Be(
                    "http://localhost/tenant-a/255901/2026/identity/v2/identities/results/round-trip-token-abc123"
                );
        }

        [Test]
        public void It_returns_200_when_the_emitted_location_is_followed()
        {
            _resultsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void It_redeems_the_same_token_the_location_carried()
        {
            _identityService
                .ResultsTokensReceived.Should()
                .ContainSingle()
                .Which.Should()
                .Be("round-trip-token-abc123");
        }
    }

    [TestFixture]
    public class Given_An_Oversized_Token_That_Overflows_The_Request_Line_Budget
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _findResponse = null!;
        private RecordingIdentityService _identityService = null!;

        [SetUp]
        public async Task Setup()
        {
            // Legal on its own (well under the 1024-character escaped ceiling and free of reserved
            // characters), but overflows the small request-line budget this host is configured with -
            // proving the composed-path check, not the character-ceiling check, is what fires here.
            string oversizedToken = new('a', 200);
            _identityService = new RecordingIdentityService(oversizedToken);
            _factory = CreateFactory(_identityService, maxRequestLineSize: 80);
            _client = _factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
            );
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                "round-trip-token"
            );

            using var findRequest = new HttpRequestMessage(
                HttpMethod.Post,
                "/tenant-a/255901/2026/identity/v2/identities/find"
            )
            {
                Content = new StringContent("""["605943412"]""", Encoding.UTF8, "application/json"),
            };
            _findResponse = await _client.SendAsync(findRequest);
        }

        [TearDown]
        public async Task TearDown()
        {
            _findResponse.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_is_refused_with_502_bad_gateway()
        {
            _findResponse.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }

        [Test]
        public void It_returns_no_location()
        {
            _findResponse.Headers.Location.Should().BeNull();
        }

        [Test]
        public void It_never_reaches_the_identity_service_results_call()
        {
            _identityService.ResultsTokensReceived.Should().BeEmpty();
        }
    }

    /// <summary>
    /// D9/task 20: BuildIdentityTemplatePath (AspNetCoreFrontend) and ComputeIdentityPollPathPrefix
    /// (Core ApiService) must locate the "/identity/v2/identities" route segment case-insensitively,
    /// since ASP.NET Core routing itself matches case-insensitively. Before the fix, an Ordinal search
    /// for the lowercase literal against this mixed-case request text fails, silently dropping the
    /// "/tenant-a/255901/2026" prefix from the emitted Location.
    /// </summary>
    [TestFixture]
    public class Given_A_Mixed_Case_Find_Route_Segment
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _findResponse = null!;

        [SetUp]
        public async Task Setup()
        {
            var identityService = new RecordingIdentityService("mixed-case-find-token");
            _factory = CreateFactory(identityService);
            _client = _factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
            );
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                "round-trip-token"
            );

            using var findRequest = new HttpRequestMessage(
                HttpMethod.Post,
                "/tenant-a/255901/2026/Identity/V2/Identities/find"
            )
            {
                Content = new StringContent("""["605943412"]""", Encoding.UTF8, "application/json"),
            };
            _findResponse = await _client.SendAsync(findRequest);
        }

        [TearDown]
        public async Task TearDown()
        {
            _findResponse.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_returns_202_accepted()
        {
            _findResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        [Test]
        public void It_keeps_the_tenant_prefix_in_the_location()
        {
            _findResponse.Headers.Location.Should().NotBeNull();
            _findResponse
                .Headers.Location!.ToString()
                .Should()
                .Be(
                    "http://localhost/tenant-a/255901/2026/identity/v2/identities/results/mixed-case-find-token"
                );
        }
    }

    /// <summary>
    /// The search counterpart to the find scenario above.
    /// </summary>
    [TestFixture]
    public class Given_A_Mixed_Case_Search_Route_Segment
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _searchResponse = null!;

        [SetUp]
        public async Task Setup()
        {
            var identityService = new RecordingIdentityService("mixed-case-search-token");
            _factory = CreateFactory(identityService);
            _client = _factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
            );
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                "round-trip-token"
            );

            using var searchRequest = new HttpRequestMessage(
                HttpMethod.Post,
                "/tenant-a/255901/2026/IDENTITY/v2/IDENTITIES/search"
            )
            {
                Content = new StringContent("""[{"firstName":"A"}]""", Encoding.UTF8, "application/json"),
            };
            _searchResponse = await _client.SendAsync(searchRequest);
        }

        [TearDown]
        public async Task TearDown()
        {
            _searchResponse.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_returns_202_accepted()
        {
            _searchResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        [Test]
        public void It_keeps_the_tenant_prefix_in_the_location()
        {
            _searchResponse.Headers.Location.Should().NotBeNull();
            _searchResponse
                .Headers.Location!.ToString()
                .Should()
                .Be(
                    "http://localhost/tenant-a/255901/2026/identity/v2/identities/results/mixed-case-search-token"
                );
        }
    }

    /// <summary>
    /// The same casing defect on the results-poll route: an Incomplete answer's 200 also carries a
    /// Location (the current poll path), which must keep the tenant prefix.
    /// </summary>
    [TestFixture]
    public class Given_A_Mixed_Case_Results_Poll_Route_Segment
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _resultsResponse = null!;

        [SetUp]
        public async Task Setup()
        {
            var identityService = new IncompleteResultsIdentityService();
            _factory = CreateFactory(identityService);
            _client = _factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
            );
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                "round-trip-token"
            );

            _resultsResponse = await _client.GetAsync(
                "/tenant-a/255901/2026/Identity/V2/Identities/results/job-token-9"
            );
        }

        [TearDown]
        public async Task TearDown()
        {
            _resultsResponse.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_returns_200()
        {
            _resultsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void It_keeps_the_tenant_prefix_in_the_location()
        {
            _resultsResponse.Headers.Location.Should().NotBeNull();
            _resultsResponse
                .Headers.Location!.ToString()
                .Should()
                .Be("http://localhost/tenant-a/255901/2026/identity/v2/identities/results/job-token-9");
        }
    }

    /// <summary>
    /// D9/task 20: ToResult (AspNetCoreFrontend) slices HttpRequest.UrlWithPathSegment() - built from
    /// PathString.ToString(), the escaped form - by the decoded dmsPath's length. A token carrying a
    /// character that needed escaping (a space and a non-ASCII letter here) makes the escaped and
    /// decoded forms different lengths, so before the fix the slice cuts the wrong number of
    /// characters and truncates the Location's host/base. Single-tenant, no route qualifiers, so the
    /// escaping defect is isolated from the casing defect covered above.
    /// </summary>
    [TestFixture]
    public class Given_A_Results_Token_With_A_Space_And_Unicode
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _resultsResponse = null!;

        [SetUp]
        public async Task Setup()
        {
            var identityService = new IncompleteResultsIdentityService();
            _factory = CreateFactory(identityService, multiTenancy: false, routeQualifierSegments: "");
            _client = _factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
            );
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                "round-trip-token"
            );

            _resultsResponse = await _client.GetAsync("/identity/v2/identities/results/job%20%C3%A91");
        }

        [TearDown]
        public async Task TearDown()
        {
            _resultsResponse.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_returns_200()
        {
            _resultsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void It_preserves_the_escaped_token_in_the_location()
        {
            _resultsResponse.Headers.Location.Should().NotBeNull();
            // Uri.ToString() unescapes reserved characters for display (the space and the accented
            // letter would both come back decoded); AbsoluteUri is the canonical escaped form, matching
            // what the server actually wrote onto the wire.
            _resultsResponse
                .Headers.Location!.AbsoluteUri.Should()
                .Be("http://localhost/identity/v2/identities/results/job%20%C3%A91");
        }
    }

    /// <summary>
    /// D9/task 20: BuildIdentityTemplatePath must copy the route-qualifier prefix from the request
    /// path's escaped form, not the decoded RouteValues-derived form, or a qualifier value carrying a
    /// reserved character (a space here) reaches the Location unescaped - an invalid path segment.
    /// Proves both that the emitted Location carries the escaped value and that the escaped Location
    /// is itself a working URL by following it back to the results endpoint.
    /// </summary>
    [TestFixture]
    public class Given_A_Route_Qualifier_Value_With_A_Space
    {
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _client = null!;
        private HttpResponseMessage _findResponse = null!;
        private HttpResponseMessage _resultsResponse = null!;
        private RecordingIdentityService _identityService = null!;

        [SetUp]
        public async Task Setup()
        {
            _identityService = new RecordingIdentityService("qualifier-space-token");
            _factory = CreateFactory(_identityService);
            _client = _factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
            );
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                "round-trip-token"
            );

            using var findRequest = new HttpRequestMessage(
                HttpMethod.Post,
                "/tenant-a/district%201/2026/identity/v2/identities/find"
            )
            {
                Content = new StringContent("""["605943412"]""", Encoding.UTF8, "application/json"),
            };
            _findResponse = await _client.SendAsync(findRequest);

            _resultsResponse = await _client.GetAsync(_findResponse.Headers.Location);
        }

        [TearDown]
        public async Task TearDown()
        {
            _findResponse.Dispose();
            _resultsResponse.Dispose();
            _client.Dispose();
            await _factory.DisposeAsync();
        }

        [Test]
        public void It_returns_202_accepted()
        {
            _findResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        [Test]
        public void It_escapes_the_qualifier_value_in_the_location()
        {
            _findResponse.Headers.Location.Should().NotBeNull();
            // Uri.ToString() unescapes reserved characters for display; AbsoluteUri is the canonical
            // escaped form, matching what the server actually wrote onto the wire.
            _findResponse.Headers.Location!.AbsoluteUri.Should().Contain("district%201");
        }

        [Test]
        public void It_does_not_leave_the_qualifier_value_unescaped_in_the_location()
        {
            _findResponse.Headers.Location!.AbsoluteUri.Should().NotContain("district 1");
        }

        [Test]
        public void It_routes_back_to_the_results_endpoint()
        {
            _resultsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Test]
        public void It_redeems_the_same_token_the_location_carried()
        {
            _identityService
                .ResultsTokensReceived.Should()
                .ContainSingle()
                .Which.Should()
                .Be("qualifier-space-token");
        }
    }

    /// <summary>
    /// Task 21: <c>FromIdentityRequest</c>/<c>ResolveMaxRequestLineSize</c> (AspNetCoreFrontend) must
    /// reduce the request-line budget passed to Core by PathBase's length. Core's composed-path check
    /// (<see cref="Identity.IdentityRequestTokenRule"/>) measures the prefix from
    /// <see cref="HttpRequest.Path"/>, which excludes PathBase, but the real request line the client
    /// sends on the follow-up poll - and the one Kestrel enforces <c>MaxRequestLineSize</c> against -
    /// includes it (<c>RootUrl()</c>; <c>UsePathBase</c> in Program.cs). Without the fix, a token sized
    /// to just fit the unqualified budget passes here but overflows the real request line by exactly
    /// PathBase's length once the poll path is fetched under the PathBase.
    ///
    /// All three fixtures below share single-tenant, no-route-qualifier routing, so
    /// <c>composedPathPrefix</c> is the fixed <c>"/identity/v2/identities/results"</c> (31 characters),
    /// making <c>IdentityRequestTokenRule.Evaluate</c>'s arithmetic exact:
    /// <c>composedRequestLineLength = 31 + 1 + escaped.Length + "GET ".Length + " HTTP/1.1".Length
    /// = 45 + escaped.Length</c>. With <see cref="RequestLineLimit"/> (100) configured as the raw
    /// Kestrel <c>MaxRequestLineSize</c>: an <see cref="ExactFitTokenLength"/> (55) token exactly fills
    /// the budget with no PathBase (45 + 55 = 100), and only a <see cref="PathBaseFitTokenLength"/> (51,
    /// four characters shorter) token exactly fills the budget once <see cref="PathBaseSegment"/>
    /// ("/api", 4 characters) is subtracted (100 - 4 = 96; 45 + 51 = 96).
    /// </summary>
    private const int RequestLineLimit = 100;
    private const string PathBaseSegment = "/api";
    private const int ExactFitTokenLength = 55;
    private const int PathBaseFitTokenLength = ExactFitTokenLength - 4;

    [TestFixture]
    public class Given_a_token_that_exactly_fits_the_budget_with_no_PathBase
    {
        private HttpResponseMessage _findResponse = null!;

        [SetUp]
        public async Task Setup()
        {
            string token = new('a', ExactFitTokenLength);
            var identityService = new RecordingIdentityService(token);
            await using var factory = CreateFactory(
                identityService,
                maxRequestLineSize: RequestLineLimit,
                multiTenancy: false,
                routeQualifierSegments: ""
            );
            using var client = factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
            );
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                "round-trip-token"
            );

            using var findRequest = new HttpRequestMessage(HttpMethod.Post, "/identity/v2/identities/find")
            {
                Content = new StringContent("""["605943412"]""", Encoding.UTF8, "application/json"),
            };
            _findResponse = await client.SendAsync(findRequest);
        }

        /// <summary>
        /// Control proving the token size math itself: with no PathBase to account for, the exact-fit
        /// token is accepted and produces a poll Location.
        /// </summary>
        [Test]
        public void It_returns_202_accepted_with_a_location()
        {
            _findResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
            _findResponse.Headers.Location.Should().NotBeNull();
            _findResponse
                .Headers.Location!.ToString()
                .Should()
                .Be(
                    $"http://localhost/identity/v2/identities/results/{new string('a', ExactFitTokenLength)}"
                );
        }
    }

    [TestFixture]
    public class Given_that_same_exact_fit_token_under_a_configured_PathBase
    {
        private HttpResponseMessage _findResponse = null!;

        [SetUp]
        public async Task Setup()
        {
            string token = new('a', ExactFitTokenLength);
            var identityService = new RecordingIdentityService(token);
            await using var factory = CreateFactory(
                identityService,
                maxRequestLineSize: RequestLineLimit,
                multiTenancy: false,
                routeQualifierSegments: "",
                pathBase: PathBaseSegment
            );
            using var client = factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
            );
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                "round-trip-token"
            );

            using var findRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"{PathBaseSegment}/identity/v2/identities/find"
            )
            {
                Content = new StringContent("""["605943412"]""", Encoding.UTF8, "application/json"),
            };
            _findResponse = await client.SendAsync(findRequest);
        }

        /// <summary>
        /// The token that fit at the root now overflows the real request line by exactly PathBase's
        /// four characters once the follow-up poll is composed under "/api", so it must be refused
        /// before the 202 rather than accepted and later fail as a transport-level 414.
        /// </summary>
        [Test]
        public void It_returns_502_bad_gateway()
        {
            _findResponse.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }

        [Test]
        public void It_returns_no_location()
        {
            _findResponse.Headers.Location.Should().BeNull();
        }

        [Test]
        public async Task It_reports_the_token_unusable_problem_type()
        {
            JsonNode body = JsonNode.Parse(await _findResponse.Content.ReadAsStringAsync())!;
            body["type"]!
                .GetValue<string>()
                .Should()
                .Be(IdentityFailureResponse.ProviderContractViolationType);
            body["detail"]!
                .GetValue<string>()
                .Should()
                .Be(
                    "The identity provider returned a request token that cannot be safely composed into a poll path."
                );
        }
    }

    [TestFixture]
    public class Given_a_token_four_characters_shorter_under_the_same_PathBase
    {
        private HttpResponseMessage _findResponse = null!;

        [SetUp]
        public async Task Setup()
        {
            string token = new('a', PathBaseFitTokenLength);
            var identityService = new RecordingIdentityService(token);
            await using var factory = CreateFactory(
                identityService,
                maxRequestLineSize: RequestLineLimit,
                multiTenancy: false,
                routeQualifierSegments: "",
                pathBase: PathBaseSegment
            );
            using var client = factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
            );
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                "round-trip-token"
            );

            using var findRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"{PathBaseSegment}/identity/v2/identities/find"
            )
            {
                Content = new StringContent("""["605943412"]""", Encoding.UTF8, "application/json"),
            };
            _findResponse = await client.SendAsync(findRequest);
        }

        /// <summary>
        /// Four characters shorter is exactly enough to fit the PathBase-reduced budget, proving the
        /// fix accounts for precisely PathBase's length and nothing more.
        /// </summary>
        [Test]
        public void It_returns_202_accepted_with_a_location_under_the_PathBase()
        {
            _findResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
            _findResponse.Headers.Location.Should().NotBeNull();
            _findResponse
                .Headers.Location!.ToString()
                .Should()
                .Be(
                    $"http://localhost{PathBaseSegment}/identity/v2/identities/results/{new string('a', PathBaseFitTokenLength)}"
                );
        }
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
    /// The plugin boundary double: hands back a fixed request token from FindAsync/SearchAsync and
    /// records every token ResultsAsync is called with, so a test can assert the exact token that
    /// reached the Location header is the one redeemed on the follow-up poll.
    /// </summary>
    private sealed class RecordingIdentityService(string token) : IIdentityService
    {
        public List<string> ResultsTokensReceived { get; } = [];

        public IdentityCapabilities Capabilities =>
            IdentityCapabilities.Find | IdentityCapabilities.Search | IdentityCapabilities.Results;

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
        ) =>
            Task.FromResult(
                new IdentityAsyncResult { Status = IdentityResultStatus.Success, RequestToken = token }
            );

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

    /// <summary>
    /// The plugin boundary double for the results-polling casing/escaping tests: always answers
    /// Incomplete regardless of the token in the URL, so a test can hit the results endpoint directly
    /// (no prior find/search call) and assert on the 200's Location.
    /// </summary>
    private sealed class IncompleteResultsIdentityService : IIdentityService
    {
        public IdentityCapabilities Capabilities => IdentityCapabilities.Results;

        public Task<IdentityResult> CreateAsync(
            JsonObject request,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Not exercised by this test.");

        public Task<IdentityResult> GetByIdAsync(
            string uniqueId,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Not exercised by this test.");

        public Task<IdentityAsyncResult> FindAsync(
            IReadOnlyList<string> uniqueIds,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Not exercised by this test.");

        public Task<IdentityAsyncResult> SearchAsync(
            IReadOnlyList<JsonObject> requests,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Not exercised by this test.");

        public Task<IdentityResult> ResultsAsync(
            string requestToken,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new IdentityResult
                {
                    Status = IdentityResultStatus.Incomplete,
                    Payload = new JsonObject { ["Status"] = "InProgress" },
                }
            );
    }
}
