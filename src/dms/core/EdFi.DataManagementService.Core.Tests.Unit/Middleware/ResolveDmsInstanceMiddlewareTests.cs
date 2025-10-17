// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ResolveDmsInstanceMiddlewareTests
{
    internal static (
        ResolveDmsInstanceMiddleware middleware,
        IDmsInstanceProvider dmsInstanceProvider,
        IDmsInstanceSelection dmsInstanceSelection
    ) CreateMiddleware()
    {
        var dmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
        var dmsInstanceSelection = A.Fake<IDmsInstanceSelection>();
        var logger = A.Fake<ILogger<ResolveDmsInstanceMiddleware>>();

        // Create a service provider that resolves IDmsInstanceSelection
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IDmsInstanceSelection)))
            .Returns(dmsInstanceSelection);

        var middleware = new ResolveDmsInstanceMiddleware(dmsInstanceProvider, serviceProvider, logger);

        return (middleware, dmsInstanceProvider, dmsInstanceSelection);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Client_With_No_Authorized_Instances : ResolveDmsInstanceMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;

        [SetUp]
        public async Task Setup()
        {
            var frontendRequest = new FrontendRequest(
                Path: "/ed-fi/students",
                Body: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("123"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET)
            {
                ClientAuthorizations = new ClientAuthorizations(
                    TokenId: "token123",
                    ClientId: "client123",
                    ClaimSetName: "test",
                    EducationOrganizationIds: [],
                    NamespacePrefixes: [],
                    DmsInstanceIds: [] // No authorized instances
                ),
            };

            var (middleware, _, _) = CreateMiddleware();

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
        public void It_does_not_call_the_next_middleware()
        {
            _nextCalled.Should().BeFalse();
        }

        [Test]
        public void It_returns_403_forbidden()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(403);
        }

        [Test]
        public void It_includes_error_detail_in_response_body()
        {
            _requestInfo.FrontendResponse.Body?.ToString().Should().Contain("No database instances");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Matching_Instance_With_No_Route_Qualifiers : ResolveDmsInstanceMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;
        private IDmsInstanceSelection _dmsInstanceSelection = null!;
        private DmsInstance _expectedInstance = null!;

        [SetUp]
        public async Task Setup()
        {
            var frontendRequest = new FrontendRequest(
                Path: "/ed-fi/students",
                Body: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("123"),
                RouteQualifiers: [] // No route qualifiers in request
            );

            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET)
            {
                ClientAuthorizations = new ClientAuthorizations(
                    TokenId: "token123",
                    ClientId: "client123",
                    ClaimSetName: "test",
                    EducationOrganizationIds: [],
                    NamespacePrefixes: [],
                    DmsInstanceIds: [new DmsInstanceId(1)]
                ),
            };

            var (middleware, dmsInstanceProvider, dmsInstanceSelection) = CreateMiddleware();
            _dmsInstanceSelection = dmsInstanceSelection;

            // Setup instance with no route context
            _expectedInstance = new DmsInstance(
                Id: 1,
                InstanceType: "Test",
                InstanceName: "Test Instance",
                ConnectionString: "test-connection",
                RouteContext: [] // Empty route context matches empty qualifiers
            );

            A.CallTo(() => dmsInstanceProvider.GetById(1)).Returns(_expectedInstance);

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
        public void It_calls_the_next_middleware()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_calls_SetSelectedDmsInstance_on_provider()
        {
            A.CallTo(() => _dmsInstanceSelection.SetSelectedDmsInstance(_expectedInstance))
                .MustHaveHappened();
        }

        [Test]
        public void It_does_not_set_a_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Matching_Instance_With_Route_Qualifiers : ResolveDmsInstanceMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;

        [SetUp]
        public async Task Setup()
        {
            var routeQualifiers = new Dictionary<RouteQualifierName, RouteQualifierValue>
            {
                [new RouteQualifierName("district")] = new RouteQualifierValue("255901"),
                [new RouteQualifierName("schoolYear")] = new RouteQualifierValue("2024"),
            };

            var frontendRequest = new FrontendRequest(
                Path: "/ed-fi/students",
                Body: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("123"),
                RouteQualifiers: routeQualifiers
            );

            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET)
            {
                ClientAuthorizations = new ClientAuthorizations(
                    TokenId: "token123",
                    ClientId: "client123",
                    ClaimSetName: "test",
                    EducationOrganizationIds: [],
                    NamespacePrefixes: [],
                    DmsInstanceIds: [new DmsInstanceId(1), new DmsInstanceId(2)]
                ),
            };

            var (middleware, dmsInstanceProvider, _) = CreateMiddleware();

            // First instance doesn't match
            A.CallTo(() => dmsInstanceProvider.GetById(1))
                .Returns(
                    new DmsInstance(
                        Id: 1,
                        InstanceType: "Test",
                        InstanceName: "Wrong Instance",
                        ConnectionString: "wrong-connection",
                        RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>
                        {
                            [new RouteQualifierName("district")] = new RouteQualifierValue("999"),
                            [new RouteQualifierName("schoolYear")] = new RouteQualifierValue("2024"),
                        }
                    )
                );

            // Second instance matches
            A.CallTo(() => dmsInstanceProvider.GetById(2))
                .Returns(
                    new DmsInstance(
                        Id: 2,
                        InstanceType: "Test",
                        InstanceName: "Correct Instance",
                        ConnectionString: "correct-connection",
                        RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>
                        {
                            [new RouteQualifierName("district")] = new RouteQualifierValue("255901"),
                            [new RouteQualifierName("schoolYear")] = new RouteQualifierValue("2024"),
                        }
                    )
                );

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
        public void It_calls_the_next_middleware()
        {
            _nextCalled.Should().BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Multiple_Matching_Instances : ResolveDmsInstanceMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;

        [SetUp]
        public async Task Setup()
        {
            var routeQualifiers = new Dictionary<RouteQualifierName, RouteQualifierValue>
            {
                [new RouteQualifierName("district")] = new RouteQualifierValue("255901"),
            };

            var frontendRequest = new FrontendRequest(
                Path: "/ed-fi/students",
                Body: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("123"),
                RouteQualifiers: routeQualifiers
            );

            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET)
            {
                ClientAuthorizations = new ClientAuthorizations(
                    TokenId: "token123",
                    ClientId: "client123",
                    ClaimSetName: "test",
                    EducationOrganizationIds: [],
                    NamespacePrefixes: [],
                    DmsInstanceIds: [new DmsInstanceId(1), new DmsInstanceId(2)]
                ),
            };

            var (middleware, dmsInstanceProvider, _) = CreateMiddleware();

            // Both instances match - ambiguous!
            A.CallTo(() => dmsInstanceProvider.GetById(1))
                .Returns(
                    new DmsInstance(
                        Id: 1,
                        InstanceType: "Test",
                        InstanceName: "Instance 1",
                        ConnectionString: "connection1",
                        RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>
                        {
                            [new RouteQualifierName("district")] = new RouteQualifierValue("255901"),
                        }
                    )
                );

            A.CallTo(() => dmsInstanceProvider.GetById(2))
                .Returns(
                    new DmsInstance(
                        Id: 2,
                        InstanceType: "Test",
                        InstanceName: "Instance 2",
                        ConnectionString: "connection2",
                        RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>
                        {
                            [new RouteQualifierName("district")] = new RouteQualifierValue("255901"),
                        }
                    )
                );

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
        public void It_does_not_call_the_next_middleware()
        {
            _nextCalled.Should().BeFalse();
        }

        [Test]
        public void It_returns_400_bad_request()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_includes_ambiguous_routing_error()
        {
            _requestInfo.FrontendResponse.Body?.ToString().Should().Contain("Multiple database instances");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_No_Matching_Instance : ResolveDmsInstanceMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;

        [SetUp]
        public async Task Setup()
        {
            var routeQualifiers = new Dictionary<RouteQualifierName, RouteQualifierValue>
            {
                [new RouteQualifierName("district")] = new RouteQualifierValue("255901"),
            };

            var frontendRequest = new FrontendRequest(
                Path: "/ed-fi/students",
                Body: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("123"),
                RouteQualifiers: routeQualifiers
            );

            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET)
            {
                ClientAuthorizations = new ClientAuthorizations(
                    TokenId: "token123",
                    ClientId: "client123",
                    ClaimSetName: "test",
                    EducationOrganizationIds: [],
                    NamespacePrefixes: [],
                    DmsInstanceIds: [new DmsInstanceId(1)]
                ),
            };

            var (middleware, dmsInstanceProvider, _) = CreateMiddleware();

            // Instance has different route qualifiers
            A.CallTo(() => dmsInstanceProvider.GetById(1))
                .Returns(
                    new DmsInstance(
                        Id: 1,
                        InstanceType: "Test",
                        InstanceName: "Test Instance",
                        ConnectionString: "test-connection",
                        RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>
                        {
                            [new RouteQualifierName("district")] = new RouteQualifierValue("999"),
                        }
                    )
                );

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
        public void It_does_not_call_the_next_middleware()
        {
            _nextCalled.Should().BeFalse();
        }

        [Test]
        public void It_returns_404_not_found()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(404);
        }

        [Test]
        public void It_includes_no_match_error()
        {
            _requestInfo
                .FrontendResponse.Body?.ToString()
                .Should()
                .Contain("No database instance found matching");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Matching_Instance_With_No_Connection_String : ResolveDmsInstanceMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;

        [SetUp]
        public async Task Setup()
        {
            var frontendRequest = new FrontendRequest(
                Path: "/ed-fi/students",
                Body: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("123"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET)
            {
                ClientAuthorizations = new ClientAuthorizations(
                    TokenId: "token123",
                    ClientId: "client123",
                    ClaimSetName: "test",
                    EducationOrganizationIds: [],
                    NamespacePrefixes: [],
                    DmsInstanceIds: [new DmsInstanceId(1)]
                ),
            };

            var (middleware, dmsInstanceProvider, _) = CreateMiddleware();

            // Instance matches but has no connection string
            A.CallTo(() => dmsInstanceProvider.GetById(1))
                .Returns(
                    new DmsInstance(
                        Id: 1,
                        InstanceType: "Test",
                        InstanceName: "Test Instance",
                        ConnectionString: null, // No connection string!
                        RouteContext: []
                    )
                );

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
        public void It_does_not_call_the_next_middleware()
        {
            _nextCalled.Should().BeFalse();
        }

        [Test]
        public void It_returns_503_service_unavailable()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(503);
        }

        [Test]
        public void It_includes_configuration_error()
        {
            _requestInfo.FrontendResponse.Body?.ToString().Should().Contain("Database connection not");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Instance_Not_Found_In_Provider : ResolveDmsInstanceMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;

        [SetUp]
        public async Task Setup()
        {
            var frontendRequest = new FrontendRequest(
                Path: "/ed-fi/students",
                Body: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("123"),
                RouteQualifiers: []
            );

            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET)
            {
                ClientAuthorizations = new ClientAuthorizations(
                    TokenId: "token123",
                    ClientId: "client123",
                    ClaimSetName: "test",
                    EducationOrganizationIds: [],
                    NamespacePrefixes: [],
                    DmsInstanceIds: [new DmsInstanceId(999)]
                ),
            };

            var (middleware, dmsInstanceProvider, _) = CreateMiddleware();

            // Instance not found in provider
            A.CallTo(() => dmsInstanceProvider.GetById(999)).Returns(null);

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
        public void It_does_not_call_the_next_middleware()
        {
            _nextCalled.Should().BeFalse();
        }

        [Test]
        public void It_returns_404_not_found()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(404);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Case_Insensitive_Route_Qualifier_Values : ResolveDmsInstanceMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;

        [SetUp]
        public async Task Setup()
        {
            var routeQualifiers = new Dictionary<RouteQualifierName, RouteQualifierValue>
            {
                [new RouteQualifierName("environment")] = new RouteQualifierValue("Production"), // Mixed case
            };

            var frontendRequest = new FrontendRequest(
                Path: "/ed-fi/students",
                Body: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("123"),
                RouteQualifiers: routeQualifiers
            );

            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET)
            {
                ClientAuthorizations = new ClientAuthorizations(
                    TokenId: "token123",
                    ClientId: "client123",
                    ClaimSetName: "test",
                    EducationOrganizationIds: [],
                    NamespacePrefixes: [],
                    DmsInstanceIds: [new DmsInstanceId(1)]
                ),
            };

            var (middleware, dmsInstanceProvider, _) = CreateMiddleware();

            // Instance has lowercase value
            A.CallTo(() => dmsInstanceProvider.GetById(1))
                .Returns(
                    new DmsInstance(
                        Id: 1,
                        InstanceType: "Test",
                        InstanceName: "Test Instance",
                        ConnectionString: "test-connection",
                        RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>
                        {
                            [new RouteQualifierName("environment")] = new RouteQualifierValue("production"), // lowercase
                        }
                    )
                );

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
        public void It_matches_case_insensitively()
        {
            _nextCalled.Should().BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Mismatched_Qualifier_Count : ResolveDmsInstanceMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled = false;

        [SetUp]
        public async Task Setup()
        {
            var routeQualifiers = new Dictionary<RouteQualifierName, RouteQualifierValue>
            {
                [new RouteQualifierName("district")] = new RouteQualifierValue("255901"),
            };

            var frontendRequest = new FrontendRequest(
                Path: "/ed-fi/students",
                Body: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("123"),
                RouteQualifiers: routeQualifiers
            );

            _requestInfo = new RequestInfo(frontendRequest, RequestMethod.GET)
            {
                ClientAuthorizations = new ClientAuthorizations(
                    TokenId: "token123",
                    ClientId: "client123",
                    ClaimSetName: "test",
                    EducationOrganizationIds: [],
                    NamespacePrefixes: [],
                    DmsInstanceIds: [new DmsInstanceId(1)]
                ),
            };

            var (middleware, dmsInstanceProvider, _) = CreateMiddleware();

            // Instance has more qualifiers than request
            A.CallTo(() => dmsInstanceProvider.GetById(1))
                .Returns(
                    new DmsInstance(
                        Id: 1,
                        InstanceType: "Test",
                        InstanceName: "Test Instance",
                        ConnectionString: "test-connection",
                        RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>
                        {
                            [new RouteQualifierName("district")] = new RouteQualifierValue("255901"),
                            [new RouteQualifierName("schoolYear")] = new RouteQualifierValue("2024"),
                        }
                    )
                );

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
        public void It_does_not_match_different_qualifier_counts()
        {
            _nextCalled.Should().BeFalse();
            _requestInfo.FrontendResponse.StatusCode.Should().Be(404);
        }
    }
}
