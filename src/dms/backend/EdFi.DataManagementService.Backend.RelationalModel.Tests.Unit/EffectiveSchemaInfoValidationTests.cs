// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_A_ResourceKeyCount_Mismatch
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var projectSchema = EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false));
        var resourceKeys = new[] { EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "School") };
        var effectiveSchemaSet = EffectiveSchemaFixture.CreateEffectiveSchemaSet(
            projectSchema,
            resourceKeys,
            resourceKeyCountOverride: 2
        );

        var builder = new DerivedRelationalModelSetBuilder(Array.Empty<IRelationalModelSetPass>());

        try
        {
            builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_should_fail_with_a_resource_key_count_mismatch()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("ResourceKeyCount");
        _exception.Message.Should().Contain("ResourceKeysInIdOrder");
    }
}

[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Missing_ResourceKeys
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var projectSchema = EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false));
        EffectiveSchemaFixture.AddAbstractResources(
            projectSchema,
            ("educationOrganizations", "EducationOrganization")
        );
        var resourceKeys = new[] { EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "School") };
        var effectiveSchemaSet = EffectiveSchemaFixture.CreateEffectiveSchemaSet(projectSchema, resourceKeys);

        var builder = new DerivedRelationalModelSetBuilder(Array.Empty<IRelationalModelSetPass>());

        try
        {
            builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_should_fail_with_a_missing_resource_key()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Missing resource keys");
        _exception.Message.Should().Contain("Ed-Fi:EducationOrganization");
    }
}

[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Duplicate_ResourceKeyIds
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var projectSchema = EffectiveSchemaFixture.CreateProjectSchema(
            ("schools", "School", false),
            ("students", "Student", false)
        );
        var resourceKeys = new[]
        {
            EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "School"),
            EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "Student"),
        };
        var effectiveSchemaSet = EffectiveSchemaFixture.CreateEffectiveSchemaSet(projectSchema, resourceKeys);

        var builder = new DerivedRelationalModelSetBuilder(Array.Empty<IRelationalModelSetPass>());

        try
        {
            builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_should_fail_with_duplicate_resource_key_ids()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Duplicate ResourceKeyId");
        _exception.Message.Should().Contain("1");
    }
}

[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Duplicate_ResourceKeys
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var projectSchema = EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", true));
        var resourceKeys = new[]
        {
            EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "School"),
            EffectiveSchemaFixture.CreateResourceKey(2, "Ed-Fi", "School"),
        };
        var effectiveSchemaSet = EffectiveSchemaFixture.CreateEffectiveSchemaSet(projectSchema, resourceKeys);

        var builder = new DerivedRelationalModelSetBuilder(Array.Empty<IRelationalModelSetPass>());

        try
        {
            builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_should_fail_with_duplicate_resource_keys()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Duplicate resource keys");
        _exception.Message.Should().Contain("Ed-Fi:School");
    }
}

internal static class EffectiveSchemaFixture
{
    public static EffectiveSchemaSet CreateEffectiveSchemaSet(
        JsonObject projectSchema,
        IReadOnlyList<ResourceKeyEntry> resourceKeys,
        int? resourceKeyCountOverride = null
    )
    {
        var effectiveSchemaInfo = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "1.0.0",
            EffectiveSchemaHash: "deadbeef",
            ResourceKeyCount: resourceKeyCountOverride ?? resourceKeys.Count,
            ResourceKeySeedHash: new byte[] { 0x01 },
            SchemaComponentsInEndpointOrder: new[]
            {
                new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.0.0", false),
            },
            ResourceKeysInIdOrder: resourceKeys
        );

        var project = new EffectiveProjectSchema(
            ProjectEndpointName: "ed-fi",
            ProjectName: "Ed-Fi",
            ProjectVersion: "5.0.0",
            IsExtensionProject: false,
            ProjectSchema: projectSchema
        );

        return new EffectiveSchemaSet(effectiveSchemaInfo, new[] { project });
    }

    public static ResourceKeyEntry CreateResourceKey(short keyId, string projectName, string resourceName)
    {
        return new ResourceKeyEntry(
            keyId,
            new QualifiedResourceName(projectName, resourceName),
            "1.0.0",
            false
        );
    }

    public static JsonObject CreateProjectSchema(
        params (string EndpointName, string ResourceName, bool IsResourceExtension)[] resources
    )
    {
        JsonObject resourceSchemas = new();

        foreach (var resource in resources)
        {
            resourceSchemas[resource.EndpointName] = new JsonObject
            {
                ["resourceName"] = resource.ResourceName,
                ["isResourceExtension"] = resource.IsResourceExtension,
            };
        }

        return new JsonObject { ["resourceSchemas"] = resourceSchemas };
    }

    public static void AddAbstractResources(
        JsonObject projectSchema,
        params (string EndpointName, string ResourceName)[] abstractResources
    )
    {
        JsonObject abstractResourceSchemas = new();

        foreach (var resource in abstractResources)
        {
            abstractResourceSchemas[resource.EndpointName] = new JsonObject
            {
                ["resourceName"] = resource.ResourceName,
            };
        }

        projectSchema["abstractResources"] = abstractResourceSchemas;
    }
}
