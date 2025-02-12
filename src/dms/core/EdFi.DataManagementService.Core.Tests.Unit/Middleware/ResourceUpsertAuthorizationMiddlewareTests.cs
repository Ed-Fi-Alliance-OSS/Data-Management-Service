// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.AuthorizationValidation;
using EdFi.DataManagementService.Core.Security.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
public class ResourceUpsertAuthorizationMiddlewareTests
{
    private PipelineContext _context = No.PipelineContext();

    internal static IPipelineStep Middleware()
    {
        var expectedAuthStrategy = "NoFurtherAuthorizationRequired";
        var authStrategyList = new List<string> { expectedAuthStrategy };
        var authorizationStrategiesProvider = A.Fake<IAuthorizationStrategiesProvider>();
        A.CallTo(
                () =>
                    authorizationStrategiesProvider.GetAuthorizationStrategies(
                        A<ResourceClaim>.Ignored,
                        A<string>.Ignored
                    )
            )
            .Returns(authStrategyList);
        var authorizationStrategyHandler = A.Fake<IAuthorizationServiceFactory>();
        A.CallTo(() => authorizationStrategyHandler.GetByName<IAuthorizationValidator>(A<string>.Ignored))
            .Returns(new NoFurtherAuthorizationRequiredValidator());
        return new ResourceUpsertAuthorizationMiddleware(
            authorizationStrategiesProvider,
            authorizationStrategyHandler,
            NullLogger.Instance
        );
    }

