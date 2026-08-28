// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Frontend.AspNetCore;
using EdFi.DataManagementService.Frontend.AspNetCore.Modules;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using AppSettings = EdFi.DataManagementService.Frontend.AspNetCore.Configuration.AppSettings;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class CoreEndpointModuleTests
{
    [TestFixture]
    public class Given_BuildRoutePattern_With_No_Multitenancy
    {
        [TestFixture]
        public class Given_No_Route_Qualifiers
        {
            private string _result = string.Empty;

            [SetUp]
            public void Setup()
            {
                _result = CoreEndpointModule.BuildRoutePattern([], multiTenancy: false);
            }

            [Test]
            public void It_should_return_simple_data_path()
            {
                _result.Should().Be("/data/{**dmsPath}");
            }
        }

        [TestFixture]
        public class Given_Single_Route_Qualifier
        {
            private string _result = string.Empty;

            [SetUp]
            public void Setup()
            {
                _result = CoreEndpointModule.BuildRoutePattern(["districtId"], multiTenancy: false);
            }

            [Test]
            public void It_should_return_path_with_qualifier_segment()
            {
                _result.Should().Be("/{districtId}/data/{**dmsPath}");
            }
        }

        [TestFixture]
        public class Given_Multiple_Route_Qualifiers
        {
            private string _result = string.Empty;

            [SetUp]
            public void Setup()
            {
                _result = CoreEndpointModule.BuildRoutePattern(
                    ["districtId", "schoolYear"],
                    multiTenancy: false
                );
            }

            [Test]
            public void It_should_return_path_with_all_qualifier_segments()
            {
                _result.Should().Be("/{districtId}/{schoolYear}/data/{**dmsPath}");
            }
        }
    }

    [TestFixture]
    public class Given_BuildRoutePattern_With_Multitenancy_Enabled
    {
        [TestFixture]
        public class Given_No_Route_Qualifiers
        {
            private string _result = string.Empty;

            [SetUp]
            public void Setup()
            {
                _result = CoreEndpointModule.BuildRoutePattern([], multiTenancy: true);
            }

            [Test]
            public void It_should_return_path_with_tenant_segment()
            {
                _result.Should().Be("/{tenant}/data/{**dmsPath}");
            }
        }

        [TestFixture]
        public class Given_Single_Route_Qualifier
        {
            private string _result = string.Empty;

            [SetUp]
            public void Setup()
            {
                _result = CoreEndpointModule.BuildRoutePattern(["districtId"], multiTenancy: true);
            }

            [Test]
            public void It_should_return_path_with_tenant_before_qualifier()
            {
                _result.Should().Be("/{tenant}/{districtId}/data/{**dmsPath}");
            }
        }

        [TestFixture]
        public class Given_Multiple_Route_Qualifiers
        {
            private string _result = string.Empty;

            [SetUp]
            public void Setup()
            {
                _result = CoreEndpointModule.BuildRoutePattern(
                    ["districtId", "schoolYear"],
                    multiTenancy: true
                );
            }

            [Test]
            public void It_should_return_path_with_tenant_before_all_qualifiers()
            {
                _result.Should().Be("/{tenant}/{districtId}/{schoolYear}/data/{**dmsPath}");
            }
        }
    }

    private const string ItemUuid = "0192ac2c-8f7f-7c2a-9c1d-3f4b5a6c7d8e";

    private static IFrontendResponse FakeCoreMethodNotAllowedResponse(string allow = "GET, POST")
    {
        JsonObject body = new() { ["source"] = "core" };

        var response = A.Fake<IFrontendResponse>();
        A.CallTo(() => response.StatusCode).Returns(405);
        A.CallTo(() => response.Body).Returns(body);
        A.CallTo(() => response.Headers).Returns(new Dictionary<string, string> { ["Allow"] = allow });
        A.CallTo(() => response.ContentType).Returns("application/json; charset=utf-8");
        return response;
    }

    private static IFrontendResponse FakeCoreOkResponse()
    {
        JsonObject body = new() { ["source"] = "core" };

        var response = A.Fake<IFrontendResponse>();
        A.CallTo(() => response.StatusCode).Returns(200);
        A.CallTo(() => response.Body).Returns(body);
        A.CallTo(() => response.Headers).Returns(new Dictionary<string, string>());
        A.CallTo(() => response.ContentType).Returns("application/json");
        return response;
    }

    [Test]
    public async Task It_passes_request_aborted_to_core_get()
    {
        var apiService = A.Fake<IApiService>();
        CancellationToken capturedCancellationToken = default;
        A.CallTo(() => apiService.Get(A<FrontendRequest>._, A<CancellationToken>._))
            .Invokes(
                (FrontendRequest _, CancellationToken cancellationToken) =>
                    capturedCancellationToken = cancellationToken
            )
            .Returns(Task.FromResult(FakeCoreOkResponse()));
        using var requestAbortedSource = new CancellationTokenSource();
        var httpContext = new DefaultHttpContext { RequestAborted = requestAbortedSource.Token };
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("api.example.test");
        httpContext.Request.Path = "/data/ed-fi/schools";

        await AspNetCoreFrontend.Get(
            httpContext,
            apiService,
            "ed-fi/schools",
            Options.Create(
                new AppSettings
                {
                    AuthenticationService = "test",
                    Datastore = "postgresql",
                    CorrelationIdHeader = "X-Correlation-ID",
                }
            )
        );

        capturedCancellationToken.Should().Be(requestAbortedSource.Token);
    }

    /// <summary>
    /// A fake whose every request entry point answers 200, so a test can assert which entry point
    /// the router selected without any of them failing for want of a configured response.
    /// </summary>
    private static IApiService FakeApiServiceAnsweringEveryVerb()
    {
        var apiService = A.Fake<IApiService>();
        A.CallTo(() => apiService.Get(A<FrontendRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(FakeCoreOkResponse()));
        A.CallTo(() => apiService.Upsert(A<FrontendRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(FakeCoreOkResponse()));
        A.CallTo(() => apiService.UpdateById(A<FrontendRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(FakeCoreOkResponse()));
        A.CallTo(() => apiService.DeleteById(A<FrontendRequest>._))
            .Returns(Task.FromResult(FakeCoreOkResponse()));
        A.CallTo(() => apiService.GetTrackedChanges(A<FrontendRequest>._))
            .Returns(Task.FromResult(FakeCoreOkResponse()));
        A.CallTo(() => apiService.MethodNotAllowed(A<FrontendRequest>._, A<string>._))
            .Returns(Task.FromResult(FakeCoreMethodNotAllowedResponse()));
        A.CallTo(() => apiService.MethodNotAllowedForTrackedChange(A<FrontendRequest>._, A<string>._))
            .Returns(Task.FromResult(FakeCoreMethodNotAllowedResponse(allow: "GET")));
        return apiService;
    }

    private static WebApplicationFactory<Program> CreateFactory(
        IApiService apiService,
        Dictionary<string, string?>? configuration = null
    )
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            if (configuration is not null)
            {
                builder.ConfigureAppConfiguration(
                    (context, configurationBuilder) =>
                        configurationBuilder.AddInMemoryCollection(configuration)
                );
            }
            builder.ConfigureServices(collection =>
            {
                TestMockHelper.AddEssentialMocks(collection);
                collection.AddTransient(x => apiService);
            });
        });
    }

    /// <summary>
    /// Exercises the real Program.cs route table with a faked IApiService, so these tests prove how
    /// the request is routed rather than what the response body says - the fake, not Core, supplies
    /// the response here.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Given_A_Data_Route_Request_With_An_Unsupported_Method
    {
        [TestCase("/data/ed-fi/schools", "/ed-fi/schools", TestName = "Collection route")]
        [TestCase($"/data/ed-fi/schools/{ItemUuid}", $"/ed-fi/schools/{ItemUuid}", TestName = "Item route")]
        public async Task It_hands_the_request_to_core_with_the_real_method_name(
            string requestUrl,
            string expectedDmsPath
        )
        {
            var apiService = A.Fake<IApiService>();
            FrontendRequest? capturedRequest = null;
            string? capturedMethodName = null;
            A.CallTo(() => apiService.MethodNotAllowed(A<FrontendRequest>._, A<string>._))
                .Invokes(
                    (FrontendRequest request, string methodName) =>
                    {
                        capturedRequest = request;
                        capturedMethodName = methodName;
                    }
                )
                .Returns(Task.FromResult(FakeCoreMethodNotAllowedResponse()));

            await using var factory = CreateFactory(apiService);
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Patch, requestUrl);

            await client.SendAsync(request);

            A.CallTo(() => apiService.MethodNotAllowed(A<FrontendRequest>._, A<string>._))
                .MustHaveHappenedOnceExactly();
            capturedRequest.Should().NotBeNull();
            capturedRequest!.Path.Should().Be(expectedDmsPath);
            capturedMethodName.Should().Be("PATCH");
        }

        [Test]
        public async Task It_returns_the_core_response_allow_header_and_body_unmodified()
        {
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.MethodNotAllowed(A<FrontendRequest>._, A<string>._))
                .Returns(Task.FromResult(FakeCoreMethodNotAllowedResponse()));

            await using var factory = CreateFactory(apiService);
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Patch, "/data/ed-fi/schools");

            var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            // ToResult is what carries Core's headers and content type to the wire; Core, not the
            // frontend, is the single authority for the Allow value.
            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            // Allow is a well-known header, so HttpClient parses the single "GET, POST" line Core
            // set into separate tokens; rejoining them recovers the value that went over the wire.
            string.Join(", ", response.Content.Headers.GetValues("Allow")).Should().Be("GET, POST");
            response.Content.Headers.ContentType!.ToString().Should().Be("application/json; charset=utf-8");
            JsonNode.Parse(content)!["source"]!.GetValue<string>().Should().Be("core");
        }
    }

    /// <summary>
    /// The regression guard for the terminal added to CoreEndpointModule: adding a method-less
    /// endpoint on the verb endpoints' own route template must not divert any supported verb.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Given_A_Data_Route_Request_With_A_Supported_Method
    {
        [TestCase("GET", "/data/ed-fi/schools")]
        [TestCase("POST", "/data/ed-fi/schools")]
        [TestCase("PUT", "/data/ed-fi/schools")]
        [TestCase("DELETE", "/data/ed-fi/schools")]
        [TestCase("GET", $"/data/ed-fi/schools/{ItemUuid}")]
        // POST on an item route must still reach Upsert so ValidateRouteSemanticsMiddleware can
        // emit its own distinct 405 message. If the terminal ever intercepted it the status would
        // still be 405 and only that message would change, which no other test here would catch.
        [TestCase("POST", $"/data/ed-fi/schools/{ItemUuid}")]
        [TestCase("PUT", $"/data/ed-fi/schools/{ItemUuid}")]
        [TestCase("DELETE", $"/data/ed-fi/schools/{ItemUuid}")]
        public async Task It_still_reaches_its_original_api_service_method(string verb, string requestUrl)
        {
            var apiService = FakeApiServiceAnsweringEveryVerb();

            await using var factory = CreateFactory(apiService);
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(new HttpMethod(verb), requestUrl);

            var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            AssertVerbReachedItsHandler(apiService, verb);
            A.CallTo(() => apiService.MethodNotAllowed(A<FrontendRequest>._, A<string>._))
                .MustNotHaveHappened();
        }
    }

    /// <summary>
    /// The regression guard for the terminals added to TrackedChangesEndpointModule, and the
    /// highest-value test in this set. Those terminals sit on literal templates that outrank the
    /// data catch-all on precedence, so at Order 0 they intercept POST, PUT and DELETE on these
    /// routes - verbs that today fall through to the catch-all verb endpoints and into Core.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Given_A_Tracked_Change_Route_Request_With_A_Supported_Method
    {
        [TestCase("deletes", "GET")]
        [TestCase("deletes", "POST")]
        [TestCase("deletes", "PUT")]
        [TestCase("deletes", "DELETE")]
        [TestCase("keyChanges", "GET")]
        [TestCase("keyChanges", "POST")]
        [TestCase("keyChanges", "PUT")]
        [TestCase("keyChanges", "DELETE")]
        public async Task It_still_reaches_its_original_api_service_method(
            string trackedChangeSegment,
            string verb
        )
        {
            var apiService = FakeApiServiceAnsweringEveryVerb();

            await using var factory = CreateFactory(apiService);
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(
                new HttpMethod(verb),
                $"/data/ed-fi/schools/{trackedChangeSegment}"
            );

            var response = await client.SendAsync(request);

            // A 405 here means a tracked terminal answered a verb it must not own.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            if (verb == "GET")
            {
                A.CallTo(() => apiService.GetTrackedChanges(A<FrontendRequest>._))
                    .MustHaveHappenedOnceExactly();
            }
            else
            {
                AssertVerbReachedItsHandler(apiService, verb);
                A.CallTo(() => apiService.GetTrackedChanges(A<FrontendRequest>._)).MustNotHaveHappened();
            }

            A.CallTo(() => apiService.MethodNotAllowedForTrackedChange(A<FrontendRequest>._, A<string>._))
                .MustNotHaveHappened();
        }
    }

    /// <summary>
    /// Like the data-route terminal, these hand the request to Core rather than answering locally,
    /// so authentication, tenant validation and resource existence precede the 405. That makes this
    /// a routing test: the response body comes from the fake, and the Core terminal's own unit test
    /// owns the Ed-Fi problem-details contract.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Given_A_Tracked_Change_Route_Request_With_An_Unsupported_Method
    {
        /// <summary>
        /// Stands in for what JwtAuthenticationMiddleware produces at the head of the Core
        /// pipeline, so a routing test can prove the frontend surfaces it instead of answering
        /// 405 itself.
        /// </summary>
        private static IFrontendResponse FakeCoreUnauthorizedResponse()
        {
            var response = A.Fake<IFrontendResponse>();
            A.CallTo(() => response.StatusCode).Returns(401);
            A.CallTo(() => response.Body).Returns(new JsonObject { ["source"] = "core" });
            A.CallTo(() => response.Headers).Returns(new Dictionary<string, string>());
            A.CallTo(() => response.ContentType).Returns("application/json");
            return response;
        }

        [TestCase("deletes")]
        [TestCase("keyChanges")]
        public async Task It_hands_the_request_to_core_with_the_operation_suffix_and_method_name(
            string trackedChangeSegment
        )
        {
            var apiService = A.Fake<IApiService>();
            FrontendRequest? capturedRequest = null;
            string? capturedMethodName = null;
            A.CallTo(() => apiService.MethodNotAllowedForTrackedChange(A<FrontendRequest>._, A<string>._))
                .Invokes(
                    (FrontendRequest request, string methodName) =>
                    {
                        capturedRequest = request;
                        capturedMethodName = methodName;
                    }
                )
                .Returns(Task.FromResult(FakeCoreMethodNotAllowedResponse(allow: "GET")));

            await using var factory = CreateFactory(apiService);
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Patch,
                $"/data/ed-fi/schools/{trackedChangeSegment}"
            );

            await client.SendAsync(request);

            A.CallTo(() => apiService.MethodNotAllowedForTrackedChange(A<FrontendRequest>._, A<string>._))
                .MustHaveHappenedOnceExactly();
            capturedRequest.Should().NotBeNull();
            // The suffix has to survive into the path, because ParseTrackedChangePathMiddleware
            // parses the operation back out of it.
            capturedRequest!.Path.Should().Be($"/ed-fi/schools/{trackedChangeSegment}");
            capturedMethodName.Should().Be("PATCH");
        }

        [Test]
        public async Task It_returns_the_core_response_allow_header_and_body_unmodified()
        {
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.MethodNotAllowedForTrackedChange(A<FrontendRequest>._, A<string>._))
                .Returns(Task.FromResult(FakeCoreMethodNotAllowedResponse(allow: "GET")));

            await using var factory = CreateFactory(apiService);
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Patch, "/data/ed-fi/schools/deletes");

            var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            // Core, not the frontend, is the single authority for the Allow value here too.
            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            response.Content.Headers.GetValues("Allow").Should().Equal("GET");
            response.Content.Headers.ContentType!.ToString().Should().Be("application/json; charset=utf-8");
            JsonNode.Parse(content)!["source"]!.GetValue<string>().Should().Be("core");
        }

        /// <summary>
        /// The regression guard for the defect this routing replaced: answering in the frontend
        /// skipped JwtAuthenticationMiddleware, so an unauthenticated caller received a 405 where
        /// every other data path requires a token. Nothing but reaching Core can restore that.
        /// </summary>
        [TestCase("deletes")]
        [TestCase("keyChanges")]
        public async Task It_lets_core_answer_rather_than_responding_before_authentication(
            string trackedChangeSegment
        )
        {
            var apiService = A.Fake<IApiService>();
            A.CallTo(() => apiService.MethodNotAllowedForTrackedChange(A<FrontendRequest>._, A<string>._))
                .Returns(Task.FromResult(FakeCoreUnauthorizedResponse()));

            await using var factory = CreateFactory(apiService);
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Patch,
                $"/data/ed-fi/schools/{trackedChangeSegment}"
            );

            var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            response.Content.Headers.Contains("Allow").Should().BeFalse();
        }

        /// <summary>
        /// Under a tenant and route-qualifier prefix both terminals sit at Order 1, so nothing but
        /// route precedence keeps the literal tracked-change template ahead of the data catch-all.
        /// If that ever inverts the status stays 405 and only the Allow value degrades from "GET" to
        /// the collection methods, which no other assertion here would notice. The prefix segments
        /// are asserted too because the Core pipeline validates the tenant and resolves the data
        /// store before reaching the terminal.
        /// </summary>
        [TestCase("deletes")]
        [TestCase("keyChanges")]
        public async Task It_reaches_the_tracked_change_terminal_under_a_tenant_and_qualifier_prefix(
            string trackedChangeSegment
        )
        {
            var apiService = FakeApiServiceAnsweringEveryVerb();
            FrontendRequest? capturedRequest = null;
            A.CallTo(() => apiService.MethodNotAllowedForTrackedChange(A<FrontendRequest>._, A<string>._))
                .Invokes((FrontendRequest request, string _) => capturedRequest = request)
                .Returns(Task.FromResult(FakeCoreMethodNotAllowedResponse(allow: "GET")));

            await using var factory = CreateFactory(
                apiService,
                new Dictionary<string, string?>
                {
                    ["AppSettings:MultiTenancy"] = "true",
                    ["AppSettings:RouteQualifierSegments"] = "districtId,schoolYear",
                }
            );
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Patch,
                $"/tenant1/255902/2026/data/ed-fi/schools/{trackedChangeSegment}"
            );

            var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            response.Content.Headers.GetValues("Allow").Should().Equal("GET");
            // The data-route terminal shares Order 1 and would answer with the collection methods.
            A.CallTo(() => apiService.MethodNotAllowed(A<FrontendRequest>._, A<string>._))
                .MustNotHaveHappened();
            capturedRequest.Should().NotBeNull();
            capturedRequest!.Path.Should().Be($"/ed-fi/schools/{trackedChangeSegment}");
            capturedRequest.Tenant.Should().Be("tenant1");
            capturedRequest
                .RouteQualifiers[new RouteQualifierName("districtId")]
                .Should()
                .Be(new RouteQualifierValue("255902"));
            capturedRequest
                .RouteQualifiers[new RouteQualifierName("schoolYear")]
                .Should()
                .Be(new RouteQualifierValue("2026"));
        }
    }

    [TestFixture]
    [NonParallelizable]
    public class Given_Other_Unmapped_Requests
    {
        /// <summary>
        /// HEAD falls through to the method-not-allowed terminal rather than being answered by the
        /// GET handler. RFC 9110 section 9.1 would have a general-purpose server support HEAD
        /// wherever GET is supported, but ODS/API declares no HttpHead action and pins the
        /// resulting 405 with an integration test, and this endpoint exists for ODS/API
        /// compatibility. Mapping HEAD onto GET here would answer 200 where ODS/API answers 405.
        /// </summary>
        [TestCase("/data/ed-fi/schools", TestName = "Collection route")]
        [TestCase($"/data/ed-fi/schools/{ItemUuid}", TestName = "Item route")]
        [TestCase("/data/ed-fi/schools/deletes", TestName = "Tracked change route")]
        public async Task It_answers_head_from_the_method_not_allowed_terminal(string requestUrl)
        {
            var apiService = FakeApiServiceAnsweringEveryVerb();
            List<string> terminalMethodNames = [];
            A.CallTo(() => apiService.MethodNotAllowed(A<FrontendRequest>._, A<string>._))
                .Invokes((FrontendRequest _, string methodName) => terminalMethodNames.Add(methodName))
                .Returns(Task.FromResult(FakeCoreMethodNotAllowedResponse()));
            A.CallTo(() => apiService.MethodNotAllowedForTrackedChange(A<FrontendRequest>._, A<string>._))
                .Invokes((FrontendRequest _, string methodName) => terminalMethodNames.Add(methodName))
                .Returns(Task.FromResult(FakeCoreMethodNotAllowedResponse(allow: "GET")));

            await using var factory = CreateFactory(apiService);
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Head, requestUrl);

            var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            // Core names the verb in the 405 body, the same way ODS/API does from
            // context.Request.Method, so the real verb has to survive the hand-off.
            terminalMethodNames.Should().Equal("HEAD");
            A.CallTo(() => apiService.Get(A<FrontendRequest>._, A<CancellationToken>._))
                .MustNotHaveHappened();
            A.CallTo(() => apiService.GetTrackedChanges(A<FrontendRequest>._)).MustNotHaveHappened();
        }

        [Test]
        public async Task It_still_answers_a_non_data_path_from_the_fallback()
        {
            var apiService = FakeApiServiceAnsweringEveryVerb();

            await using var factory = CreateFactory(apiService);
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/nope");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            A.CallTo(() => apiService.MethodNotAllowed(A<FrontendRequest>._, A<string>._))
                .MustNotHaveHappened();
        }

        [Test]
        public async Task It_still_answers_a_cors_preflight_before_reaching_the_terminal()
        {
            var apiService = FakeApiServiceAnsweringEveryVerb();

            await using var factory = CreateFactory(apiService);
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Options, "/data/ed-fi/schools");
            request.Headers.Add("Origin", "http://localhost:8082");
            request.Headers.Add("Access-Control-Request-Method", "GET");

            var response = await client.SendAsync(request);

            // A plain OPTIONS now reaches the terminal and answers 405, but the CORS middleware
            // short-circuits a preflight ahead of endpoint execution.
            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            A.CallTo(() => apiService.MethodNotAllowed(A<FrontendRequest>._, A<string>._))
                .MustNotHaveHappened();
        }
    }

    private static void AssertVerbReachedItsHandler(IApiService apiService, string verb)
    {
        switch (verb)
        {
            case "GET":
                A.CallTo(() => apiService.Get(A<FrontendRequest>._, A<CancellationToken>._))
                    .MustHaveHappenedOnceExactly();
                break;
            case "POST":
                A.CallTo(() => apiService.Upsert(A<FrontendRequest>._, A<CancellationToken>._))
                    .MustHaveHappenedOnceExactly();
                break;
            case "PUT":
                A.CallTo(() => apiService.UpdateById(A<FrontendRequest>._, A<CancellationToken>._))
                    .MustHaveHappenedOnceExactly();
                break;
            case "DELETE":
                A.CallTo(() => apiService.DeleteById(A<FrontendRequest>._)).MustHaveHappenedOnceExactly();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(verb), verb, "Unhandled verb");
        }
    }
}
