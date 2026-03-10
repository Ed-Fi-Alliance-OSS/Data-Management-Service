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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
public class ProfileResolutionMiddlewareTests
{
    private static void AssertLegacyProblemDetailsResponse(
        IFrontendResponse response,
        int expectedStatusCode,
        string expectedType,
        string expectedTitle,
        string expectedDetail,
        string expectedCorrelationId,
        params string[] expectedErrors
    )
    {
        response.StatusCode.Should().Be(expectedStatusCode);
        response.ContentType.Should().Be("application/problem+json");

        JsonObject body = response.Body!.AsObject();

        body.Select(property => property.Key)
            .Should()
            .BeEquivalentTo("detail", "type", "title", "status", "correlationId", "errors");

        body["detail"]?.GetValue<string>().Should().Be(expectedDetail);
        body["type"]?.GetValue<string>().Should().Be(expectedType);
        body["title"]?.GetValue<string>().Should().Be(expectedTitle);
        body["status"]?.GetValue<int>().Should().Be(expectedStatusCode);
        body["correlationId"]?.GetValue<string>().Should().Be(expectedCorrelationId);
        body["validationErrors"].Should().BeNull();
        body["errors"]!.AsArray().Select(error => error!.GetValue<string>()).Should().Equal(expectedErrors);
    }

    private static ProfileResolutionMiddleware CreateMiddleware(IProfileService? profileService = null)
    {
        return new ProfileResolutionMiddleware(
            profileService ?? A.Fake<IProfileService>(),
            NullLogger<ProfileResolutionMiddleware>.Instance
        );
    }

    private static IServiceProvider BuildScopedServiceProvider(
        IApplicationContextProvider? appContextProvider = null
    )
    {
        var applicationContextProvider = appContextProvider ?? A.Fake<IApplicationContextProvider>();

        if (appContextProvider is null)
        {
            A.CallTo(() => applicationContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(Task.FromResult<ApplicationContext?>(null));
        }

        return new ServiceCollection()
            .AddScoped(_ => applicationContextProvider)
            .BuildServiceProvider()
            .CreateScope()
            .ServiceProvider;
    }

    private static ApplicationContext CreateApplicationContext(long applicationId = 1) =>
        new(
            Id: 1,
            ApplicationId: applicationId,
            ClientId: "client123",
            ClientUuid: Guid.NewGuid(),
            DmsInstanceIds: []
        );

    private static RequestInfo CreateRequestInfo(
        RequestMethod method,
        Dictionary<string, string>? headers = null,
        string resourceName = "Student",
        IServiceProvider? scopedServiceProvider = null
    )
    {
        var frontendRequest = new FrontendRequest(
            Path: $"/ed-fi/{resourceName.ToLowerInvariant()}s",
            Body: "{}",
            Form: null,
            Headers: headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            QueryParameters: [],
            TraceId: new TraceId("test-trace-id"),
            RouteQualifiers: []
        );

        var requestInfo = new RequestInfo(
            frontendRequest,
            method,
            scopedServiceProvider ?? BuildScopedServiceProvider()
        )
        {
            ResourceSchema = new Core.ApiSchema.ResourceSchema(
                new JsonObject { ["resourceName"] = resourceName }
            ),
            ClientAuthorizations = new ClientAuthorizations(
                TokenId: "token123",
                ClientId: "client123",
                ClaimSetName: "TestClaimSet",
                EducationOrganizationIds: [],
                NamespacePrefixes: [],
                DmsInstanceIds: []
            ),
        };

        return requestInfo;
    }

    [TestFixture]
    public class Given_Invalid_Profile_Header : ProfileResolutionMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept"] = "application/vnd.ed-fi.student.invalid+json", // Missing usage type
            };

            _requestInfo = CreateRequestInfo(RequestMethod.GET, headers);
            _nextCalled = false;

            var middleware = CreateMiddleware();

            await middleware.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_does_not_call_next()
        {
            _nextCalled.Should().BeFalse();
        }

        [Test]
        public void It_returns_400_bad_request()
        {
            _requestInfo.FrontendResponse.Should().NotBeNull();
            _requestInfo.FrontendResponse!.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_problem_json()
        {
            _requestInfo.FrontendResponse!.ContentType.Should().Be("application/problem+json");
        }

        [Test]
        public void It_returns_the_legacy_problem_details_payload()
        {
            AssertLegacyProblemDetailsResponse(
                _requestInfo.FrontendResponse!,
                expectedStatusCode: 400,
                expectedType: "urn:ed-fi:api:profile:invalid-profile-usage",
                expectedTitle: "Invalid Profile Usage",
                expectedDetail: "The request construction was invalid with respect to usage of a data policy.",
                expectedCorrelationId: "test-trace-id",
                "The format of the profile-based content type header was invalid."
            );
        }
    }

