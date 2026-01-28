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

[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Extra_ResourceKeys
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var projectSchema = EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false));
        var resourceKeys = new[]
        {
            EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "School"),
            EffectiveSchemaFixture.CreateResourceKey(2, "Ed-Fi", "Student"),
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
    public void It_should_fail_with_extra_resource_keys()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Resource keys reference unknown resources");
        _exception.Message.Should().Contain("Ed-Fi:Student");
    }
}

[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Mismatched_Abstract_Resource_Flags
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

        var resourceKeys = new[]
        {
            EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "School"),
            EffectiveSchemaFixture.CreateResourceKey(
                2,
                "Ed-Fi",
                "EducationOrganization",
                isAbstractResource: false
            ),
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
    public void It_should_fail_with_an_abstract_resource_mismatch()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("IsAbstractResource");
        _exception.Message.Should().Contain("Ed-Fi:EducationOrganization");
    }
}

[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Missing_SchemaComponents
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
            schemaComponentsOverride: Array.Empty<SchemaComponentInfo>()
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
    public void It_should_fail_with_missing_schema_components()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("SchemaComponentsInEndpointOrder");
        _exception.Message.Should().Contain("ed-fi");
    }
}

[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Extra_SchemaComponents
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var projectSchema = EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false));
        var resourceKeys = new[] { EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "School") };
        var extraComponents = new[]
        {
            new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.0.0", false),
            new SchemaComponentInfo("tpdm", "TPDM", "1.0.0", true),
        };
        var effectiveSchemaSet = EffectiveSchemaFixture.CreateEffectiveSchemaSet(
            projectSchema,
            resourceKeys,
            schemaComponentsOverride: extraComponents
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
    public void It_should_fail_with_extra_schema_components()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("SchemaComponentsInEndpointOrder");
        _exception.Message.Should().Contain("tpdm");
    }
}

[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Duplicate_SchemaComponents
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var projectSchema = EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false));
        var resourceKeys = new[] { EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "School") };
        var duplicateComponents = new[]
        {
            new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.0.0", false),
            new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.0.0", false),
        };
        var effectiveSchemaSet = EffectiveSchemaFixture.CreateEffectiveSchemaSet(
            projectSchema,
            resourceKeys,
            schemaComponentsOverride: duplicateComponents
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
    public void It_should_fail_with_duplicate_schema_components()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("duplicate");
        _exception.Message.Should().Contain("ed-fi");
    }
}

[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Out_Of_Order_SchemaComponents
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var coreSchema = EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false));
        var extensionSchema = EffectiveSchemaFixture.CreateProjectSchema(("students", "Student", false));
        var projects = new[]
        {
            new EffectiveProjectSchema("ed-fi", "Ed-Fi", "5.0.0", false, coreSchema),
            new EffectiveProjectSchema("tpdm", "TPDM", "1.0.0", true, extensionSchema),
        };

        var resourceKeys = new[]
        {
            new ResourceKeyEntry(1, new QualifiedResourceName("Ed-Fi", "School"), "1.0.0", false),
            new ResourceKeyEntry(2, new QualifiedResourceName("TPDM", "Student"), "1.0.0", false),
        };

        var schemaComponents = new[]
        {
            new SchemaComponentInfo("tpdm", "TPDM", "1.0.0", true),
            new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.0.0", false),
        };

        var effectiveSchemaInfo = new EffectiveSchemaInfo(
            "1.0.0",
            "1.0.0",
            "deadbeef",
            resourceKeys.Length,
            new byte[] { 0x01 },
            schemaComponents,
            resourceKeys
        );

        var effectiveSchemaSet = new EffectiveSchemaSet(effectiveSchemaInfo, projects);
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
    public void It_should_fail_with_out_of_order_schema_components()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("SchemaComponentsInEndpointOrder");
        _exception.Message.Should().Contain("tpdm");
        _exception.Message.Should().Contain("ed-fi");
    }
}

internal static class EffectiveSchemaFixture
{
    public static EffectiveSchemaSet CreateEffectiveSchemaSet(
        JsonObject projectSchema,
        IReadOnlyList<ResourceKeyEntry> resourceKeys,
        int? resourceKeyCountOverride = null,
        IReadOnlyList<SchemaComponentInfo>? schemaComponentsOverride = null
    )
    {
        var schemaComponents =
            schemaComponentsOverride ?? new[] { new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.0.0", false) };

        var effectiveSchemaInfo = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "1.0.0",
            EffectiveSchemaHash: "deadbeef",
            ResourceKeyCount: resourceKeyCountOverride ?? resourceKeys.Count,
            ResourceKeySeedHash: new byte[] { 0x01 },
            SchemaComponentsInEndpointOrder: schemaComponents,
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

    public static ResourceKeyEntry CreateResourceKey(
        short keyId,
        string projectName,
        string resourceName,
        bool isAbstractResource = false
    )
    {
        return new ResourceKeyEntry(
            keyId,
            new QualifiedResourceName(projectName, resourceName),
            "1.0.0",
            isAbstractResource
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
