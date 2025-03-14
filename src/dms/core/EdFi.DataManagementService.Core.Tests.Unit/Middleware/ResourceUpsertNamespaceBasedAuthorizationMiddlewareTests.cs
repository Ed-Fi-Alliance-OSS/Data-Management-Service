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

    internal static IPipelineStep NoFurtherAuthorizationMiddleware()
    {
        var authorizationServiceFactory = A.Fake<IAuthorizationServiceFactory>();
        A.CallTo(
                () =>
                    authorizationServiceFactory.GetByName<IAuthorizationValidator>(
                        "NoFurtherAuthorizationRequired"
                    )
            )
            .Returns(new NoFurtherAuthorizationRequiredValidator());
        return new ResourceUpsertAuthorizationMiddleware(authorizationServiceFactory, NullLogger.Instance);
    }

    internal static IPipelineStep NullValidatorMiddleware()
    {
        var authorizationServiceFactory = A.Fake<IAuthorizationServiceFactory>();
        A.CallTo(() => authorizationServiceFactory.GetByName<IAuthorizationValidator>(A<string>.Ignored))
            .Returns(null);
        return new ResourceUpsertAuthorizationMiddleware(authorizationServiceFactory, NullLogger.Instance);
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
                ClientAuthorizations: new ClientAuthorizations("", "SIS-Vendor", [], [])
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.POST)
            {
                PathComponents = new PathComponents(
                    new ProjectNamespace("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
                ResourceActionAuthStrategies = ["NoFurtherAuthorizationRequired"],
            };
            await NoFurtherAuthorizationMiddleware().Execute(_context, TestHelper.NullNext);
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
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                ClientAuthorizations: new ClientAuthorizations("", "SIS-Vendor", [], [])
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.POST)
            {
                PathComponents = new PathComponents(
                    new ProjectNamespace("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
                ResourceClaim = new ResourceClaim() { Name = "schools" },
                ResourceActionAuthStrategies = ["SomeAuthStrategy"],
            };
            await NullValidatorMiddleware().Execute(_context, TestHelper.NullNext);
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
            var authorizationServiceFactory = A.Fake<IAuthorizationServiceFactory>();
            A.CallTo(() => authorizationServiceFactory.GetByName<IAuthorizationValidator>(A<string>.Ignored))
                .Returns(null);
            var authMiddleware = new ResourceUpsertAuthorizationMiddleware(
                authorizationServiceFactory,
                NullLogger.Instance
            );

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                ClientAuthorizations: new ClientAuthorizations("", "SIS-Vendor", [], [])
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.POST)
            {
                PathComponents = new PathComponents(
                    new ProjectNamespace("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
                ResourceActionAuthStrategies = ["NotValidAuthStrategy"],
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
            string authStrategy = "AnyAuthStrategy";

            var authorizationServiceFactory = A.Fake<IAuthorizationServiceFactory>();

            var notAuthorizedValidator = A.Fake<IAuthorizationValidator>();
            A.CallTo(
                    () =>
                        notAuthorizedValidator.ValidateAuthorization(
                            A<DocumentSecurityElements>.Ignored,
                            A<ClientAuthorizations>.Ignored
                        )
                )
                .Returns(new AuthorizationResult(false));
            A.CallTo(() => authorizationServiceFactory.GetByName<IAuthorizationValidator>(authStrategy))
                .Returns(notAuthorizedValidator);

            var authMiddleware = new ResourceUpsertAuthorizationMiddleware(
                authorizationServiceFactory,
                NullLogger.Instance
            );

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                ClientAuthorizations: new ClientAuthorizations("", "SIS-Vendor", [], [])
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.POST)
            {
                PathComponents = new PathComponents(
                    new ProjectNamespace("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
                ResourceActionAuthStrategies = [authStrategy],
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

            response.Should().Contain("Access to the resource could not be authorized.");
        }
    }
}
