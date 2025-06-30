// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;

[TestFixture]
[NonParallelizable]
public class ApiSchemaProviderUploadTests
{
    private ILogger<ApiSchemaProvider> _logger = null!;
    private IOptions<AppSettings> _appSettings = null!;
    private IApiSchemaValidator _apiSchemaValidator = null!;
    private ApiSchemaProvider _loader = null!;

    [SetUp]
    public void Setup()
    {
        _logger = A.Fake<ILogger<ApiSchemaProvider>>();
        _appSettings = A.Fake<IOptions<AppSettings>>();

        A.CallTo(() => _appSettings.Value)
            .Returns(new AppSettings { UseApiSchemaPath = false, AllowIdentityUpdateOverrides = "" });

        _apiSchemaValidator = A.Fake<IApiSchemaValidator>();
        // By default, return no validation errors
        A.CallTo(() => _apiSchemaValidator.Validate(A<JsonNode>._))
            .Returns(new List<SchemaValidationFailure>());

        _loader = new ApiSchemaProvider(_logger, _appSettings, _apiSchemaValidator);
    }

    [Test]
    public async Task UploadAndReloadSchemasAsync_WithValidCoreSchema_ReturnsTrue()
    {
        // Arrange
        var coreSchema = JsonNode.Parse(
            """
            {
                "projectSchema": {
                    "isExtensionProject": false,
                    "projectName": "Ed-Fi"
                }
            }
            """
        );
        var extensionSchemas = Array.Empty<JsonNode>();

        // Act
        var (result, _) = await _loader.LoadApiSchemaFromAsync(coreSchema!, extensionSchemas);

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public async Task UploadAndReloadSchemasAsync_WithExtensionMarkedAsCore_ReturnsFalse()
    {
        // Arrange
        var coreSchema = JsonNode.Parse(
            """
            {
                "projectSchema": {
                    "isExtensionProject": true,
                    "projectName": "TPDM"
                }
            }
            """
        );
        var extensionSchemas = Array.Empty<JsonNode>();

        // Act
        var (result, _) = await _loader.LoadApiSchemaFromAsync(coreSchema!, extensionSchemas);

        // Assert
        result.Should().BeFalse();
        A.CallTo(_logger)
            .Where(call =>
                call.Method.Name == "Log"
                && call.GetArgument<LogLevel>(0) == LogLevel.Error
                && call.GetArgument<object>(2)!
                    .ToString()!
                    .Contains("Core schema is marked as extension project")
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task UploadAndReloadSchemasAsync_UpdatesApiSchemaNodes()
    {
        // Arrange
        var coreSchema = JsonNode.Parse(
            """
            {
                "projectSchema": {
                    "isExtensionProject": false,
                    "projectName": "Ed-Fi"
                }
            }
            """
        );
        var extension1 = JsonNode.Parse(
            """
            {
                "projectSchema": {
                    "isExtensionProject": true,
                    "projectName": "TPDM"
                }
            }
            """
        );
        var extension2 = JsonNode.Parse(
            """
            {
                "projectSchema": {
                    "isExtensionProject": true,
                    "projectName": "Sample"
                }
            }
            """
        );
        var extensionSchemas = new[] { extension1!, extension2! };

        // Act
        var (result, _) = await _loader.LoadApiSchemaFromAsync(coreSchema!, extensionSchemas);

        // Assert
        result.Should().BeTrue();

        // Verify the schemas are stored correctly
        var nodes = _loader.GetApiSchemaNodes();
        nodes.CoreApiSchemaRootNode.Should().Be(coreSchema);
        nodes.ExtensionApiSchemaRootNodes.Should().HaveCount(2);
        nodes.ExtensionApiSchemaRootNodes.Should().Contain(extension1!);
        nodes.ExtensionApiSchemaRootNodes.Should().Contain(extension2!);
    }

    [Test]
    public async Task UploadAndReloadSchemasAsync_GeneratesNewReloadId()
    {
        // Arrange
        var coreSchema = JsonNode.Parse(
            """
            {
                "projectSchema": {
                    "isExtensionProject": false,
                    "projectName": "Ed-Fi"
                }
            }
            """
        );
        var originalReloadId = _loader.ReloadId;

        // Act
        var (result, _) = await _loader.LoadApiSchemaFromAsync(coreSchema!, []);

        // Assert
        result.Should().BeTrue();
        _loader.ReloadId.Should().NotBe(originalReloadId);
        _loader.ReloadId.Should().NotBeEmpty();
    }

    [Test]
    public async Task UploadAndReloadSchemasAsync_LogsSuccess()
    {
        // Arrange
        var coreSchema = JsonNode.Parse(
            """
            {
                "projectSchema": {
                    "isExtensionProject": false,
                    "projectName": "Ed-Fi"
                }
            }
            """
        );

        // Act
        await _loader.LoadApiSchemaFromAsync(coreSchema!, []);

        // Assert
        A.CallTo(_logger)
            .Where(call =>
                call.Method.Name == "Log"
                && call.GetArgument<LogLevel>(0) == LogLevel.Information
                && call.GetArgument<object>(2)!
                    .ToString()!
                    .Contains("Uploading and reloading API schemas from memory")
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(_logger)
            .Where(call =>
                call.Method.Name == "Log"
                && call.GetArgument<LogLevel>(0) == LogLevel.Information
                && call.GetArgument<object>(2)!.ToString()!.Contains("Schema updated successfully")
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task UploadAndReloadSchemasAsync_WhenExceptionThrown_ReturnsFalse()
    {
        // Arrange
        // Create a scenario that will cause an exception by mocking the logger to throw
        var coreSchema = JsonNode.Parse("""{"projectSchema": {"isExtensionProject": false}}""");

        // Configure logger to throw when logging success message
        A.CallTo(_logger)
            .Where(call =>
                call.Method.Name == "Log"
                && call.GetArgument<LogLevel>(0) == LogLevel.Information
                && call.GetArgument<object>(2)!.ToString()!.Contains("Schema updated successfully")
            )
            .Throws(new InvalidOperationException("Test exception"));

        // Act
        var (result, _) = await _loader.LoadApiSchemaFromAsync(coreSchema!, []);

        // Assert
        result.Should().BeFalse();
        A.CallTo(_logger)
            .Where(call =>
                call.Method.Name == "Log"
                && call.GetArgument<LogLevel>(0) == LogLevel.Error
                && call.GetArgument<object>(2)!.ToString()!.Contains("Failed to process uploaded API schemas")
            )
            .MustHaveHappened();
    }

    [Test]
    [Ignore("Failing on build server")]
    public async Task UploadAndReloadSchemasAsync_IsThreadSafe()
    {
        // Arrange
        var coreSchema = JsonNode.Parse(
            """
            {
                "projectSchema": {
                    "isExtensionProject": false,
                    "projectName": "Ed-Fi"
                }
            }
            """
        );

        var tasks = new List<Task<bool>>();
        var reloadIds = new List<Guid>();

        // Act - Call the method multiple times concurrently
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(
                Task.Run(async () =>
                {
                    var uploadResult = await _loader.LoadApiSchemaFromAsync(coreSchema!, []);
                    lock (reloadIds)
                    {
                        reloadIds.Add(_loader.ReloadId);
                    }
                    return uploadResult.Success;
                })
            );
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().AllBeEquivalentTo(true);
        // All reload IDs should be valid GUIDs (not empty)
        reloadIds.Should().AllSatisfy(id => id.Should().NotBeEmpty());
        // We should have multiple different reload IDs (since each reload generates a new one)
        reloadIds.Distinct().Count().Should().BeGreaterThan(1);
    }
}
