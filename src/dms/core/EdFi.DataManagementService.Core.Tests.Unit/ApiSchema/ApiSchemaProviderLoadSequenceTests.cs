// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;

[TestFixture]
[NonParallelizable]
public class ApiSchemaProviderLoadSequenceTests
{
    private string _testDirectory = null!;
    private IOptions<AppSettings> _appSettings = null!;
    private IApiSchemaValidator _apiSchemaValidator = null!;

    [SetUp]
    public void Setup()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);

        _appSettings = Options.Create(
            new AppSettings
            {
                AllowIdentityUpdateOverrides = "",
                UseApiSchemaPath = true,
                ApiSchemaPath = _testDirectory,
            }
        );

        _apiSchemaValidator = A.Fake<IApiSchemaValidator>();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [Test]
    public async Task InitialLoad_WithInvalidSchema_SetsIsSchemaValidToFalse()
    {
        // Arrange
        var validationErrors = new List<SchemaValidationFailure> { new(new("$.test"), ["Test error"]) };
        A.CallTo(() => _apiSchemaValidator.Validate(A<JsonNode>._)).Returns(validationErrors);

        await WriteTestSchemaFile("ApiSchema.json", CreateValidApiSchema());
        var loader = new ApiSchemaProvider(
            NullLogger<ApiSchemaProvider>.Instance,
            _appSettings,
            _apiSchemaValidator
        );

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => loader.GetApiSchemaNodes());
        loader.IsSchemaValid.Should().BeFalse();
        loader.ApiSchemaFailures.Should().HaveCount(validationErrors.Count);
    }

    [Test]
    public async Task InitialLoad_ThenInvalidUpload_KeepsValidSchemaAndIsSchemaValidTrue()
    {
        // Arrange - Start with valid schema
        A.CallTo(() => _apiSchemaValidator.Validate(A<JsonNode>._))
            .Returns(new List<SchemaValidationFailure>());

        await WriteTestSchemaFile("ApiSchema.json", CreateValidApiSchema());
        var loader = new ApiSchemaProvider(
            NullLogger<ApiSchemaProvider>.Instance,
            _appSettings,
            _apiSchemaValidator
        );

        // Act 1 - Initial load with valid schema
        var initialNodes = loader.GetApiSchemaNodes();
        var initialReloadId = loader.ReloadId;
        loader.IsSchemaValid.Should().BeTrue();

        // Arrange 2 - Configure validator to return errors for next schema
        var validationErrors = new List<SchemaValidationFailure>
        {
            new(new("$.invalid"), ["Schema is invalid"]),
        };
        A.CallTo(() => _apiSchemaValidator.Validate(A<JsonNode>._)).Returns(validationErrors);

        // Act 2 - Upload invalid schema
        var invalidSchema = JsonNode.Parse("""{"projectSchema": {"isExtensionProject": false}}""");
        var uploadResult = await loader.LoadApiSchemaFromAsync(invalidSchema!, []);

        // Assert - Invalid upload failed, but existing schema remains
        uploadResult.Success.Should().BeFalse();
        loader.IsSchemaValid.Should().BeTrue(); // Still true!
        loader.ReloadId.Should().Be(initialReloadId); // ReloadId unchanged
        loader.GetApiSchemaNodes().Should().BeSameAs(initialNodes); // Same schema instance
        loader.ApiSchemaFailures.Should().BeEmpty(); // No failures stored for current valid schema
    }

    [Test]
    public async Task InitialLoad_ThenInvalidUpload_ThenValidUpload_UpdatesSchemaCorrectly()
    {
        // Arrange - Start with valid schema
        A.CallTo(() => _apiSchemaValidator.Validate(A<JsonNode>._))
            .Returns(new List<SchemaValidationFailure>());

        await WriteTestSchemaFile("ApiSchema.json", CreateValidApiSchema("Ed-Fi"));
        var loader = new ApiSchemaProvider(
            NullLogger<ApiSchemaProvider>.Instance,
            _appSettings,
            _apiSchemaValidator
        );

        // Act 1 - Initial load
        var initialNodes = loader.GetApiSchemaNodes();
        var initialReloadId = loader.ReloadId;
        var initialProjectName = initialNodes
            .CoreApiSchemaRootNode["projectSchema"]
            ?["projectName"]?.GetValue<string>();
        initialProjectName.Should().Be("Ed-Fi");

        // Arrange 2 - Configure validator to fail next validation
        A.CallTo(() => _apiSchemaValidator.Validate(A<JsonNode>._))
            .ReturnsNextFromSequence(
                new List<SchemaValidationFailure> { new(new("$.test"), ["Invalid"]) },
                new List<SchemaValidationFailure>() // Valid for third attempt
            );

        // Act 2 - Upload invalid schema
        var invalidSchema = JsonNode.Parse(
            """{"projectSchema": {"isExtensionProject": false, "projectName": "Invalid"}}"""
        );
        var invalidUploadResult = await loader.LoadApiSchemaFromAsync(invalidSchema!, []);

        // Assert 2 - Invalid upload failed
        invalidUploadResult.Success.Should().BeFalse();
        loader.ReloadId.Should().Be(initialReloadId);
        loader
            .GetApiSchemaNodes()
            .CoreApiSchemaRootNode["projectSchema"]
            ?["projectName"]?.GetValue<string>()
            .Should()
            .Be("Ed-Fi"); // Still original

        // Act 3 - Upload valid schema
        var validSchema = JsonNode.Parse(
            """{"projectSchema": {"isExtensionProject": false, "projectName": "Updated"}}"""
        );
        var validUploadResult = await loader.LoadApiSchemaFromAsync(validSchema!, []);

        // Assert 3 - Valid upload succeeded
        validUploadResult.Success.Should().BeTrue();
        loader.IsSchemaValid.Should().BeTrue();
        loader.ReloadId.Should().NotBe(initialReloadId); // New reload ID
        loader
            .GetApiSchemaNodes()
            .CoreApiSchemaRootNode["projectSchema"]
            ?["projectName"]?.GetValue<string>()
            .Should()
            .Be("Updated"); // Schema updated
    }

    [Test]
    public async Task InitialLoadFailure_ThenValidUpload_SetsIsSchemaValidToTrue()
    {
        // Arrange - Configure initial load to fail
        var initialErrors = new List<SchemaValidationFailure> { new(new("$.initial"), ["Initial error"]) };
        A.CallTo(() => _apiSchemaValidator.Validate(A<JsonNode>._)).Returns(initialErrors);

        await WriteTestSchemaFile("ApiSchema.json", CreateValidApiSchema());
        var loader = new ApiSchemaProvider(
            NullLogger<ApiSchemaProvider>.Instance,
            _appSettings,
            _apiSchemaValidator
        );

        // Act 1 - Initial load fails
        Assert.Throws<InvalidOperationException>(() => loader.GetApiSchemaNodes());
        loader.IsSchemaValid.Should().BeFalse();

        // Arrange 2 - Configure next validation to succeed
        A.CallTo(() => _apiSchemaValidator.Validate(A<JsonNode>._))
            .Returns(new List<SchemaValidationFailure>());

        // Act 2 - Upload valid schema
        var validSchema = JsonNode.Parse(
            """{"projectSchema": {"isExtensionProject": false, "projectName": "Uploaded"}}"""
        );
        var uploadResult = await loader.LoadApiSchemaFromAsync(validSchema!, []);

        // Assert - Upload succeeded and IsSchemaValid is now true
        uploadResult.Success.Should().BeTrue();
        loader.IsSchemaValid.Should().BeTrue();
        loader.ApiSchemaFailures.Should().BeEmpty();
        loader
            .GetApiSchemaNodes()
            .CoreApiSchemaRootNode["projectSchema"]
            ?["projectName"]?.GetValue<string>()
            .Should()
            .Be("Uploaded");
    }

    [Test]
    public async Task MultipleFailedReloads_DoNotAffectCurrentValidSchema()
    {
        // Arrange - Start with valid schema
        A.CallTo(() => _apiSchemaValidator.Validate(A<JsonNode>._))
            .Returns(new List<SchemaValidationFailure>()); // Valid for initial load

        await WriteTestSchemaFile("ApiSchema.json", CreateValidApiSchema());
        var loader = new ApiSchemaProvider(
            NullLogger<ApiSchemaProvider>.Instance,
            _appSettings,
            _apiSchemaValidator
        );

        // Act 1 - Initial load
        var initialNodes = loader.GetApiSchemaNodes();
        var initialReloadId = loader.ReloadId;

        // Arrange 2 - All subsequent validations fail
        A.CallTo(() => _apiSchemaValidator.Validate(A<JsonNode>._))
            .Returns(new List<SchemaValidationFailure> { new(new("$.error"), ["Always fails"]) });

        // Act 2 - Multiple failed reload attempts
        var results = new List<bool>();
        for (int i = 0; i < 5; i++)
        {
            // Alternate between reload from file and upload
            bool result;
            if (i % 2 == 0)
            {
                var reloadResult = await loader.ReloadApiSchemaAsync();
                result = reloadResult.Success;
            }
            else
            {
                var schema = JsonNode.Parse(
                    $$"""{ "projectSchema": { "isExtensionProject": false, "attempt": {{i}} } }"""
                );
                var uploadResult = await loader.LoadApiSchemaFromAsync(schema!, []);
                result = uploadResult.Success;
            }
            results.Add(result);
        }

        // Assert - All reloads failed, but schema remains valid and unchanged
        results.Should().AllBeEquivalentTo(false);
        loader.IsSchemaValid.Should().BeTrue();
        loader.ReloadId.Should().Be(initialReloadId);
        loader.GetApiSchemaNodes().Should().BeSameAs(initialNodes);
    }

    [Test]
    public async Task ReloadFromFileSystem_AfterSuccessfulUpload_WorksCorrectly()
    {
        // Arrange - Start with valid schema from file
        A.CallTo(() => _apiSchemaValidator.Validate(A<JsonNode>._))
            .Returns(new List<SchemaValidationFailure>());

        await WriteTestSchemaFile("ApiSchema.json", CreateValidApiSchema("Ed-Fi"));
        var loader = new ApiSchemaProvider(
            NullLogger<ApiSchemaProvider>.Instance,
            _appSettings,
            _apiSchemaValidator
        );

        // Act 1 - Initial load from file system
        var fileSystemNodes = loader.GetApiSchemaNodes();
        fileSystemNodes
            .CoreApiSchemaRootNode["projectSchema"]
            ?["projectName"]?.GetValue<string>()
            .Should()
            .Be("Ed-Fi");

        // Act 2 - Upload new schema
        var uploadedSchema = JsonNode.Parse(
            """{"projectSchema": {"isExtensionProject": false, "projectName": "Uploaded"}}"""
        );
        await loader.LoadApiSchemaFromAsync(uploadedSchema!, []);
        loader
            .GetApiSchemaNodes()
            .CoreApiSchemaRootNode["projectSchema"]
            ?["projectName"]?.GetValue<string>()
            .Should()
            .Be("Uploaded");

        // Act 3 - Reload from file system (should go back to file content)
        await loader.ReloadApiSchemaAsync();

        // Assert - Schema reverted to file system content
        loader
            .GetApiSchemaNodes()
            .CoreApiSchemaRootNode["projectSchema"]
            ?["projectName"]?.GetValue<string>()
            .Should()
            .Be("Ed-Fi");
    }

    private async Task WriteTestSchemaFile(string fileName, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(_testDirectory, fileName), content);
    }

    private static string CreateValidApiSchema(string projectName = "Ed-Fi")
    {
        var schema = new JsonObject
        {
            ["apiSchemaVersion"] = "1.0.0",
            ["projectSchema"] = new JsonObject
            {
                ["projectName"] = projectName,
                ["projectVersion"] = "5.0.0",
                ["description"] = $"Test {projectName} project",
                ["projectEndpointName"] = projectName.ToLower().Replace("-", ""),
                ["isExtensionProject"] = projectName != "Ed-Fi",
                ["abstractResources"] = new JsonObject(),
                ["caseInsensitiveEndpointNameMapping"] = new JsonObject(),
                ["resourceNameMapping"] = new JsonObject(),
                ["resourceSchemas"] = new JsonObject(),
            },
        };

        return schema.ToJsonString();
    }
}
