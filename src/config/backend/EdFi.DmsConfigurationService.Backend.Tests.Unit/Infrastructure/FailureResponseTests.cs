// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using FluentAssertions;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Infrastructure;

[TestFixture]
public class FailureResponseTests
{
    private const string CorrelationId = "test-correlation-id";

    [Test]
    public void ForUnauthorized_ShouldReturnCorrectJsonNode()
    {
        // Arrange
        string title = "Unauthorized";
        string detail = "Access denied";
        string[] errors = { "Error1", "Error2" };

        // Act
        var result = FailureResponse.ForUnauthorized(title, detail, CorrelationId, errors);

        // Assert
        result.Should().BeOfType<JsonObject>();
        result["detail"]?.GetValue<string>().Should().Be(detail);
        result["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:security:authentication");
        result["title"]?.GetValue<string>().Should().Be(title);
        result["status"]?.GetValue<int>().Should().Be(401);
        result["correlationId"]?.GetValue<string>().Should().Be(CorrelationId);
        result["errors"]?.AsArray().Count.Should().Be(2);
    }

    [Test]
    public void ForForbidden_ShouldReturnCorrectJsonNode()
    {
        // Arrange
        string title = "Forbidden";
        string detail = "Access forbidden";
        string[] errors = { "Error1", "Error2" };

        // Act
        var result = FailureResponse.ForForbidden(title, detail, CorrelationId, errors);

        // Assert
        result.Should().BeOfType<JsonObject>();
        result["detail"]?.GetValue<string>().Should().Be(detail);
        result["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:security:authorization");
        result["title"]?.GetValue<string>().Should().Be(title);
        result["status"]?.GetValue<int>().Should().Be(403);
        result["correlationId"]?.GetValue<string>().Should().Be(CorrelationId);
        result["errors"]?.AsArray().Count.Should().Be(2);
    }

    [Test]
    public void ForBadRequest_ShouldReturnCorrectJsonNode()
    {
        // Arrange
        string detail = "Bad request";

        // Act
        var result = FailureResponse.ForBadRequest(detail, CorrelationId);

        // Assert
        result.Should().BeOfType<JsonObject>();
        result["detail"]?.GetValue<string>().Should().Be(detail);
        result["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request");
        result["title"]?.GetValue<string>().Should().Be("Bad Request");
        result["status"]?.GetValue<int>().Should().Be(400);
        result["correlationId"]?.GetValue<string>().Should().Be(CorrelationId);
    }

    [Test]
    public void ForNotFound_ShouldReturnCorrectJsonNode()
    {
        // Arrange
        string detail = "Not found";

        // Act
        var result = FailureResponse.ForNotFound(detail, CorrelationId);

        // Assert
        result.Should().BeOfType<JsonObject>();
        result["detail"]?.GetValue<string>().Should().Be(detail);
        result["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:not-found");
        result["title"]?.GetValue<string>().Should().Be("Not Found");
        result["status"]?.GetValue<int>().Should().Be(404);
        result["correlationId"]?.GetValue<string>().Should().Be(CorrelationId);
    }

    [Test]
    public void ForConflict_ShouldReturnCorrectJsonNode()
    {
        // Arrange
        string detail = "Conflict";

        // Act
        var result = FailureResponse.ForConflict(detail, CorrelationId);

        // Assert
        result.Should().BeOfType<JsonObject>();
        result["detail"]?.GetValue<string>().Should().Be(detail);
        result["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:conflict");
        result["title"]?.GetValue<string>().Should().Be("Conflict");
        result["status"]?.GetValue<int>().Should().Be(409);
        result["correlationId"]?.GetValue<string>().Should().Be(CorrelationId);
    }

    [Test]
    public void It_returns_the_correct_method_not_allowed_response()
    {
        // Arrange
        string detail = "The request method is not allowed for this resource.";

        // Act
        var result = FailureResponse.ForMethodNotAllowed(detail, CorrelationId);

        // Assert
        result.Should().BeOfType<JsonObject>();
        result["detail"]?.GetValue<string>().Should().Be(detail);
        result["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:method-not-allowed");
        result["title"]?.GetValue<string>().Should().Be("Method Not Allowed");
        result["status"]?.GetValue<int>().Should().Be(405);
        result["correlationId"]?.GetValue<string>().Should().Be(CorrelationId);
        result["validationErrors"]?.AsObject().Count.Should().Be(0);
        result["errors"]?.AsArray().Count.Should().Be(0);
    }

