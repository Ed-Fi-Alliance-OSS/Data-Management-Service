// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;

[TestFixture]
[Parallelizable]
public class ApiSchemaUploadServiceTests
{
    private IApiSchemaProvider _apiSchemaProvider = null!;
    private ILogger<UploadApiSchemaService> _logger = null!;
    private IOptions<AppSettings> _appSettings = null!;
    private UploadApiSchemaService _service = null!;

    [SetUp]
    public void Setup()
    {
        _apiSchemaProvider = A.Fake<IApiSchemaProvider>();
        _logger = A.Fake<ILogger<UploadApiSchemaService>>();
        _appSettings = A.Fake<IOptions<AppSettings>>();

        A.CallTo(() => _appSettings.Value)
            .Returns(new AppSettings { EnableManagementEndpoints = true, AllowIdentityUpdateOverrides = "" });

        _service = new UploadApiSchemaService(_apiSchemaProvider, _logger, _appSettings);
    }

    [Test]
    public async Task UploadAndReloadApiSchemaAsync_WhenManagementEndpointsDisabled_Returns404()
    {
        // Arrange
        A.CallTo(() => _appSettings.Value)
            .Returns(
                new AppSettings { EnableManagementEndpoints = false, AllowIdentityUpdateOverrides = "" }
            );
        var request = new UploadSchemaRequest(CoreSchema: """{"test": "schema"}""", ExtensionSchemas: null);

        // Act
        var result = await _service.UploadApiSchemaAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Management endpoints are disabled");
        result.SchemasProcessed.Should().Be(0);
        result.IsValidationError.Should().BeFalse();
    }

    [Test]
    public async Task UploadAndReloadApiSchemaAsync_WhenCoreSchemaIsNull_Returns400()
    {
        // Arrange
        var request = new UploadSchemaRequest(CoreSchema: null!, ExtensionSchemas: null);

        // Act
        var result = await _service.UploadApiSchemaAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Core ApiSchema is required");
        result.SchemasProcessed.Should().Be(0);
        result.IsValidationError.Should().BeTrue();
    }

    [Test]
    public async Task UploadAndReloadApiSchemaAsync_WhenCoreSchemaIsEmpty_Returns400()
    {
        // Arrange
        var request = new UploadSchemaRequest(CoreSchema: "", ExtensionSchemas: null);

        // Act
        var result = await _service.UploadApiSchemaAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Core ApiSchema is required");
        result.SchemasProcessed.Should().Be(0);
        result.IsValidationError.Should().BeTrue();
    }

    [Test]
    public async Task UploadAndReloadApiSchemaAsync_WhenCoreSchemaIsInvalidJson_Returns400()
    {
        // Arrange
        var request = new UploadSchemaRequest(CoreSchema: "not valid json", ExtensionSchemas: null);

        // Act
        var result = await _service.UploadApiSchemaAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Invalid core ApiSchema JSON");
        result.SchemasProcessed.Should().Be(0);
        result.IsValidationError.Should().BeTrue();
    }

    [Test]
    public async Task UploadAndReloadApiSchemaAsync_WhenExtensionSchemaIsInvalidJson_Returns400()
    {
        // Arrange
        var request = new UploadSchemaRequest(
            CoreSchema: """{"valid": "json"}""",
            ExtensionSchemas: ["not valid json"]
        );

        // Act
        var result = await _service.UploadApiSchemaAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Invalid extension ApiSchema JSON at index 0");
        result.SchemasProcessed.Should().Be(0);
        result.IsValidationError.Should().BeTrue();
    }

    [Test]
    public async Task UploadAndReloadApiSchemaAsync_WhenUploadSucceeds_Returns200()
    {
        // Arrange
        var reloadId = Guid.NewGuid();
        var request = new UploadSchemaRequest(CoreSchema: """{"valid": "json"}""", ExtensionSchemas: null);

        A.CallTo(() => _apiSchemaProvider.LoadApiSchemaFromAsync(A<JsonNode>._, A<JsonNode[]>._))
            .Returns(Task.FromResult(new ApiSchemaLoadStatus(true, [])));

        A.CallTo(() => _apiSchemaProvider.ReloadId).Returns(reloadId);

        // Act
        var result = await _service.UploadApiSchemaAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        result.ReloadId.Should().Be(reloadId);
        result.SchemasProcessed.Should().Be(1);
        result.ErrorMessage.Should().BeNull();
        result.IsValidationError.Should().BeFalse();
    }

    [Test]
    public async Task UploadAndReloadApiSchemaAsync_WithExtensionSchemas_CountsCorrectly()
    {
        // Arrange
        var reloadId = Guid.NewGuid();
        var request = new UploadSchemaRequest(
            CoreSchema: """{"valid": "core"}""",
            ExtensionSchemas: ["""{"valid": "ext1"}""", """{"valid": "ext2"}"""]
        );

        A.CallTo(() => _apiSchemaProvider.LoadApiSchemaFromAsync(A<JsonNode>._, A<JsonNode[]>._))
            .Returns(Task.FromResult(new ApiSchemaLoadStatus(true, [])));

        A.CallTo(() => _apiSchemaProvider.ReloadId).Returns(reloadId);

        // Act
        var result = await _service.UploadApiSchemaAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        result.SchemasProcessed.Should().Be(3); // 1 core + 2 extensions
        result.IsValidationError.Should().BeFalse();
    }