    [TestFixture]
    public class Given_No_Application_Context_And_No_Profile_Header : ProfileResolutionMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var appContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => appContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(Task.FromResult<ApplicationContext?>(null));

            _requestInfo = CreateRequestInfo(
                RequestMethod.GET,
                scopedServiceProvider: BuildScopedServiceProvider(appContextProvider)
            );
            _nextCalled = false;

            var middleware = CreateMiddleware();

            await middleware.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_No_Application_Context_But_Profile_Header_Specified : ProfileResolutionMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept"] = "application/vnd.ed-fi.student.testprofile.readable+json",
            };

            var appContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => appContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(Task.FromResult<ApplicationContext?>(null));

            _requestInfo = CreateRequestInfo(
                RequestMethod.GET,
                headers,
                scopedServiceProvider: BuildScopedServiceProvider(appContextProvider)
            );
            _nextCalled = false;

            var middleware = CreateMiddleware();

            await middleware.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_does_not_call_next()
        {
            _nextCalled.Should().BeFalse();
        }

        [Test]
        public void It_returns_406_for_GET()
        {
            _requestInfo.FrontendResponse!.StatusCode.Should().Be(406);
        }

        [Test]
        public void It_returns_the_legacy_problem_details_payload()
        {
            AssertLegacyProblemDetailsResponse(
                _requestInfo.FrontendResponse!,
                expectedStatusCode: 406,
                expectedType: "urn:ed-fi:api:profile:invalid-profile-usage",
                expectedTitle: "Invalid Profile Usage",
                expectedDetail: "The request construction was invalid with respect to usage of a data policy.",
                expectedCorrelationId: "test-trace-id",
                "Unable to resolve application context for profile validation."
            );
        }
    }

    [TestFixture]
    public class Given_No_Application_Context_But_Profile_Header_For_POST : ProfileResolutionMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/vnd.ed-fi.student.testprofile.writable+json",
            };

            var appContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => appContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(Task.FromResult<ApplicationContext?>(null));

            _requestInfo = CreateRequestInfo(
                RequestMethod.POST,
                headers,
                scopedServiceProvider: BuildScopedServiceProvider(appContextProvider)
            );

            var middleware = CreateMiddleware();

            await middleware.Execute(_requestInfo, () => Task.CompletedTask);
        }

        [Test]
        public void It_returns_415_for_POST()
        {
            _requestInfo.FrontendResponse!.StatusCode.Should().Be(415);
        }

        [Test]
        public void It_returns_the_legacy_problem_details_payload()
        {
            AssertLegacyProblemDetailsResponse(
                _requestInfo.FrontendResponse!,
                expectedStatusCode: 415,
                expectedType: "urn:ed-fi:api:profile:invalid-profile-usage",
                expectedTitle: "Invalid Profile Usage",
                expectedDetail: "The request construction was invalid with respect to usage of a data policy.",
                expectedCorrelationId: "test-trace-id",
                "Unable to resolve application context for profile validation."
            );
        }
    }

    [TestFixture]
    public class Given_No_Application_Context_But_Profile_Header_For_PUT : ProfileResolutionMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/vnd.ed-fi.student.testprofile.writable+json",
            };

            var appContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => appContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(Task.FromResult<ApplicationContext?>(null));

            _requestInfo = CreateRequestInfo(
                RequestMethod.PUT,
                headers,
                scopedServiceProvider: BuildScopedServiceProvider(appContextProvider)
            );

            var middleware = CreateMiddleware();

            await middleware.Execute(_requestInfo, () => Task.CompletedTask);
        }

        [Test]
        public void It_returns_415_for_PUT()
        {
            _requestInfo.FrontendResponse!.StatusCode.Should().Be(415);
        }
    }

    [TestFixture]
    public class Given_Profile_Resolution_Failure : ProfileResolutionMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept"] = "application/vnd.ed-fi.student.testprofile.readable+json",
            };

            var appContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => appContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(CreateApplicationContext());

            _requestInfo = CreateRequestInfo(
                RequestMethod.GET,
                headers,
                scopedServiceProvider: BuildScopedServiceProvider(appContextProvider)
            );
            _nextCalled = false;

            var profileService = A.Fake<IProfileService>();
            A.CallTo(() =>
                    profileService.ResolveProfileAsync(
                        A<ParsedProfileHeader?>._,
                        A<RequestMethod>._,
                        A<string>._,
                        A<long>._,
                        A<string?>._
                    )
                )
                .Returns(
                    ProfileResolutionResult.Failure(
                        new ProfileResolutionError(
                            StatusCode: 403,
                            ErrorType: "urn:ed-fi:api:security:data-policy:incorrect-usage",
                            Title: "Data Policy Failure",
                            Detail: "Profile not assigned to application",
                            Errors: ["Profile 'testprofile' is not assigned to this application."]
                        )
                    )
                );

            var middleware = CreateMiddleware(profileService);

            await middleware.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_does_not_call_next()
        {
            _nextCalled.Should().BeFalse();
        }

        [Test]
        public void It_returns_error_status_code()
        {
            _requestInfo.FrontendResponse!.StatusCode.Should().Be(403);
        }

        [Test]
        public void It_returns_problem_json()
        {
            _requestInfo.FrontendResponse!.ContentType.Should().Be("application/problem+json");
        }

        [Test]
        public void It_returns_the_legacy_problem_details_payload()
        {
            AssertLegacyProblemDetailsResponse(
                _requestInfo.FrontendResponse!,
                expectedStatusCode: 403,
                expectedType: "urn:ed-fi:api:security:data-policy:incorrect-usage",
                expectedTitle: "Data Policy Failure",
                expectedDetail: "Profile not assigned to application",
                expectedCorrelationId: "test-trace-id",
                "Profile 'testprofile' is not assigned to this application."
            );
        }
    }

    [TestFixture]
    public class Given_Profile_Resolution_Success_With_Profile : ProfileResolutionMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept"] = "application/vnd.ed-fi.student.testprofile.readable+json",
            };

            var appContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => appContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(CreateApplicationContext());

            _requestInfo = CreateRequestInfo(
                RequestMethod.GET,
                headers,
                scopedServiceProvider: BuildScopedServiceProvider(appContextProvider)
            );
            _nextCalled = false;

            var resourceProfile = new ResourceProfile(
                "Student",
                null,
                new ContentTypeDefinition(MemberSelection.IncludeAll, [], [], [], []),
                null
            );

            var profileService = A.Fake<IProfileService>();
            A.CallTo(() =>
                    profileService.ResolveProfileAsync(
                        A<ParsedProfileHeader?>._,
                        A<RequestMethod>._,
                        A<string>._,
                        A<long>._,
                        A<string?>._
                    )
                )
                .Returns(
                    ProfileResolutionResult.Success(
                        new ProfileContext(
                            ProfileName: "TestProfile",
                            ContentType: ProfileContentType.Read,
                            ResourceProfile: resourceProfile,
                            WasExplicitlySpecified: true
                        )
                    )
                );

            var middleware = CreateMiddleware(profileService);

            await middleware.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_sets_profile_context()
        {
            _requestInfo.ProfileContext.Should().NotBeNull();
            _requestInfo.ProfileContext!.ProfileName.Should().Be("TestProfile");
        }
    }

    [TestFixture]
    public class Given_Profile_Resolution_Success_Without_Profile : ProfileResolutionMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var appContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => appContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(CreateApplicationContext());

            _requestInfo = CreateRequestInfo(
                RequestMethod.GET,
                scopedServiceProvider: BuildScopedServiceProvider(appContextProvider)
            );
            _nextCalled = false;

            var profileService = A.Fake<IProfileService>();
            A.CallTo(() =>
                    profileService.ResolveProfileAsync(
                        A<ParsedProfileHeader?>._,
                        A<RequestMethod>._,
                        A<string>._,
                        A<long>._,
                        A<string?>._
                    )
                )
                .Returns(ProfileResolutionResult.NoProfileApplies());

            var middleware = CreateMiddleware(profileService);

            await middleware.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_does_not_set_profile_context()
        {
            _requestInfo.ProfileContext.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_PUT_Request_With_Writable_Header : ProfileResolutionMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/vnd.ed-fi.student.testprofile.writable+json",
            };

            var appContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => appContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(CreateApplicationContext());

            _requestInfo = CreateRequestInfo(
                RequestMethod.PUT,
                headers,
                scopedServiceProvider: BuildScopedServiceProvider(appContextProvider)
            );
            _nextCalled = false;

            var resourceProfile = new ResourceProfile(
                "Student",
                null,
                null,
                new ContentTypeDefinition(MemberSelection.IncludeAll, [], [], [], [])
            );

            var profileService = A.Fake<IProfileService>();
            A.CallTo(() =>
                    profileService.ResolveProfileAsync(
                        A<ParsedProfileHeader?>._,
                        A<RequestMethod>._,
                        A<string>._,
                        A<long>._,
                        A<string?>._
                    )
                )
                .Returns(
                    ProfileResolutionResult.Success(
                        new ProfileContext(
                            ProfileName: "TestProfile",
                            ContentType: ProfileContentType.Write,
                            ResourceProfile: resourceProfile,
                            WasExplicitlySpecified: true
                        )
                    )
                );

            var middleware = CreateMiddleware(profileService);

            await middleware.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_sets_write_profile_context()
        {
            _requestInfo.ProfileContext!.ContentType.Should().Be(ProfileContentType.Write);
        }
    }

    [TestFixture]
    public class Given_DELETE_Request : ProfileResolutionMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;
        private IProfileService _profileService = null!;

        [SetUp]
        public async Task Setup()
        {
            var appContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => appContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(CreateApplicationContext());

            _requestInfo = CreateRequestInfo(
                RequestMethod.DELETE,
                scopedServiceProvider: BuildScopedServiceProvider(appContextProvider)
            );
            _nextCalled = false;

            _profileService = A.Fake<IProfileService>();
            A.CallTo(() =>
                    _profileService.ResolveProfileAsync(
                        A<ParsedProfileHeader?>._,
                        A<RequestMethod>._,
                        A<string>._,
                        A<long>._,
                        A<string?>._
                    )
                )
                .Returns(ProfileResolutionResult.NoProfileApplies());

            var middleware = CreateMiddleware(_profileService);

            await middleware.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_calls_profile_service_with_null_header()
        {
            A.CallTo(() =>
                    _profileService.ResolveProfileAsync(
                        null,
                        RequestMethod.DELETE,
                        A<string>._,
                        A<long>._,
                        A<string?>._
                    )
                )
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_Standard_Json_Accept_Header : ProfileResolutionMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;
        private IProfileService _profileService = null!;

        [SetUp]
        public async Task Setup()
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept"] = "application/json",
            };

            var appContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => appContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(CreateApplicationContext());

            _requestInfo = CreateRequestInfo(
                RequestMethod.GET,
                headers,
                scopedServiceProvider: BuildScopedServiceProvider(appContextProvider)
            );
            _nextCalled = false;

            _profileService = A.Fake<IProfileService>();
            A.CallTo(() =>
                    _profileService.ResolveProfileAsync(
                        A<ParsedProfileHeader?>._,
                        A<RequestMethod>._,
                        A<string>._,
                        A<long>._,
                        A<string?>._
                    )
                )
                .Returns(ProfileResolutionResult.NoProfileApplies());

            var middleware = CreateMiddleware(_profileService);

            await middleware.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_calls_profile_service_with_null_parsed_header()
        {
            A.CallTo(() =>
                    _profileService.ResolveProfileAsync(
                        null,
                        RequestMethod.GET,
                        A<string>._,
                        A<long>._,
                        A<string?>._
                    )
                )
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_Middleware_Is_Reused_Across_Requests : ProfileResolutionMiddlewareTests
    {
        private IApplicationContextProvider _firstApplicationContextProvider = null!;
        private IApplicationContextProvider _secondApplicationContextProvider = null!;
        private IProfileService _profileService = null!;

        [SetUp]
        public async Task Setup()
        {
            _firstApplicationContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => _firstApplicationContextProvider.GetApplicationByClientIdAsync("client123"))
                .Returns(CreateApplicationContext(11));

            _secondApplicationContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => _secondApplicationContextProvider.GetApplicationByClientIdAsync("client123"))
                .Returns(CreateApplicationContext(22));

            _profileService = A.Fake<IProfileService>();
            A.CallTo(() =>
                    _profileService.ResolveProfileAsync(
                        A<ParsedProfileHeader?>._,
                        A<RequestMethod>._,
                        A<string>._,
                        A<long>._,
                        A<string?>._
                    )
                )
                .Returns(ProfileResolutionResult.NoProfileApplies());

            var middleware = CreateMiddleware(_profileService);

            await middleware.Execute(
                CreateRequestInfo(
                    RequestMethod.GET,
                    scopedServiceProvider: BuildScopedServiceProvider(_firstApplicationContextProvider)
                ),
                () => Task.CompletedTask
            );

            await middleware.Execute(
                CreateRequestInfo(
                    RequestMethod.GET,
                    scopedServiceProvider: BuildScopedServiceProvider(_secondApplicationContextProvider)
                ),
                () => Task.CompletedTask
            );
        }

        [Test]
        public void It_uses_the_first_request_scope_for_the_first_call()
        {
            A.CallTo(() =>
                    _profileService.ResolveProfileAsync(null, RequestMethod.GET, "Student", 11, A<string?>._)
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_uses_the_second_request_scope_for_the_second_call()
        {
            A.CallTo(() =>
                    _profileService.ResolveProfileAsync(null, RequestMethod.GET, "Student", 22, A<string?>._)
                )
                .MustHaveHappenedOnceExactly();
        }
    }
}
