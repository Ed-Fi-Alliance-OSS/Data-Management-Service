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
using EdFi.DataManagementService.Core.Security.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;
using static EdFi.DataManagementService.Core.UtilityService;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
public class ResourceAuthorizationMiddlewareTests
{
    private PipelineContext _context = No.PipelineContext();

    internal static IPipelineStep Middleware()
    {
        var securityMetadataService = A.Fake<ISecurityMetadataService>();
        A.CallTo(() => securityMetadataService.GetClaimSets())
            .Returns(
                [
                    new ClaimSet()
                    {
                        Name = "SIS-Vendor",
                        ResourceClaims =
                        [
                            new ResourceClaim()
                            {
                                Name = "schools",
                                Actions = [new() { Enabled = true, Name = "Create" }],
                            },
                        ],
                    },
                ]
            );
        return new ResourceAuthorizationMiddleware(securityMetadataService, NullLogger.Instance);
    }

    [TestFixture]
    public class Given_Matching_Resource_Claim : ResourceAuthorizationMiddlewareTests
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

            _context = new PipelineContext(frontEndRequest, RequestMethod.POST);
            _context.PathComponents = new PathComponents(
                new ProjectNamespace("ed-fi"),
                new EndpointName("schools"),
                new DocumentUuid()
            );
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_has_No_response()
        {
            _context?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class Given_No_Matching_ClaimSet : ResourceAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                ApiClientDetails: new ApiClientDetails("", "NO-MATCH", [], [])
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.POST);
            _context.PathComponents = new PathComponents(
                new ProjectNamespace("ed-fi"),
                new EndpointName("schools"),
                new DocumentUuid()
            );
            await Middleware().Execute(_context, NullNext);
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
    public class Given_No_Matching_ResourceClaim : ResourceAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/stateDescriptor",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                ApiClientDetails: new ApiClientDetails("", "SIS-Vendor", [], [])
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.POST);
            _context.PathComponents = new PathComponents(
                new ProjectNamespace("ed-fi"),
                new EndpointName("stateDescriptor"),
                new DocumentUuid()
            );
            await Middleware().Execute(_context, NullNext);
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
    public class Given_Matching_ResourceClaimAction : ResourceAuthorizationMiddlewareTests
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
                    new EndpointName("stateDescriptor"),
                    new DocumentUuid()
                ),
            };
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class Given_No_Matching_ResourceClaimAction : ResourceAuthorizationMiddlewareTests
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

            _context = new PipelineContext(frontEndRequest, RequestMethod.PUT)
            {
                PathComponents = new PathComponents(
                    new ProjectNamespace("ed-fi"),
                    new EndpointName("schools"),
                    new DocumentUuid()
                ),
            };
            await Middleware().Execute(_context, NullNext);
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

            string response = JsonSerializer.Serialize(_context.FrontendResponse.Body, SerializerOptions);

            response
                .Should()
                .Contain(
                    "\"errors\":[\"The API client's assigned claim set (currently 'SIS-Vendor') must grant permission of the 'Update' action on one of the following resource claims: schools\"]"
                );
        }
    }

    [TestFixture]
    public class Given_No_ResourceClaimActions : ResourceAuthorizationMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            var securityMetadataService = A.Fake<ISecurityMetadataService>();
            A.CallTo(() => securityMetadataService.GetClaimSets())
                .Returns(
                    [
                        new ClaimSet()
                        {
                            Name = "SIS-Vendor",
                            ResourceClaims = [new ResourceClaim() { Name = "schools", Actions = null }],
                        },
                    ]
                );
            var authMiddleware = new ResourceAuthorizationMiddleware(
                securityMetadataService,
                NullLogger.Instance
            );

            FrontendRequest frontEndRequest = new(
                Path: "ed-fi/schools",
                Body: """{ "schoolId":"12345", "nameOfInstitution":"School Test"}""",
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                ApiClientDetails: new ApiClientDetails("", "SIS-Vendor", [], [])
            );

            _context = new PipelineContext(frontEndRequest, RequestMethod.PUT)
            {
                PathComponents = new PathComponents(
                    new ProjectNamespace("ed-fi"),
                    new EndpointName("stateDescriptor"),
                    new DocumentUuid()
                ),
            };
            await authMiddleware.Execute(_context, NullNext);
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
}
