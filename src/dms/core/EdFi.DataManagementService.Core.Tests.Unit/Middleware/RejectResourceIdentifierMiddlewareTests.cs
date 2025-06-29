// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class RejectResourceIdentifierMiddlewareTests
{
    internal static IPipelineStep Middleware()
    {
        return new RejectResourceIdentifierMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_Payload_With_ResourceId : RejectResourceIdentifierMiddlewareTests
    {
        private RequestData _context = No.RequestData();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest = new(
                Body: """{"id":"2a5a8b65-40c9-4524-8eb8-a1b3bb857b32","studentUniqueId":"333333","birthDate": "2017-08-26","firstName": "hello firstName","lastSurname":"lastSurname"}""",
                Headers: [],
                Path: "/ed-fi/students",
                QueryParameters: [],
                TraceId: new TraceId(""),
                ClientAuthorizations: new ClientAuthorizations(
                    TokenId: "",
                    ClaimSetName: "",
                    EducationOrganizationIds: [],
                    NamespacePrefixes: []
                )
            );
            _context = new(frontendRequest, RequestMethod.POST);
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_has_a_response()
        {
            _context?.FrontendResponse.Should().NotBe(No.FrontendResponse);
        }

        [Test]
        public void It_returns_status_400()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_resource_identifiers_validation_error()
        {
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("Resource identifiers cannot be assigned by the client");

            _context.FrontendResponse.Body?.ToJsonString().Should().Contain("id");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Post_Request_Payload_Without_ResourceId : RejectResourceIdentifierMiddlewareTests
    {
        private RequestData _context = No.RequestData();

        [SetUp]
        public async Task Setup()
        {
            FrontendRequest frontendRequest = new(
                Body: """{"studentUniqueId":"333333","birthDate": "2017-08-26","firstName": "hello firstName","lastSurname":"lastSurname"}""",
                Headers: [],
                Path: "/ed-fi/students",
                QueryParameters: [],
                TraceId: new TraceId(""),
                ClientAuthorizations: new ClientAuthorizations(
                    TokenId: "",
                    ClaimSetName: "",
                    EducationOrganizationIds: [],
                    NamespacePrefixes: []
                )
            );
            _context = new(frontendRequest, RequestMethod.POST);
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_provides_no_response()
        {
            _context?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }
}
