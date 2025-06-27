// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Modules;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
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
        var successResponse = new UploadSchemaResponse(
            Success: true,
            ReloadId: Guid.NewGuid(),
            SchemasProcessed: 1
        );

        A.CallTo(() => _apiService.UploadAndReloadApiSchemaAsync(request))
            .Returns(Task.FromResult(successResponse));

        // Act
        var result = await ManagementEndpointModule.UploadAndReloadApiSchema(request, _apiService, _logger);

        // Assert
        result.Should().BeOfType<Ok<UploadSchemaResponse>>();
        var okResult = (Ok<UploadSchemaResponse>)result;
        okResult.StatusCode.Should().Be(200);
        okResult.Value.Should().Be(successResponse);
    }

    [Test]
    public async Task UploadAndReloadApiSchema_Returns400BadRequest_WhenInvalidSchema()
    {
        // Arrange
        var request = new UploadSchemaRequest(CoreSchema: "", ExtensionSchemas: null);
        var errorResponse = new UploadSchemaResponse(
            Success: false,
            ErrorMessage: "Invalid core schema",
            IsValidationError: true
        );

        A.CallTo(() => _apiService.UploadAndReloadApiSchemaAsync(request))
            .Returns(Task.FromResult(errorResponse));

        // Act
        var result = await ManagementEndpointModule.UploadAndReloadApiSchema(request, _apiService, _logger);

        // Assert
        result.Should().BeOfType<BadRequest<UploadSchemaResponse>>();
        var badRequestResult = (BadRequest<UploadSchemaResponse>)result;
        badRequestResult.StatusCode.Should().Be(400);
        badRequestResult.Value.Should().Be(errorResponse);
    }

    [Test]
    public async Task UploadAndReloadApiSchema_Returns404NotFound_WhenDisabled()
    {
        // Arrange
        var request = new UploadSchemaRequest(CoreSchema: """{"test": "schema"}""", ExtensionSchemas: null);
        var errorResponse = new UploadSchemaResponse(
            Success: false,
            ErrorMessage: "Management endpoints are disabled",
            IsManagementEndpointsDisabled: true
        );

        A.CallTo(() => _apiService.UploadAndReloadApiSchemaAsync(request))
            .Returns(Task.FromResult(errorResponse));

        // Act
        var result = await ManagementEndpointModule.UploadAndReloadApiSchema(request, _apiService, _logger);

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
        var errorResponse = new UploadSchemaResponse(Success: false, ErrorMessage: "Internal error");

        A.CallTo(() => _apiService.UploadAndReloadApiSchemaAsync(request))
            .Returns(Task.FromResult(errorResponse));

        // Act
        var result = await ManagementEndpointModule.UploadAndReloadApiSchema(request, _apiService, _logger);

        // Assert
        result.Should().BeOfType<JsonHttpResult<UploadSchemaResponse>>();
        var jsonResult = (JsonHttpResult<UploadSchemaResponse>)result;
        jsonResult.StatusCode.Should().Be(500);
        jsonResult.Value.Should().Be(errorResponse);
    }

    [Test]
    public async Task UploadAndReloadApiSchema_LogsRequest()
    {
        // Arrange
        var request = new UploadSchemaRequest(CoreSchema: """{"test": "schema"}""", ExtensionSchemas: null);
        var successResponse = new UploadSchemaResponse(Success: true);

        A.CallTo(() => _apiService.UploadAndReloadApiSchemaAsync(request))
            .Returns(Task.FromResult(successResponse));

        // Act
        await ManagementEndpointModule.UploadAndReloadApiSchema(request, _apiService, _logger);

        // Assert
        A.CallTo(_logger)
            .Where(call =>
                call.Method.Name == "Log"
                && call.GetArgument<LogLevel>(0) == LogLevel.Information
                && call.GetArgument<object>(2)!
                    .ToString()!
                    .Contains("Schema upload and reload requested via management endpoint")
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task UploadAndReloadApiSchema_HandlesUnexpectedStatusCode()
    {
        // Arrange
        var request = new UploadSchemaRequest(CoreSchema: """{"test": "schema"}""", ExtensionSchemas: null);
        var errorResponse = new UploadSchemaResponse(Success: false, ErrorMessage: "Service unavailable");

        A.CallTo(() => _apiService.UploadAndReloadApiSchemaAsync(request))
            .Returns(Task.FromResult(errorResponse));

        // Act
        var result = await ManagementEndpointModule.UploadAndReloadApiSchema(request, _apiService, _logger);

        // Assert
        result.Should().BeOfType<JsonHttpResult<UploadSchemaResponse>>();
        var jsonResult = (JsonHttpResult<UploadSchemaResponse>)result;
        jsonResult.StatusCode.Should().Be(500);
        jsonResult.Value.Should().Be(errorResponse);
    }
}
