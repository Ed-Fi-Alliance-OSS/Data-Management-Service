// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for an effective schema info with a resource key count mismatch.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_A_ResourceKeyCount_Mismatch
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with a resource key count mismatch.
    /// </summary>
    [Test]
    public void It_should_fail_with_a_resource_key_count_mismatch()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("ResourceKeyCount");
        _exception.Message.Should().Contain("ResourceKeysInIdOrder");
    }
}

/// <summary>
/// Test fixture for an effective schema info with missing resource keys.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Missing_ResourceKeys
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with a missing resource key.
    /// </summary>
    [Test]
    public void It_should_fail_with_a_missing_resource_key()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Missing resource keys");
        _exception.Message.Should().Contain("Ed-Fi:EducationOrganization");
    }
}

/// <summary>
/// Test fixture for an effective schema info with duplicate resource key ids.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Duplicate_ResourceKeyIds
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with duplicate resource key ids.
    /// </summary>
    [Test]
    public void It_should_fail_with_duplicate_resource_key_ids()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Duplicate ResourceKeyId");
        _exception.Message.Should().Contain("1");
    }
}

/// <summary>
/// Test fixture for an effective schema info with duplicate resource keys.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Duplicate_ResourceKeys
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with duplicate resource keys.
    /// </summary>
    [Test]
    public void It_should_fail_with_duplicate_resource_keys()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Duplicate resource keys");
        _exception.Message.Should().Contain("Ed-Fi:School");
    }
}

/// <summary>
/// Test fixture for an effective schema info with extra resource keys.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Extra_ResourceKeys
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with extra resource keys.
    /// </summary>
    [Test]
    public void It_should_fail_with_extra_resource_keys()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Resource keys reference unknown resources");
        _exception.Message.Should().Contain("Ed-Fi:Student");
    }
}

/// <summary>
/// Test fixture for an effective schema info with mismatched abstract resource flags.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Mismatched_Abstract_Resource_Flags
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with an abstract resource mismatch.
    /// </summary>
    [Test]
    public void It_should_fail_with_an_abstract_resource_mismatch()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("IsAbstractResource");
        _exception.Message.Should().Contain("Ed-Fi:EducationOrganization");
    }
}

/// <summary>
/// Test fixture for an effective schema info with missing schema components.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Missing_SchemaComponents
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with missing schema components.
    /// </summary>
    [Test]
    public void It_should_fail_with_missing_schema_components()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("SchemaComponentsInEndpointOrder");
        _exception.Message.Should().Contain("ed-fi");
    }
}

/// <summary>
/// Test fixture for an effective schema info with extra schema components.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Extra_SchemaComponents
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false));
        var resourceKeys = new[] { EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "School") };
        var extraComponents = new[]
        {
            new SchemaComponentInfo(
                "ed-fi",
                "Ed-Fi",
                "5.0.0",
                false,
                "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
            ),
            new SchemaComponentInfo(
                "tpdm",
                "TPDM",
                "1.0.0",
                true,
                "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
            ),
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

    /// <summary>
    /// It should fail with extra schema components.
    /// </summary>
    [Test]
    public void It_should_fail_with_extra_schema_components()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("SchemaComponentsInEndpointOrder");
        _exception.Message.Should().Contain("tpdm");
    }
}

/// <summary>
/// Test fixture for an effective schema info with duplicate schema components.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Duplicate_SchemaComponents
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false));
        var resourceKeys = new[] { EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "School") };
        var duplicateComponents = new[]
        {
            new SchemaComponentInfo(
                "ed-fi",
                "Ed-Fi",
                "5.0.0",
                false,
                "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
            ),
            new SchemaComponentInfo(
                "ed-fi",
                "Ed-Fi",
                "5.0.0",
                false,
                "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
            ),
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

    /// <summary>
    /// It should fail with duplicate schema components.
    /// </summary>
    [Test]
    public void It_should_fail_with_duplicate_schema_components()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("duplicate");
        _exception.Message.Should().Contain("ed-fi");
    }
}

/// <summary>
/// Test fixture for an effective schema info with out of order schema components.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaInfo_With_Out_Of_Order_SchemaComponents
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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
            new SchemaComponentInfo(
                "tpdm",
                "TPDM",
                "1.0.0",
                true,
                "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
            ),
            new SchemaComponentInfo(
                "ed-fi",
                "Ed-Fi",
                "5.0.0",
                false,
                "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
            ),
        };

        var effectiveSchemaInfo = new EffectiveSchemaInfo(
            "1.0.0",
            "1.0.0",
            "edf1edf1",
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

    /// <summary>
    /// It should fail with out of order schema components.
    /// </summary>
    [Test]
    public void It_should_fail_with_out_of_order_schema_components()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("SchemaComponentsInEndpointOrder");
        _exception.Message.Should().Contain("tpdm");
        _exception.Message.Should().Contain("ed-fi");
    }
}

/// <summary>
/// Test fixture for an effective schema set with a subclass resource missing jsonSchemaForInsert.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaSet_With_A_Subclass_Resource_Missing_JsonSchemaForInsert
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false));
        var resourceSchemas = (JsonObject)projectSchema["resourceSchemas"]!;
        var schoolSchema = (JsonObject)resourceSchemas["schools"]!;
        schoolSchema["isSubclass"] = true;

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

    /// <summary>
    /// It should fail fast when subclass json schema is missing.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_subclass_json_schema_for_insert_is_missing()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Subclass resource");
        _exception.Message.Should().Contain("Ed-Fi:School");
        _exception.Message.Should().Contain("jsonSchemaForInsert");
    }
}

