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
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
public class ProfileResolutionMiddlewareTests
{
    private static ProfileResolutionMiddleware CreateMiddleware(
        IProfileService? profileService = null,
        IApplicationContextProvider? appContextProvider = null
    )
    {
        return new ProfileResolutionMiddleware(
            profileService ?? A.Fake<IProfileService>(),
            appContextProvider ?? A.Fake<IApplicationContextProvider>(),
            NullLogger<ProfileResolutionMiddleware>.Instance
        );
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
        string resourceName = "Student"
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

        var requestInfo = new RequestInfo(frontendRequest, method)
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
    }

    [TestFixture]
    public class Given_No_Application_Context_And_No_Profile_Header : ProfileResolutionMiddlewareTests
    {
        private RequestInfo _requestInfo = null!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(RequestMethod.GET);
            _nextCalled = false;

            var appContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => appContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(Task.FromResult<ApplicationContext?>(null));

            var middleware = CreateMiddleware(appContextProvider: appContextProvider);

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

            _requestInfo = CreateRequestInfo(RequestMethod.GET, headers);
            _nextCalled = false;

            var appContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => appContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(Task.FromResult<ApplicationContext?>(null));

            var middleware = CreateMiddleware(appContextProvider: appContextProvider);

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

            _requestInfo = CreateRequestInfo(RequestMethod.POST, headers);

            var appContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => appContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(Task.FromResult<ApplicationContext?>(null));

            var middleware = CreateMiddleware(appContextProvider: appContextProvider);

            await middleware.Execute(_requestInfo, () => Task.CompletedTask);
        }

        [Test]
        public void It_returns_415_for_POST()
        {
            _requestInfo.FrontendResponse!.StatusCode.Should().Be(415);
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

            _requestInfo = CreateRequestInfo(RequestMethod.PUT, headers);

            var appContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => appContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(Task.FromResult<ApplicationContext?>(null));

            var middleware = CreateMiddleware(appContextProvider: appContextProvider);

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

            _requestInfo = CreateRequestInfo(RequestMethod.GET, headers);
            _nextCalled = false;

            var appContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => appContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(CreateApplicationContext());

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

            var middleware = CreateMiddleware(profileService, appContextProvider);

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

            _requestInfo = CreateRequestInfo(RequestMethod.GET, headers);
            _nextCalled = false;

            var appContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => appContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(CreateApplicationContext());

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

            var middleware = CreateMiddleware(profileService, appContextProvider);

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
            _requestInfo = CreateRequestInfo(RequestMethod.GET);
            _nextCalled = false;

            var appContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => appContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(CreateApplicationContext());

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

            var middleware = CreateMiddleware(profileService, appContextProvider);

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

            _requestInfo = CreateRequestInfo(RequestMethod.PUT, headers);
            _nextCalled = false;

            var appContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => appContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(CreateApplicationContext());

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

            var middleware = CreateMiddleware(profileService, appContextProvider);

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
            _requestInfo = CreateRequestInfo(RequestMethod.DELETE);
            _nextCalled = false;

            var appContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => appContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(CreateApplicationContext());

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

            var middleware = CreateMiddleware(_profileService, appContextProvider);

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

            _requestInfo = CreateRequestInfo(RequestMethod.GET, headers);
            _nextCalled = false;

            var appContextProvider = A.Fake<IApplicationContextProvider>();
            A.CallTo(() => appContextProvider.GetApplicationByClientIdAsync(A<string>._))
                .Returns(CreateApplicationContext());

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

            var middleware = CreateMiddleware(_profileService, appContextProvider);

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
}