    [Test]
    public async Task UploadAndReloadApiSchemaAsync_WhenUploadFails_Returns500()
    {
        // Arrange
        var request = new UploadSchemaRequest(CoreSchema: """{"valid": "json"}""", ExtensionSchemas: null);

        A.CallTo(() => _apiSchemaProvider.LoadApiSchemaFromAsync(A<JsonNode>._, A<JsonNode[]>._))
            .Returns(Task.FromResult(new ApiSchemaLoadStatus(false, [])));

        // Act
        var result = await _service.UploadApiSchemaAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Failed to upload ApiSchema");
        result.SchemasProcessed.Should().Be(0);
        result.IsValidationError.Should().BeFalse();
    }

    [Test]
    public async Task UploadAndReloadApiSchemaAsync_WhenExceptionThrown_Returns500()
    {
        // Arrange
        var request = new UploadSchemaRequest(CoreSchema: """{"valid": "json"}""", ExtensionSchemas: null);

        A.CallTo(() => _apiSchemaProvider.LoadApiSchemaFromAsync(A<JsonNode>._, A<JsonNode[]>._))
            .Throws<Exception>();

        // Act
        var result = await _service.UploadApiSchemaAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Internal error during ApiSchema upload");
        result.SchemasProcessed.Should().Be(0);
        result.IsValidationError.Should().BeFalse();
    }

    [Test]
    public async Task UploadAndReloadApiSchemaAsync_LogsAppropriateMessages()
    {
        // Arrange
        var request = new UploadSchemaRequest(CoreSchema: """{"valid": "json"}""", ExtensionSchemas: null);

        A.CallTo(() => _apiSchemaProvider.LoadApiSchemaFromAsync(A<JsonNode>._, A<JsonNode[]>._))
            .Returns(Task.FromResult(new ApiSchemaLoadStatus(true, [])));

        // Act
        await _service.UploadApiSchemaAsync(request);

        // Assert - verify logging calls were made
        A.CallTo(_logger)
            .Where(call =>
                call.Method.Name == "Log"
                && call.GetArgument<LogLevel>(0) == LogLevel.Information
                && call.GetArgument<object>(2)!.ToString()!.Contains("Processing ApiSchema upload request")
            )
            .MustHaveHappenedOnceExactly();

        A.CallTo(_logger)
            .Where(call =>
                call.Method.Name == "Log"
                && call.GetArgument<LogLevel>(0) == LogLevel.Information
                && call.GetArgument<object>(2)!
                    .ToString()!
                    .Contains("ApiSchema upload completed successfully")
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task UploadAndReloadApiSchemaAsync_WhenUploadFailsWithValidationErrors_ReturnsValidationError()
    {
        // Arrange
        var request = new UploadSchemaRequest(CoreSchema: """{"valid": "json"}""", ExtensionSchemas: null);
        var failures = new List<ApiSchemaFailure>
        {
            new ApiSchemaFailure("Validation", "Schema is missing required property", new JsonPath("$.test")),
        };

        A.CallTo(() => _apiSchemaProvider.LoadApiSchemaFromAsync(A<JsonNode>._, A<JsonNode[]>._))
            .Returns(Task.FromResult(new ApiSchemaLoadStatus(false, failures)));

        // Act
        var result = await _service.UploadApiSchemaAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Schema is missing required property");
        result.SchemasProcessed.Should().Be(0);
        result.IsValidationError.Should().BeTrue();
    }

    [Test]
    public async Task UploadAndReloadApiSchemaAsync_WhenUploadFailsWithNonValidationErrors_ReturnsNonValidationError()
    {
        // Arrange
        var request = new UploadSchemaRequest(CoreSchema: """{"valid": "json"}""", ExtensionSchemas: null);
        var failures = new List<ApiSchemaFailure>
        {
            new ApiSchemaFailure("Configuration", "Invalid configuration detected"),
        };

        A.CallTo(() => _apiSchemaProvider.LoadApiSchemaFromAsync(A<JsonNode>._, A<JsonNode[]>._))
            .Returns(Task.FromResult(new ApiSchemaLoadStatus(false, failures)));

        // Act
        var result = await _service.UploadApiSchemaAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Invalid configuration detected");
        result.SchemasProcessed.Should().Be(0);
        result.IsValidationError.Should().BeFalse();
    }

    [Test]
    public async Task UploadAndReloadApiSchemaAsync_PassesCorrectJsonNodesToService()
    {
        // Arrange
        var coreSchema = """{"core": true}""";
        var extension1 = """{"extension": 1}""";
        var extension2 = """{"extension": 2}""";

        var request = new UploadSchemaRequest(
            CoreSchema: coreSchema,
            ExtensionSchemas: [extension1, extension2]
        );

        JsonNode? capturedCoreNode = null;
        JsonNode[]? capturedExtensions = null;

        A.CallTo(() => _apiSchemaProvider.LoadApiSchemaFromAsync(A<JsonNode>._, A<JsonNode[]>._))
            .Invokes(
                (JsonNode core, JsonNode[] exts) =>
                {
                    capturedCoreNode = core;
                    capturedExtensions = exts;
                }
            )
            .Returns(Task.FromResult(new ApiSchemaLoadStatus(true, [])));

        // Act
        await _service.UploadApiSchemaAsync(request);

        // Assert
        capturedCoreNode.Should().NotBeNull();
        capturedCoreNode!.ToJsonString().Should().Be(JsonNode.Parse(coreSchema)!.ToJsonString());

        capturedExtensions.Should().NotBeNull();
        capturedExtensions!.Length.Should().Be(2);
        capturedExtensions[0].ToJsonString().Should().Be(JsonNode.Parse(extension1)!.ToJsonString());
        capturedExtensions[1].ToJsonString().Should().Be(JsonNode.Parse(extension2)!.ToJsonString());
    }
}