    [Test]
    public void It_returns_the_correct_unsupported_media_type_response()
    {
        // Arrange
        string detail = "The request content type is not supported.";

        // Act
        var result = FailureResponse.ForUnsupportedMediaType(detail, CorrelationId);

        // Assert
        result.Should().BeOfType<JsonObject>();
        result["detail"]?.GetValue<string>().Should().Be(detail);
        result["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:unsupported-media-type");
        result["title"]?.GetValue<string>().Should().Be("Unsupported Media Type");
        result["status"]?.GetValue<int>().Should().Be(415);
        result["correlationId"]?.GetValue<string>().Should().Be(CorrelationId);
        result["validationErrors"]?.AsObject().Count.Should().Be(0);
        result["errors"]?.AsArray().Count.Should().Be(0);
    }

    [Test]
    public void It_returns_the_complete_grouped_data_validation_contract()
    {
        // Arrange
        var validationFailures = new List<ValidationFailure>
        {
            new("Property1", "Error1"),
            new("Property1", "Error2"),
            new("Property2", "Error3"),
        };

        // Act
        var result = FailureResponse.ForDataValidation(validationFailures, CorrelationId);

        // Assert
        result.Should().BeOfType<JsonObject>();

        // Exactly the Ed-Fi Problem Details members, no more and no fewer.
        result
            .AsObject()
            .Select(property => property.Key)
            .Should()
            .BeEquivalentTo(
                "detail",
                "type",
                "title",
                "status",
                "correlationId",
                "validationErrors",
                "errors"
            );

        result["detail"]
            ?.GetValue<string>()
            .Should()
            .Be("Data validation failed. See 'validationErrors' for details.");
        result["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request:data");
        result["title"]?.GetValue<string>().Should().Be("Data Validation Failed");
        result["status"]?.GetValue<int>().Should().Be(400);
        result["correlationId"]?.GetValue<string>().Should().Be(CorrelationId);

        // Each property keeps its own messages, in order, with nothing lost, duplicated, moved between
        // properties, or reordered.
        var validationErrors = result["validationErrors"]!.AsObject();
        validationErrors.Select(property => property.Key).Should().BeEquivalentTo("Property1", "Property2");
        validationErrors["Property1"]!
            .AsArray()
            .Select(value => value!.GetValue<string>())
            .Should()
            .Equal("Error1", "Error2");
        validationErrors["Property2"]!
            .AsArray()
            .Select(value => value!.GetValue<string>())
            .Should()
            .Equal("Error3");

        result["errors"]!.AsArray().Should().BeEmpty();
    }

    [Test]
    public void ForNonUniqueIdentity_ShouldReturnCorrectJsonNode()
    {
        // Arrange
        string detail =
            "The identifying value(s) of the item are the same as another item that already exists.";
        string[] errors = ["A claim set with this name already exists."];

        // Act
        var result = FailureResponse.ForNonUniqueIdentity(detail, CorrelationId, errors);

        // Assert
        result.Should().BeOfType<JsonObject>();
        result["detail"]?.GetValue<string>().Should().Be(detail);
        result["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:conflict:non-unique-identity");
        result["title"]?.GetValue<string>().Should().Be("Identifying Values Are Not Unique");
        result["status"]?.GetValue<int>().Should().Be(409);
        result["correlationId"]?.GetValue<string>().Should().Be(CorrelationId);
        result["errors"]?.AsArray().Should().ContainSingle(error => error!.GetValue<string>() == errors[0]);
    }

    [Test]
    public void ForBadGateway_ShouldReturnCorrectJsonNode()
    {
        // Arrange
        string detail = "Bad gateway";
        string[] errors = { "Error1", "Error2" };

        // Act
        var result = FailureResponse.ForBadGateway(detail, CorrelationId, errors);

        // Assert
        result.Should().BeOfType<JsonObject>();
        result["detail"]?.GetValue<string>().Should().Be(detail);
        result["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:bad-gateway");
        result["title"]?.GetValue<string>().Should().Be("Bad Gateway");
        result["status"]?.GetValue<int>().Should().Be(502);
        result["correlationId"]?.GetValue<string>().Should().Be(CorrelationId);
        result["errors"]?.AsArray().Count.Should().Be(2);
    }

    [Test]
    public void ForUnknown_ShouldReturnCorrectJsonNode()
    {
        // Act
        var result = FailureResponse.ForUnknown(CorrelationId);

        // Assert
        result.Should().BeOfType<JsonObject>();
        result["detail"]?.GetValue<string>().Should().Be("");
        result["type"]?.GetValue<string>().Should().Be("urn:ed-fi:api:internal-server-error");
        result["title"]?.GetValue<string>().Should().Be("Internal Server Error");
        result["status"]?.GetValue<int>().Should().Be(500);
        result["correlationId"]?.GetValue<string>().Should().Be(CorrelationId);
    }
}