    [TestFixture]
    public class GivenMatchingResourceActionClaimAction : ResourceUpsertAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                ApiClientDetails: new ApiClientDetails("", "SIS-Vendor", [], [])
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.POST)
            {
                PathComponents = new PathComponents(
                    new ProjectNamespace("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
            };
            await Middleware().Execute(_context, TestHelper.NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class GivenMatchingResourceActionClaimActionAuthStrategy
        : ResourceUpsertAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                ApiClientDetails: new ApiClientDetails("", "SIS-Vendor", [], [])
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.POST)
            {
                PathComponents = new PathComponents(
                    new ProjectNamespace("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
            };
            await Middleware().Execute(_context, TestHelper.NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class GivenNoResourceActionClaimActionAuthStrategies : ResourceUpsertAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            var authorizationStrategiesProvider = A.Fake<IAuthorizationStrategiesProvider>();
            A.CallTo(
                    () =>
                        authorizationStrategiesProvider.GetAuthorizationStrategies(
                            A<ResourceClaim>.Ignored,
                            A<string>.Ignored
                        )
                )
                .Returns([]);
            var authorizationStrategyHandler = A.Fake<IAuthorizationValidatorProvider>();
            var authMiddleware = new ResourceUpsertAuthorizationMiddleware(
                authorizationStrategiesProvider,
                authorizationStrategyHandler,
                NullLogger.Instance
            );

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                ApiClientDetails: new ApiClientDetails("", "SIS-Vendor", [], [])
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.POST)
            {
                PathComponents = new PathComponents(
                    new ProjectNamespace("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
                ResourceClaim = new ResourceClaim() { Name = "schools" },
            };
            await authMiddleware.Execute(_context, TestHelper.NullNext);
            Console.Write(_context.FrontendResponse);
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_has_forbidden_response()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(403);
        }

        [Test]
        public void It_returns_message_body_with_failures()
        {
            _context.FrontendResponse.Body?.ToJsonString().Should().Contain("Authorization Denied");

            string response = JsonSerializer.Serialize(
                _context.FrontendResponse.Body,
                UtilityService.SerializerOptions
            );

            response
                .Should()
                .Contain(
                    "\"errors\":[\"No authorization strategies were defined for the requested action 'Create' against resource ['schools'] matched by the caller's claim 'SIS-Vendor'.\"]"
                );
        }
    }

    [TestFixture]
    public class GivenNoValidResourceActionClaimActionAuthStrategyHandler
        : ResourceUpsertAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            var authStrategy = "NotValidAuthStrategy";
            var claimSetCacheService = A.Fake<IClaimSetCacheService>();
            A.CallTo(() => claimSetCacheService.GetClaimSets())
                .Returns(
                    [
                        new ClaimSet(
                            Name: "SIS-Vendor",
                            ResourceClaims:
                            [
                                new ResourceClaim()
                                {
                                    Name = "schools",
                                    Actions = [new(Enabled: true, Name: "Create")],
                                },
                            ]
                        ),
                    ]
                );
            var authorizationStrategiesProvider = A.Fake<IAuthorizationStrategiesProvider>();
            A.CallTo(
                    () =>
                        authorizationStrategiesProvider.GetAuthorizationStrategies(
                            A<ResourceClaim>.Ignored,
                            A<string>.Ignored
                        )
                )
                .Returns([authStrategy]);
            var authorizationStrategyHandler = A.Fake<IAuthorizationValidatorProvider>();
            A.CallTo(() => authorizationStrategyHandler.GetByName(authStrategy)).Returns(null);
            var authMiddleware = new ResourceUpsertAuthorizationMiddleware(
                authorizationStrategiesProvider,
                authorizationStrategyHandler,
                NullLogger.Instance
            );

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                ApiClientDetails: new ApiClientDetails("", "SIS-Vendor", [], [])
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.POST)
            {
                PathComponents = new PathComponents(
                    new ProjectNamespace("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
            };
            await authMiddleware.Execute(_context, TestHelper.NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_has_forbidden_response()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(403);
        }

        [Test]
        public void It_returns_message_body_with_failures()
        {
            _context.FrontendResponse.Body?.ToJsonString().Should().Contain("Authorization Denied");

            string response = JsonSerializer.Serialize(
                _context.FrontendResponse.Body,
                UtilityService.SerializerOptions
            );

            response
                .Should()
                .Contain(
                    "\"errors\":[\"Could not find authorization strategy implementation for the following strategy: 'NotValidAuthStrategy'.\"]"
                );
        }
    }

    [TestFixture]
    public class Given_Request_Not_Authorized : ResourceUpsertAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            var authStrategy = "NoFurtherAuthorizationRequired";
            var claimSetCacheService = A.Fake<IClaimSetCacheService>();
            A.CallTo(() => claimSetCacheService.GetClaimSets())
                .Returns(
                    [
                        new ClaimSet(
                            Name: "SIS-Vendor",
                            ResourceClaims:
                            [
                                new ResourceClaim()
                                {
                                    Name = "schools",
                                    Actions = [new(Enabled: true, Name: "Create")],
                                },
                            ]
                        ),
                    ]
                );
            var authorizationStrategiesProvider = A.Fake<IAuthorizationStrategiesProvider>();
            A.CallTo(
                    () =>
                        authorizationStrategiesProvider.GetAuthorizationStrategies(
                            A<ResourceClaim>.Ignored,
                            A<string>.Ignored
                        )
                )
                .Returns([authStrategy]);
            var authorizationStrategyHandlerProvider = A.Fake<IAuthorizationServiceFactory>();

            var authorizationStrategyHandler = A.Fake<IAuthorizationValidator>();
            A.CallTo(
                    () =>
                        authorizationStrategyHandler.ValidateAuthorization(
                            A<DocumentSecurityElements>.Ignored,
                            A<ApiClientDetails>.Ignored
                        )
                )
                .Returns(new AuthorizationResult(false, "test-error"));

            A.CallTo(
                    () =>
                        authorizationStrategyHandlerProvider.GetByName<IAuthorizationValidator>(authStrategy)
                )
                .Returns(authorizationStrategyHandler);

            var authMiddleware = new ResourceUpsertAuthorizationMiddleware(
                authorizationStrategiesProvider,
                authorizationStrategyHandlerProvider,
                NullLogger.Instance
            );

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                ApiClientDetails: new ApiClientDetails("", "SIS-Vendor", [], [])
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.POST)
            {
                PathComponents = new PathComponents(
                    new ProjectNamespace("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
            };
            await authMiddleware.Execute(_context, TestHelper.NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_has_forbidden_response()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(403);
        }

        [Test]
        public void It_returns_message_body_with_failures()
        {
            _context.FrontendResponse.Body?.ToJsonString().Should().Contain("Authorization Denied");

            string response = JsonSerializer.Serialize(
                _context.FrontendResponse.Body,
                UtilityService.SerializerOptions
            );

            response.Should().Contain("\"errors\":[\"test-error\"]");
        }
    }
}