/// <summary>
/// Test fixture for an effective schema set with a subclass mapping that declares multiple identity paths.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaSet_With_A_Subclass_SuperclassIdentityJsonPath_And_Multiple_IdentityJsonPaths
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false));
        EffectiveSchemaFixture.AddAbstractResources(
            projectSchema,
            ("educationOrganizations", "EducationOrganization")
        );

        var resourceSchemas = (JsonObject)projectSchema["resourceSchemas"]!;
        var schoolSchema = (JsonObject)resourceSchemas["schools"]!;

        schoolSchema["isSubclass"] = true;
        schoolSchema["superclassProjectName"] = "Ed-Fi";
        schoolSchema["superclassResourceName"] = "EducationOrganization";
        schoolSchema["superclassIdentityJsonPath"] = "$.educationOrganizationId";
        schoolSchema["identityJsonPaths"] = new JsonArray { "$.schoolId", "$.educationOrganizationId" };
        schoolSchema["jsonSchemaForInsert"] = new JsonObject();

        var abstractResources = (JsonObject)projectSchema["abstractResources"]!;
        var educationOrganizationSchema = (JsonObject)abstractResources["educationOrganizations"]!;
        educationOrganizationSchema["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" };

        var resourceKeys = new[]
        {
            EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "School"),
            EffectiveSchemaFixture.CreateResourceKey(
                2,
                "Ed-Fi",
                "EducationOrganization",
                isAbstractResource: true
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

    /// <summary>
    /// It should fail fast when subclass mapping does not declare exactly one identity path.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_subclass_mapping_does_not_declare_exactly_one_identity_path()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("superclassIdentityJsonPath");
        _exception.Message.Should().Contain("exactly one identityJsonPaths entry");
        _exception.Message.Should().Contain("Ed-Fi:School");
    }
}

/// <summary>
/// Test fixture for an effective schema set with a mismatched superclass identity path mapping.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaSet_With_A_Subclass_SuperclassIdentityJsonPath_That_Does_Not_Match_Abstract_Identity
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false));
        EffectiveSchemaFixture.AddAbstractResources(
            projectSchema,
            ("educationOrganizations", "EducationOrganization")
        );

        var resourceSchemas = (JsonObject)projectSchema["resourceSchemas"]!;
        var schoolSchema = (JsonObject)resourceSchemas["schools"]!;

        schoolSchema["isSubclass"] = true;
        schoolSchema["superclassProjectName"] = "Ed-Fi";
        schoolSchema["superclassResourceName"] = "EducationOrganization";
        schoolSchema["superclassIdentityJsonPath"] = "$.districtId";
        schoolSchema["identityJsonPaths"] = new JsonArray { "$.schoolId" };
        schoolSchema["jsonSchemaForInsert"] = new JsonObject();

        var abstractResources = (JsonObject)projectSchema["abstractResources"]!;
        var educationOrganizationSchema = (JsonObject)abstractResources["educationOrganizations"]!;
        educationOrganizationSchema["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" };

        var resourceKeys = new[]
        {
            EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "School"),
            EffectiveSchemaFixture.CreateResourceKey(
                2,
                "Ed-Fi",
                "EducationOrganization",
                isAbstractResource: true
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

    /// <summary>
    /// It should fail fast when superclassIdentityJsonPath differs from abstract identity contract.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_superclass_identity_json_path_differs_from_abstract_identity_contract()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("superclassIdentityJsonPath");
        _exception.Message.Should().Contain("$.districtId");
        _exception.Message.Should().Contain("$.educationOrganizationId");
        _exception.Message.Should().Contain("Ed-Fi:EducationOrganization");
    }
}

/// <summary>
/// Test type effective schema fixture.
/// </summary>
internal static class EffectiveSchemaFixture
{
    /// <summary>
    /// Create effective schema set.
    /// </summary>
    public static EffectiveSchemaSet CreateEffectiveSchemaSet(
        JsonObject projectSchema,
        IReadOnlyList<ResourceKeyEntry> resourceKeys,
        int? resourceKeyCountOverride = null,
        IReadOnlyList<SchemaComponentInfo>? schemaComponentsOverride = null
    )
    {
        var schemaComponents =
            schemaComponentsOverride
            ?? new[]
            {
                new SchemaComponentInfo(
                    "ed-fi",
                    "Ed-Fi",
                    "5.0.0",
                    false,
                    "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                ),
            };

        var effectiveSchemaInfo = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "1.0.0",
            EffectiveSchemaHash: "edf1edf1",
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

    /// <summary>
    /// Create resource key.
    /// </summary>
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

    /// <summary>
    /// Create project schema.
    /// </summary>
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
                ["documentPathsMapping"] = new JsonObject(),
            };
        }

        return new JsonObject { ["resourceSchemas"] = resourceSchemas };
    }

    /// <summary>
    /// Add abstract resources.
    /// </summary>
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
                ["documentPathsMapping"] = new JsonObject(),
            };
        }

        projectSchema["abstractResources"] = abstractResourceSchemas;
    }
}
