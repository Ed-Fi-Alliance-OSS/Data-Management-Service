// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Modules;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
[Parallelizable]
public class ManagementEndpointModuleUploadTests
{
    private IApiService _apiService = null!;
    private ILogger<ManagementEndpointModule> _logger = null!;

    [SetUp]
    public void Setup()
    {
        _apiService = A.Fake<IApiService>();
        _logger = A.Fake<ILogger<ManagementEndpointModule>>();
    }

    [Test]
    public async Task UploadAndReloadApiSchema_Returns200Ok_WhenSuccessful()
    {
        // Arrange
        var request = new UploadSchemaRequest(CoreSchema: """{"test": "schema"}""", ExtensionSchemas: null);
        var reloadId = Guid.NewGuid();
        var successResponse = A.Fake<IFrontendResponse>();
        A.CallTo(() => successResponse.StatusCode).Returns(200);
        A.CallTo(() => successResponse.Body)
            .Returns(
                JsonNode.Parse(
                    $$$"""
                    {
                        "message": "Schema uploaded successfully",
                        "reloadId": "{{{reloadId}}}",
                        "schemasProcessed": 1
                    }
                    """
                )
            );

        A.CallTo(() => _apiService.UploadApiSchemaAsync(request)).Returns(Task.FromResult(successResponse));

        // Act
        var result = await ManagementEndpointModule.UploadApiSchema(request, _apiService, _logger);

        // Assert
        result.Should().BeOfType<JsonHttpResult<JsonNode?>>();
        var jsonResult = (JsonHttpResult<JsonNode?>)result;
        jsonResult.StatusCode.Should().Be(200);
        jsonResult.Value.Should().BeEquivalentTo(successResponse.Body);
    }

    [Test]
    public async Task UploadAndReloadApiSchema_Returns400BadRequest_WhenInvalidSchema()
    {
        // Arrange
        var request = new UploadSchemaRequest(CoreSchema: "", ExtensionSchemas: null);
        var errorResponse = A.Fake<IFrontendResponse>();
        A.CallTo(() => errorResponse.StatusCode).Returns(400);
        A.CallTo(() => errorResponse.Body).Returns(JsonNode.Parse("""{"error": "Invalid core schema"}"""));

        A.CallTo(() => _apiService.UploadApiSchemaAsync(request)).Returns(Task.FromResult(errorResponse));

        // Act
        var result = await ManagementEndpointModule.UploadApiSchema(request, _apiService, _logger);

        // Assert
        result.Should().BeOfType<JsonHttpResult<JsonNode?>>();
        var jsonResult = (JsonHttpResult<JsonNode?>)result;
        jsonResult.StatusCode.Should().Be(400);
        jsonResult.Value.Should().BeEquivalentTo(errorResponse.Body);
    }

    [Test]
    public async Task UploadAndReloadApiSchema_Returns404NotFound_WhenDisabled()
    {
        // Arrange
        var request = new UploadSchemaRequest(CoreSchema: """{"test": "schema"}""", ExtensionSchemas: null);
        var errorResponse = A.Fake<IFrontendResponse>();
        A.CallTo(() => errorResponse.StatusCode).Returns(404);
        A.CallTo(() => errorResponse.Body).Returns((JsonNode?)null);

        A.CallTo(() => _apiService.UploadApiSchemaAsync(request)).Returns(Task.FromResult(errorResponse));

        // Act
        var result = await ManagementEndpointModule.UploadApiSchema(request, _apiService, _logger);

        // Assert
        result.Should().BeOfType<NotFound>();
        var notFoundResult = (NotFound)result;
        notFoundResult.StatusCode.Should().Be(404);
    }

    [Test]
    public async Task UploadAndReloadApiSchema_Returns500ServerError_WhenUploadFails()
    {
        // Arrange
        var request = new UploadSchemaRequest(CoreSchema: """{"test": "schema"}""", ExtensionSchemas: null);
        var errorResponse = A.Fake<IFrontendResponse>();
        A.CallTo(() => errorResponse.StatusCode).Returns(500);
        A.CallTo(() => errorResponse.Body).Returns(JsonNode.Parse("""{"error": "Internal error"}"""));

        A.CallTo(() => _apiService.UploadApiSchemaAsync(request)).Returns(Task.FromResult(errorResponse));

        // Act
        var result = await ManagementEndpointModule.UploadApiSchema(request, _apiService, _logger);

        // Assert
        result.Should().BeOfType<JsonHttpResult<JsonNode?>>();
        var jsonResult = (JsonHttpResult<JsonNode?>)result;
        jsonResult.StatusCode.Should().Be(500);
        jsonResult.Value.Should().BeEquivalentTo(errorResponse.Body);
    }

    [Test]
    public async Task UploadAndReloadApiSchema_LogsRequest()
    {
        // Arrange
        var request = new UploadSchemaRequest(CoreSchema: """{"test": "schema"}""", ExtensionSchemas: null);
        var successResponse = A.Fake<IFrontendResponse>();
        A.CallTo(() => successResponse.StatusCode).Returns(200);
        A.CallTo(() => successResponse.Body)
            .Returns(JsonNode.Parse("""{"message": "Schema uploaded successfully"}"""));

        A.CallTo(() => _apiService.UploadApiSchemaAsync(request)).Returns(Task.FromResult(successResponse));

        // Act
        await ManagementEndpointModule.UploadApiSchema(request, _apiService, _logger);

        // Assert
        A.CallTo(_logger)
            .Where(call =>
                call.Method.Name == "Log"
                && call.GetArgument<LogLevel>(0) == LogLevel.Information
                && call.GetArgument<object>(2)!
                    .ToString()!
                    .Contains("Schema upload requested via management endpoint")
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task UploadAndReloadApiSchema_HandlesUnexpectedStatusCode()
    {
        // Arrange
        var request = new UploadSchemaRequest(CoreSchema: """{"test": "schema"}""", ExtensionSchemas: null);
        var errorResponse = A.Fake<IFrontendResponse>();
        A.CallTo(() => errorResponse.StatusCode).Returns(503);
        A.CallTo(() => errorResponse.Body).Returns(JsonNode.Parse("""{"error": "Service unavailable"}"""));

        A.CallTo(() => _apiService.UploadApiSchemaAsync(request)).Returns(Task.FromResult(errorResponse));

        // Act
        var result = await ManagementEndpointModule.UploadApiSchema(request, _apiService, _logger);

        // Assert
        result.Should().BeOfType<StatusCodeHttpResult>();
        var statusResult = (StatusCodeHttpResult)result;
        statusResult.StatusCode.Should().Be(503);
    }
}
