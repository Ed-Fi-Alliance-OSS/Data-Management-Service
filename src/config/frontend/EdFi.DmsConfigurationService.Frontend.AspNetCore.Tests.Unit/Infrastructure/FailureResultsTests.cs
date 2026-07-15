// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

public class FailureResultsTests
{
    /// <summary>
    /// Verifies that every FailureResults helper emits the Ed-Fi Problem Details contract with the
    /// application/problem+json content type, so endpoints, middleware, and authorization handling can
    /// rely on a single consistent error-response shape.
    /// </summary>
    [TestFixture]
    public class Given_A_Failure_Results_Helper
    {
        private const string CorrelationId = "test-correlation-id";
        private const string ProblemJsonContentType = "application/problem+json";
        private const string AuthenticationFailedDetail = "The caller could not be authenticated.";
        private const string AuthorizationDeniedDetail = "Access to the resource could not be authorized.";

        private static (int? StatusCode, string? ContentType, JsonNode Body) Inspect(IResult result)
        {
            result.Should().BeAssignableTo<IStatusCodeHttpResult>();
            result.Should().BeAssignableTo<IContentTypeHttpResult>();
            result.Should().BeAssignableTo<IValueHttpResult>();

            int? statusCode = ((IStatusCodeHttpResult)result).StatusCode;
            string? contentType = ((IContentTypeHttpResult)result).ContentType;
            var body = (JsonNode)((IValueHttpResult)result).Value!;
            return (statusCode, contentType, body);
        }

        [Test]
        public void It_Unknown_returns_compliant_internal_server_error()
        {
            var (statusCode, contentType, body) = Inspect(FailureResults.Unknown(CorrelationId));

            statusCode.Should().Be(500);
            contentType.Should().Be(ProblemJsonContentType);
            body["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:internal-server-error");
            body["title"]?.GetValue<string>().Should().Be("Internal Server Error");
            body["status"]?.GetValue<int>().Should().Be(500);
            body["correlationId"]?.GetValue<string>().Should().Be(CorrelationId);
            body["validationErrors"]?.AsObject().Count.Should().Be(0);
            body["errors"]?.AsArray().Count.Should().Be(0);
        }

        [Test]
        public void It_NotFound_returns_compliant_not_found()
        {
            var (statusCode, contentType, body) = Inspect(
                FailureResults.NotFound("Resource is not available.", CorrelationId)
            );

            statusCode.Should().Be(404);
            contentType.Should().Be(ProblemJsonContentType);
            body["detail"]?.GetValue<string>().Should().Be("Resource is not available.");
            body["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:not-found");
            body["title"]?.GetValue<string>().Should().Be("Not Found");
            body["status"]?.GetValue<int>().Should().Be(404);
            body["correlationId"]?.GetValue<string>().Should().Be(CorrelationId);
            body["validationErrors"]?.AsObject().Count.Should().Be(0);
            body["errors"]?.AsArray().Count.Should().Be(0);
        }

        [Test]
        public void It_BadRequest_returns_compliant_bad_request()
        {
            var (statusCode, contentType, body) = Inspect(
                FailureResults.BadRequest("The request could not be parsed.", CorrelationId)
            );

            statusCode.Should().Be(400);
            contentType.Should().Be(ProblemJsonContentType);
            body["detail"]?.GetValue<string>().Should().Be("The request could not be parsed.");
            body["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request");
            body["title"]?.GetValue<string>().Should().Be("Bad Request");
            body["status"]?.GetValue<int>().Should().Be(400);
            body["correlationId"]?.GetValue<string>().Should().Be(CorrelationId);
            body["validationErrors"]?.AsObject().Count.Should().Be(0);
            body["errors"]?.AsArray().Count.Should().Be(0);
        }

