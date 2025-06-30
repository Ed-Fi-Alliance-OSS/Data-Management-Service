// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Tests.E2E.Builders;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace EdFi.DataManagementService.Tests.Integration;

[TestFixture]
public class HotReloadIntegrationTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _tempSchemaDirectory = null!;

    [SetUp]
    public void Setup()
    {
        _tempSchemaDirectory = Path.Combine(Path.GetTempPath(), $"dms-test-schemas-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempSchemaDirectory);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(
                (context, config) =>
                {
                    config.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:EnableManagementEndpoints"] = "true",
                            ["ApiSchema:Source"] = "Directory",
                            ["ApiSchema:DirectoryPath"] = _tempSchemaDirectory,
                        }
                    );
                }
            );

            builder.UseEnvironment("Test");
        });

        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();

        if (Directory.Exists(_tempSchemaDirectory))
        {
            Directory.Delete(_tempSchemaDirectory, true);
        }
    }

    [Test]
    public async Task HotReload_UpdatedSchema_ReflectedInRequests()
    {
        // Arrange - Create initial schema with Student resource
        var initialSchema = new ApiSchemaBuilder()
            .WithStartProject("Ed-Fi", "5.0.0")
            .WithStartResource("Student")
            .WithIdentityJsonPaths("$.studentUniqueId")
            .WithSimpleJsonSchema(
                ("studentUniqueId", "string"),
                ("firstName", "string"),
                ("lastName", "string")
            )
            .WithEndResource()
            .WithEndProject();

        await WriteSchemaToDirectory(initialSchema);

        // Act 1 - Verify initial schema is loaded
        var discoveryResponse = await _client.GetAsync("/");
        discoveryResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var discoveryContent = await discoveryResponse.Content.ReadAsStringAsync();
        discoveryContent.Should().Contain("students");

        // Act 2 - Update schema to add Teacher resource
        var updatedSchema = new ApiSchemaBuilder()
            .WithStartProject("Ed-Fi", "5.0.0")
            .WithStartResource("Student")
            .WithIdentityJsonPaths("$.studentUniqueId")
            .WithSimpleJsonSchema(
                ("studentUniqueId", "string"),
                ("firstName", "string"),
                ("lastName", "string")
            )
            .WithEndResource()
            .WithStartResource("Teacher")
            .WithIdentityJsonPaths("$.teacherUniqueId")
            .WithSimpleJsonSchema(
                ("teacherUniqueId", "string"),
                ("firstName", "string"),
                ("lastName", "string")
            )
            .WithEndResource()
            .WithEndProject();

        await WriteSchemaToDirectory(updatedSchema);

        // Act 3 - Trigger hot reload
        var reloadResponse = await _client.PostAsync("/management/reload-api-schema", null);
        reloadResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act 4 - Verify updated schema is reflected
        discoveryResponse = await _client.GetAsync("/");
        discoveryContent = await discoveryResponse.Content.ReadAsStringAsync();

        // Assert
        discoveryContent.Should().Contain("students");
        discoveryContent.Should().Contain("teachers");
    }

    [Test]
    public async Task HotReload_ConcurrentRequests_NoErrors()
    {
        // Arrange - Create schema
        var schema = new ApiSchemaBuilder()
            .WithStartProject("Ed-Fi", "5.0.0")
            .WithStartResource("School")
            .WithIdentityJsonPaths("$.schoolId")
            .WithSimpleJsonSchema(("schoolId", "string"), ("nameOfInstitution", "string"))
            .WithEndResource()
            .WithEndProject();

        await WriteSchemaToDirectory(schema);

        // Create test data
        var schoolData = JsonSerializer.Serialize(
            new { schoolId = "12345", nameOfInstitution = "Test School" }
        );

        // Act - Send concurrent requests while reloading
        var tasks = new List<Task<HttpResponseMessage>>();

        // Start requests
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(
                _client.PostAsync(
                    "/ed-fi/schools",
                    new StringContent(schoolData, Encoding.UTF8, "application/json")
                )
            );
        }

        // Trigger reload in the middle
        tasks.Add(_client.PostAsync("/management/reload-api-schema", null));

        // More requests after reload
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(
                _client.PostAsync(
                    "/ed-fi/schools",
                    new StringContent(schoolData, Encoding.UTF8, "application/json")
                )
            );
        }

        var responses = await Task.WhenAll(tasks);

        // Assert - All requests should complete without errors
        responses.Should().HaveCount(41); // 40 POST requests + 1 reload
        responses
            .Where(r => r.RequestMessage?.RequestUri?.ToString().Contains("reload-api-schema") ?? false)
            .Should()
            .OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        responses
            .Where(r => !(r.RequestMessage?.RequestUri?.ToString().Contains("reload-api-schema") ?? false))
            .Should()
            .OnlyContain(r =>
                r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.BadRequest
            ); // Duplicate key errors are ok
    }

    [Test]
    public async Task HotReload_MultipleReloads_Successive()
    {
        // Arrange - Create schemas with different versions
        var schemas = new[]
        {
            new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.0.0")
                .WithStartResource("Course")
                .WithIdentityJsonPaths("$.courseCode")
                .WithSimpleJsonSchema(("courseCode", "string"), ("courseTitle", "string"))
                .WithEndResource()
                .WithEndProject(),
            new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.1.0")
                .WithStartResource("Course")
                .WithIdentityJsonPaths("$.courseCode")
                .WithSimpleJsonSchema(
                    ("courseCode", "string"),
                    ("courseTitle", "string"),
                    ("numberOfParts", "integer")
                )
                .WithEndResource()
                .WithEndProject(),
            new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.2.0")
                .WithStartResource("Course")
                .WithIdentityJsonPaths("$.courseCode")
                .WithSimpleJsonSchema(
                    ("courseCode", "string"),
                    ("courseTitle", "string"),
                    ("numberOfParts", "integer"),
                    ("maximumAvailableCredits", "number")
                )
                .WithEndResource()
                .WithEndProject(),
        };

        // Act & Assert - Apply each schema and verify
        foreach (var (schema, index) in schemas.Select((s, i) => (s, i)))
        {
            await WriteSchemaToDirectory(schema);

            var reloadResponse = await _client.PostAsync("/management/reload-api-schema", null);
            reloadResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            // Get OpenAPI spec to verify schema version
            var openApiResponse = await _client.GetAsync("/api/openapi");
            openApiResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var openApiContent = await openApiResponse.Content.ReadAsStringAsync();
            var openApiJson = JsonNode.Parse(openApiContent);

            var version = $"5.{index}.0";
            openApiJson?["info"]?["version"]?.GetValue<string>().Should().Contain(version);
        }
    }

    [Test]
    public async Task HotReload_InvalidSchema_RollbackBehavior()
    {
        // Arrange - Create valid initial schema
        var validSchema = new ApiSchemaBuilder()
            .WithStartProject("Ed-Fi", "5.0.0")
            .WithStartResource("Section")
            .WithIdentityJsonPaths("$.sectionIdentifier")
            .WithSimpleJsonSchema(("sectionIdentifier", "string"))
            .WithEndResource()
            .WithEndProject();

        await WriteSchemaToDirectory(validSchema);

        // Verify initial schema works
        var initialResponse = await _client.GetAsync("/ed-fi/sections");
        initialResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act - Write invalid schema
        var invalidSchemaPath = Path.Combine(_tempSchemaDirectory, "ApiSchema.json");
        await File.WriteAllTextAsync(invalidSchemaPath, "{ invalid json");

        // Try to reload
        var reloadResponse = await _client.PostAsync("/management/reload-api-schema", null);
        reloadResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        // Assert - Original schema should still work
        var afterFailedReloadResponse = await _client.GetAsync("/ed-fi/sections");
        afterFailedReloadResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task HotReload_SchemaValidationErrors_HandledGracefully()
    {
        // Arrange - Create schema with missing required fields
        var incompleteSchemaJson = JsonNode.Parse(
            """
            {
                "apiSchemaVersion": "1.0.0",
                "projectSchema": {
                    "projectName": "Ed-Fi",
                    "resourceSchemas": {
                        "invalidResources": {
                            "resourceName": "InvalidResource"
                        }
                    }
                }
            }
            """
        );

        var schemaPath = Path.Combine(_tempSchemaDirectory, "ApiSchema.json");
        await File.WriteAllTextAsync(schemaPath, incompleteSchemaJson!.ToJsonString());

        // Act - Try to reload
        var reloadResponse = await _client.PostAsync("/management/reload-api-schema", null);

        // Assert - Should handle gracefully
        reloadResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var responseContent = await reloadResponse.Content.ReadAsStringAsync();
        responseContent.Should().Contain("Schema Reload Failed");
    }

    private async Task WriteSchemaToDirectory(ApiSchemaBuilder builder)
    {
        builder.WithEndProject(); // Ensure project is ended
        await builder.WriteApiSchemasToDirectoryAsync(_tempSchemaDirectory);
    }
}
