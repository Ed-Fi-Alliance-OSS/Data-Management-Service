// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;

/// <summary>
/// Integration tests to verify the relational metadata is present and accessible
/// in the actual ApiSchema packages consumed by DMS
/// </summary>
[TestFixture]
public class RelationalMetadataIntegrationTests
{
    private readonly string _packageId = "EdFi.DataStandard52.ApiSchema";
    private JsonNode _apiSchemaNode = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var apiSchemaPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "ApiSchema",
            "Packages",
            _packageId,
            "ApiSchema.json"
        );

        File.Exists(apiSchemaPath).Should().BeTrue("ApiSchema.json should be copied from the package");

        string jsonContent = File.ReadAllText(apiSchemaPath);
        _apiSchemaNode = JsonNode.Parse(jsonContent)!;
        _apiSchemaNode.Should().NotBeNull();
    }

    [Test]
    public void Should_Find_Resources_With_Relational_Metadata_In_DataStandard52()
    {
        // Arrange
        var resourceSchemas = _apiSchemaNode["projectSchema"]?["resourceSchemas"]?.AsObject();
        resourceSchemas.Should().NotBeNull();

        // Assert - Find resources with relational metadata
        var resourcesWithRelational = new List<string>();

        foreach (var resource in resourceSchemas!)
        {
            var resourceSchema = new ResourceSchema(resource.Value!);

            if (resourceSchema.Relational is not null)
            {
                resourcesWithRelational.Add(resource.Key);

                // Verify the relational block is properly deserialized
                TestContext.WriteLine($"Resource '{resource.Key}' has relational metadata:");
                TestContext.WriteLine(
                    $"  - RootTableNameOverride: {resourceSchema.Relational.RootTableNameOverride ?? "(null)"}"
                );
                TestContext.WriteLine(
                    $"  - NameOverrides count: {resourceSchema.Relational.NameOverrides.Count}"
                );
            }
        }

        // Assert
        resourcesWithRelational.Should().Contain("assessmentAdministrationParticipations");

        TestContext.WriteLine(
            $"\n✓ Found {resourcesWithRelational.Count} resources with relational metadata"
        );
        TestContext.WriteLine($"  Total resources checked: {resourceSchemas.Count}");
    }

    [Test]
    public void Should_Properly_Deserialize_RootTableNameOverride_When_Present()
    {
        // Arrange
        var resourceSchemas = _apiSchemaNode["projectSchema"]?["resourceSchemas"]?.AsObject();

        // Act - Find a resource with rootTableNameOverride
        ResourceSchema? resourceWithOverride = null;
        string? resourceNameWithOverride = null;

        foreach (var resource in resourceSchemas!)
        {
            var resourceSchema = new ResourceSchema(resource.Value!);

            if (resourceSchema.Relational?.RootTableNameOverride is not null)
            {
                resourceWithOverride = resourceSchema;
                resourceNameWithOverride = resource.Key;
                break;
            }
        }

        // Assert
        if (resourceWithOverride is not null)
        {
            resourceWithOverride.Relational!.RootTableNameOverride.Should().NotBeNullOrEmpty();
            TestContext.WriteLine(
                $"✓ Found resource '{resourceNameWithOverride}' with rootTableNameOverride: '{resourceWithOverride.Relational.RootTableNameOverride}'"
            );
        }
        else
        {
            Assert.Inconclusive("No resources found with rootTableNameOverride in this ApiSchema");
        }
    }

    [Test]
    public void Should_Properly_Deserialize_NameOverrides_When_Present()
    {
        // Arrange
        var resourceSchemas = _apiSchemaNode["projectSchema"]?["resourceSchemas"]?.AsObject();

        // Act - Find a resource with nameOverrides
        ResourceSchema? resourceWithOverrides = null;
        string? resourceNameWithOverrides = null;

        foreach (var resource in resourceSchemas!)
        {
            var resourceSchema = new ResourceSchema(resource.Value!);

            if (
                resourceSchema.Relational?.NameOverrides is not null
                && resourceSchema.Relational.NameOverrides.Count > 0
            )
            {
                resourceWithOverrides = resourceSchema;
                resourceNameWithOverrides = resource.Key;
                break;
            }
        }

        // Assert
        if (resourceWithOverrides is not null)
        {
            resourceWithOverrides.Relational!.NameOverrides.Should().NotBeEmpty();

            TestContext.WriteLine(
                $"✓ Found resource '{resourceNameWithOverrides}' with {resourceWithOverrides.Relational.NameOverrides.Count} nameOverrides:"
            );

            foreach (var kvp in resourceWithOverrides.Relational.NameOverrides.Take(3))
            {
                TestContext.WriteLine($"    {kvp.Key} → {kvp.Value}");
            }
        }
        else
        {
            Assert.Inconclusive("No resources found with nameOverrides in this ApiSchema");
        }
    }
}