        [Test]
        public void It_DataValidation_returns_compliant_validation_failure_with_grouped_errors()
        {
            var validationFailures = new List<ValidationFailure>
            {
                new("Claims", "Claims JSON is required."),
                new("Claims", "Claims JSON is malformed."),
                new("Name", "Name is required."),
            };

            var (statusCode, contentType, body) = Inspect(
                FailureResults.DataValidation(validationFailures, CorrelationId)
            );

            statusCode.Should().Be(400);
            contentType.Should().Be(ProblemJsonContentType);
            body["detail"]
                ?.GetValue<string>()
                .Should()
                .Be("Data validation failed. See 'validationErrors' for details.");
            body["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request:data-validation-failed");
            body["title"]?.GetValue<string>().Should().Be("Data Validation Failed");
            body["status"]?.GetValue<int>().Should().Be(400);
            body["correlationId"]?.GetValue<string>().Should().Be(CorrelationId);

            var validationErrors = body["validationErrors"]!.AsObject();
            validationErrors.Count.Should().Be(2);
            validationErrors["Claims"]!.AsArray().Count.Should().Be(2);
            validationErrors["Name"]!.AsArray().Count.Should().Be(1);
            body["errors"]?.AsArray().Count.Should().Be(0);
        }

        [Test]
        public void It_Unauthorized_with_errors_returns_compliant_authentication_failure_using_errors_verbatim()
        {
            var (statusCode, contentType, body) = Inspect(
                FailureResults.Unauthorized(
                    ["Authentication is required to access this resource."],
                    CorrelationId
                )
            );

            statusCode.Should().Be(401);
            contentType.Should().Be(ProblemJsonContentType);
            body["detail"]?.GetValue<string>().Should().Be(AuthenticationFailedDetail);
            body["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:security:authentication");
            body["title"]?.GetValue<string>().Should().Be("Authentication Failed");
            body["status"]?.GetValue<int>().Should().Be(401);
            body["correlationId"]?.GetValue<string>().Should().Be(CorrelationId);
            body["validationErrors"]?.AsObject().Count.Should().Be(0);

            var errors = body["errors"]!.AsArray();
            errors.Count.Should().Be(1);
            errors[0]!.GetValue<string>().Should().Be("Authentication is required to access this resource.");
        }

        [Test]
        public void It_Forbidden_with_errors_returns_compliant_authorization_failure_using_errors_verbatim()
        {
            var (statusCode, contentType, body) = Inspect(
                FailureResults.Forbidden(["Registration is disabled."], CorrelationId)
            );

            statusCode.Should().Be(403);
            contentType.Should().Be(ProblemJsonContentType);
            body["detail"]?.GetValue<string>().Should().Be(AuthorizationDeniedDetail);
            body["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:security:authorization");
            body["title"]?.GetValue<string>().Should().Be("Authorization Denied");
            body["status"]?.GetValue<int>().Should().Be(403);
            body["correlationId"]?.GetValue<string>().Should().Be(CorrelationId);
            body["validationErrors"]?.AsObject().Count.Should().Be(0);

            var errors = body["errors"]!.AsArray();
            errors.Count.Should().Be(1);
            errors[0]!.GetValue<string>().Should().Be("Registration is disabled.");
        }

        /// <summary>
        /// Guards that the identity-provider Forbidden overload keeps its existing "Forbidden. " prefixing
        /// behavior, so the new authorization overload does not accidentally change token-endpoint errors.
        /// </summary>
        [Test]
        public void It_Forbidden_with_detail_preserves_identity_provider_prefix()
        {
            var (statusCode, contentType, body) = Inspect(
                FailureResults.Forbidden("Registration is disabled.", CorrelationId)
            );

            statusCode.Should().Be(403);
            contentType.Should().Be(ProblemJsonContentType);
            body["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:security:authorization");

            var errors = body["errors"]!.AsArray();
            errors.Count.Should().Be(1);
            errors[0]!.GetValue<string>().Should().Be("Forbidden. Registration is disabled.");
        }

        [Test]
        public void It_Unauthorized_uses_problem_json_content_type()
        {
            var (statusCode, contentType, body) = Inspect(
                FailureResults.Unauthorized("unauthorized_client", CorrelationId)
            );

            statusCode.Should().Be(401);
            contentType.Should().Be(ProblemJsonContentType);
            body["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:security:authentication");
        }

        [Test]
        public void It_BadGateway_uses_problem_json_content_type()
        {
            var (statusCode, contentType, body) = Inspect(
                FailureResults.BadGateway("Upstream failed.", CorrelationId)
            );

            statusCode.Should().Be(502);
            contentType.Should().Be(ProblemJsonContentType);
            body["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:bad-gateway");
        }
    }
}
