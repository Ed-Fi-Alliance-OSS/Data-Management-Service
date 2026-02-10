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
/// Test fixture for an extension project key matching project endpoint name.
/// </summary>
[TestFixture]
public class Given_An_Extension_Project_Key_Matching_Project_Endpoint_Name
{
    private ProjectSchemaInfo _project = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projects = new[]
        {
            new EffectiveProjectSchema(
                "ed-fi",
                "Ed-Fi",
                "5.0.0",
                false,
                EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false))
            ),
            new EffectiveProjectSchema(
                "sample-ext",
                "Sample",
                "1.0.0",
                true,
                EffectiveSchemaFixture.CreateProjectSchema(("schoolExtensions", "SchoolExtension", true))
            ),
        };

        var resourceKeys = new[]
        {
            new ResourceKeyEntry(1, new QualifiedResourceName("Ed-Fi", "School"), "1.0.0", false),
            new ResourceKeyEntry(2, new QualifiedResourceName("Sample", "SchoolExtension"), "1.0.0", false),
        };

        var context = ExtensionProjectKeyFixture.CreateContext(projects, resourceKeys);
        var extensionSite = ExtensionProjectKeyFixture.CreateExtensionSite("sample-ext");

        _project = context.ResolveExtensionProjectKey(
            "sample-ext",
            extensionSite,
            new QualifiedResourceName("Ed-Fi", "School")
        );
    }

    /// <summary>
    /// It should resolve to the matching endpoint name.
    /// </summary>
    [Test]
    public void It_should_resolve_to_the_matching_endpoint_name()
    {
        _project.ProjectEndpointName.Should().Be("sample-ext");
    }
}

/// <summary>
/// Test fixture for an extension project key matching endpoint name with mixed casing.
/// </summary>
[TestFixture]
public class Given_An_Extension_Project_Key_Matching_Project_Endpoint_Name_With_Mixed_Casing
{
    private ProjectSchemaInfo _project = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projects = new[]
        {
            new EffectiveProjectSchema(
                "ed-fi",
                "Ed-Fi",
                "5.0.0",
                false,
                EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false))
            ),
            new EffectiveProjectSchema(
                "sample-ext",
                "Sample",
                "1.0.0",
                true,
                EffectiveSchemaFixture.CreateProjectSchema(("schoolExtensions", "SchoolExtension", true))
            ),
        };

        var resourceKeys = new[]
        {
            new ResourceKeyEntry(1, new QualifiedResourceName("Ed-Fi", "School"), "1.0.0", false),
            new ResourceKeyEntry(2, new QualifiedResourceName("Sample", "SchoolExtension"), "1.0.0", false),
        };

        var context = ExtensionProjectKeyFixture.CreateContext(projects, resourceKeys);
        var extensionSite = ExtensionProjectKeyFixture.CreateExtensionSite("SaMpLe-ExT");

        _project = context.ResolveExtensionProjectKey(
            "SaMpLe-ExT",
            extensionSite,
            new QualifiedResourceName("Ed-Fi", "School")
        );
    }

    /// <summary>
    /// It should resolve to the matching endpoint name ignoring key casing.
    /// </summary>
    [Test]
    public void It_should_resolve_to_the_matching_endpoint_name_ignoring_key_casing()
    {
        _project.ProjectEndpointName.Should().Be("sample-ext");
    }
}

/// <summary>
/// Test fixture for deterministic extension project key resolution across casing variants.
/// </summary>
[TestFixture]
public class Given_An_Extension_Project_Key_Resolved_With_Multiple_Casing_Variants
{
    private ProjectSchemaInfo _lowerCaseProject = default!;
    private ProjectSchemaInfo _mixedCaseProject = default!;
    private ProjectSchemaInfo _upperCaseProject = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projects = new[]
        {
            new EffectiveProjectSchema(
                "ed-fi",
                "Ed-Fi",
                "5.0.0",
                false,
                EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false))
            ),
            new EffectiveProjectSchema(
                "sample-ext",
                "Sample",
                "1.0.0",
                true,
                EffectiveSchemaFixture.CreateProjectSchema(("schoolExtensions", "SchoolExtension", true))
            ),
        };

        var resourceKeys = new[]
        {
            new ResourceKeyEntry(1, new QualifiedResourceName("Ed-Fi", "School"), "1.0.0", false),
            new ResourceKeyEntry(2, new QualifiedResourceName("Sample", "SchoolExtension"), "1.0.0", false),
        };

        var context = ExtensionProjectKeyFixture.CreateContext(projects, resourceKeys);
        var resource = new QualifiedResourceName("Ed-Fi", "School");

        _lowerCaseProject = context.ResolveExtensionProjectKey(
            "sample-ext",
            ExtensionProjectKeyFixture.CreateExtensionSite("sample-ext"),
            resource
        );

        _mixedCaseProject = context.ResolveExtensionProjectKey(
            "SaMpLe-ExT",
            ExtensionProjectKeyFixture.CreateExtensionSite("SaMpLe-ExT"),
            resource
        );

        _upperCaseProject = context.ResolveExtensionProjectKey(
            "SAMPLE-EXT",
            ExtensionProjectKeyFixture.CreateExtensionSite("SAMPLE-EXT"),
            resource
        );
    }

    /// <summary>
    /// It should resolve to the same endpoint for all casing variants.
    /// </summary>
    [Test]
    public void It_should_resolve_to_the_same_endpoint_for_all_casing_variants()
    {
        _lowerCaseProject.ProjectEndpointName.Should().Be("sample-ext");
        _mixedCaseProject.ProjectEndpointName.Should().Be("sample-ext");
        _upperCaseProject.ProjectEndpointName.Should().Be("sample-ext");
    }

    /// <summary>
    /// It should resolve deterministically regardless of key casing.
    /// </summary>
    [Test]
    public void It_should_resolve_deterministically_regardless_of_key_casing()
    {
        _mixedCaseProject.ProjectName.Should().Be(_lowerCaseProject.ProjectName);
        _upperCaseProject.ProjectName.Should().Be(_lowerCaseProject.ProjectName);
        _mixedCaseProject.ProjectEndpointName.Should().Be(_lowerCaseProject.ProjectEndpointName);
        _upperCaseProject.ProjectEndpointName.Should().Be(_lowerCaseProject.ProjectEndpointName);
    }
}

/// <summary>
/// Test fixture for a mixed-case extension project key that does not match any configured project.
/// </summary>
[TestFixture]
public class Given_A_Mixed_Case_Extension_Project_Key_That_Does_Not_Match_Any_Project
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projects = new[]
        {
            new EffectiveProjectSchema(
                "ed-fi",
                "Ed-Fi",
                "5.0.0",
                false,
                EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false))
            ),
            new EffectiveProjectSchema(
                "sample-ext",
                "Sample",
                "1.0.0",
                true,
                EffectiveSchemaFixture.CreateProjectSchema(("schoolExtensions", "SchoolExtension", true))
            ),
        };

        var resourceKeys = new[]
        {
            new ResourceKeyEntry(1, new QualifiedResourceName("Ed-Fi", "School"), "1.0.0", false),
            new ResourceKeyEntry(2, new QualifiedResourceName("Sample", "SchoolExtension"), "1.0.0", false),
        };

        var context = ExtensionProjectKeyFixture.CreateContext(projects, resourceKeys);
        var extensionSite = ExtensionProjectKeyFixture.CreateExtensionSite("UnKnOwN-ExT");

        try
        {
            context.ResolveExtensionProjectKey(
                "UnKnOwN-ExT",
                extensionSite,
                new QualifiedResourceName("Ed-Fi", "School")
            );
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail when mixed-case project key does not match any configured project.
    /// </summary>
    [Test]
    public void It_should_fail_when_mixed_case_project_key_does_not_match_any_configured_project()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("UnKnOwN-ExT");
        _exception.Message.Should().Contain("does not match any configured project");
        _exception.Message.Should().Contain("resource 'Ed-Fi:School'");
        _exception.Message.Should().Contain("owning scope '$'");
        _exception.Message.Should().Contain("extension path '$._ext'");
    }
}

/// <summary>
/// Test fixture for an extension project key matching project name.
/// </summary>
[TestFixture]
public class Given_An_Extension_Project_Key_Matching_Project_Name
{
    private ProjectSchemaInfo _project = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projects = new[]
        {
            new EffectiveProjectSchema(
                "ed-fi",
                "Ed-Fi",
                "5.0.0",
                false,
                EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false))
            ),
            new EffectiveProjectSchema(
                "sample-ext",
                "Sample",
                "1.0.0",
                true,
                EffectiveSchemaFixture.CreateProjectSchema(("schoolExtensions", "SchoolExtension", true))
            ),
        };

        var resourceKeys = new[]
        {
            new ResourceKeyEntry(1, new QualifiedResourceName("Ed-Fi", "School"), "1.0.0", false),
            new ResourceKeyEntry(2, new QualifiedResourceName("Sample", "SchoolExtension"), "1.0.0", false),
        };

        var context = ExtensionProjectKeyFixture.CreateContext(projects, resourceKeys);
        var extensionSite = ExtensionProjectKeyFixture.CreateExtensionSite("Sample");

        _project = context.ResolveExtensionProjectKey(
            "Sample",
            extensionSite,
            new QualifiedResourceName("Ed-Fi", "School")
        );
    }

    /// <summary>
    /// It should resolve to the matching project name when no endpoint match exists.
    /// </summary>
    [Test]
    public void It_should_resolve_to_the_matching_project_name_when_no_endpoint_match_exists()
    {
        _project.ProjectEndpointName.Should().Be("sample-ext");
    }
}

/// <summary>
/// Test fixture for an extension project key matching endpoint and project names.
/// </summary>
[TestFixture]
public class Given_An_Extension_Project_Key_Matching_Endpoint_And_Project_Names
{
    private ProjectSchemaInfo _project = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projects = new[]
        {
            new EffectiveProjectSchema(
                "ed-fi",
                "Ed-Fi",
                "5.0.0",
                false,
                EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false))
            ),
            new EffectiveProjectSchema(
                "alpha",
                "AlphaOne",
                "1.0.0",
                true,
                EffectiveSchemaFixture.CreateProjectSchema(("alphaExtensions", "AlphaExtension", true))
            ),
            new EffectiveProjectSchema(
                "beta",
                "Alpha",
                "1.0.0",
                true,
                EffectiveSchemaFixture.CreateProjectSchema(("betaExtensions", "BetaExtension", true))
            ),
        };

        var resourceKeys = new[]
        {
            new ResourceKeyEntry(1, new QualifiedResourceName("Ed-Fi", "School"), "1.0.0", false),
            new ResourceKeyEntry(2, new QualifiedResourceName("AlphaOne", "AlphaExtension"), "1.0.0", false),
            new ResourceKeyEntry(3, new QualifiedResourceName("Alpha", "BetaExtension"), "1.0.0", false),
        };

        var context = ExtensionProjectKeyFixture.CreateContext(projects, resourceKeys);
        var extensionSite = ExtensionProjectKeyFixture.CreateExtensionSite("alpha");

        _project = context.ResolveExtensionProjectKey(
            "alpha",
            extensionSite,
            new QualifiedResourceName("Ed-Fi", "School")
        );
    }

    /// <summary>
    /// It should prefer endpoint name matches over project name fallbacks.
    /// </summary>
    [Test]
    public void It_should_prefer_endpoint_name_matches_over_project_name_fallbacks()
    {
        _project.ProjectEndpointName.Should().Be("alpha");
    }
}

/// <summary>
/// Test fixture for an extension project key matching a non extension project.
/// </summary>
[TestFixture]
public class Given_An_Extension_Project_Key_Matching_A_Non_Extension_Project
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projects = new[]
        {
            new EffectiveProjectSchema(
                "core",
                "Core",
                "5.0.0",
                false,
                EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false))
            ),
            new EffectiveProjectSchema(
                "sample-ext",
                "Sample",
                "1.0.0",
                true,
                EffectiveSchemaFixture.CreateProjectSchema(("schoolExtensions", "SchoolExtension", true))
            ),
        };

        var resourceKeys = new[]
        {
            new ResourceKeyEntry(1, new QualifiedResourceName("Core", "School"), "1.0.0", false),
            new ResourceKeyEntry(2, new QualifiedResourceName("Sample", "SchoolExtension"), "1.0.0", false),
        };

        var context = ExtensionProjectKeyFixture.CreateContext(projects, resourceKeys);
        var extensionSite = ExtensionProjectKeyFixture.CreateExtensionSite("core");

        try
        {
            context.ResolveExtensionProjectKey(
                "core",
                extensionSite,
                new QualifiedResourceName("Core", "School")
            );
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail when the key resolves to a non extension project.
    /// </summary>
    [Test]
    public void It_should_fail_when_the_key_resolves_to_a_non_extension_project()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("core");
        _exception.Message.Should().Contain("resource 'Core:School'");
        _exception.Message.Should().Contain("non-extension project 'core' (Core)");
    }
}

/// <summary>
/// Test fixture for an effective schema set with an unknown extension project key.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaSet_With_An_Unknown_Extension_Project_Key
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projects = new[]
        {
            new EffectiveProjectSchema(
                "ed-fi",
                "Ed-Fi",
                "5.0.0",
                false,
                EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false))
            ),
            new EffectiveProjectSchema(
                "sample",
                "Sample",
                "1.0.0",
                true,
                EffectiveSchemaFixture.CreateProjectSchema(("schoolExtensions", "SchoolExtension", true))
            ),
        };

        var resourceKeys = new[]
        {
            new ResourceKeyEntry(1, new QualifiedResourceName("Ed-Fi", "School"), "1.0.0", false),
            new ResourceKeyEntry(2, new QualifiedResourceName("Sample", "SchoolExtension"), "1.0.0", false),
        };

        var context = ExtensionProjectKeyFixture.CreateContext(projects, resourceKeys);
        var extensionSites = new[]
        {
            new ExtensionSite(
                JsonPathExpressionCompiler.Compile("$"),
                JsonPathExpressionCompiler.Compile("$._ext"),
                new[] { "unknown" }
            ),
        };

        try
        {
            context.RegisterExtensionSitesForResource(
                new QualifiedResourceName("Ed-Fi", "School"),
                extensionSites
            );
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail with an unknown extension project key.
    /// </summary>
    [Test]
    public void It_should_fail_with_an_unknown_extension_project_key()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("unknown");
        _exception.Message.Should().Contain("owning scope");
        _exception.Message.Should().Contain("$._ext");
        _exception.Message.Should().Contain("Ed-Fi:School");
    }
}

/// <summary>
/// Test fixture for an effective schema set with an ambiguous extension project key.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaSet_With_An_Ambiguous_Extension_Project_Key
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projects = new[]
        {
            new EffectiveProjectSchema(
                "ed-fi",
                "Ed-Fi",
                "5.0.0",
                false,
                EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false))
            ),
            new EffectiveProjectSchema(
                "sample-one",
                "Sample",
                "1.0.0",
                true,
                EffectiveSchemaFixture.CreateProjectSchema(("studentExtensions", "StudentExtension", true))
            ),
            new EffectiveProjectSchema(
                "sample-two",
                "Sample",
                "1.0.0",
                true,
                EffectiveSchemaFixture.CreateProjectSchema(("staffExtensions", "StaffExtension", true))
            ),
        };

        var resourceKeys = new[]
        {
            new ResourceKeyEntry(1, new QualifiedResourceName("Ed-Fi", "School"), "1.0.0", false),
            new ResourceKeyEntry(2, new QualifiedResourceName("Sample", "StudentExtension"), "1.0.0", false),
            new ResourceKeyEntry(3, new QualifiedResourceName("Sample", "StaffExtension"), "1.0.0", false),
        };

        var context = ExtensionProjectKeyFixture.CreateContext(projects, resourceKeys);
        var extensionSites = new[]
        {
            new ExtensionSite(
                JsonPathExpressionCompiler.Compile("$"),
                JsonPathExpressionCompiler.Compile("$._ext"),
                new[] { "Sample" }
            ),
        };

        try
        {
            context.RegisterExtensionSitesForResource(
                new QualifiedResourceName("Ed-Fi", "School"),
                extensionSites
            );
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail with an ambiguous extension project key.
    /// </summary>
    [Test]
    public void It_should_fail_with_an_ambiguous_extension_project_key()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Sample");
        _exception.Message.Should().Contain("matches multiple");
        _exception.Message.Should().Contain("sample-one");
        _exception.Message.Should().Contain("sample-two");
    }
}

/// <summary>
/// Test fixture for mixed-case extension project keys during full set derivation.
/// </summary>
[TestFixture]
public class Given_A_Mixed_Case_Extension_Project_Key_During_Set_Derivation
{
    private DbTableModel _extensionTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSetFromFixtures(
            new (string FileName, bool IsExtensionProject)[]
            {
                ("hand-authored-name-override-extension-core-api-schema.json", false),
                ("hand-authored-name-override-extension-project-api-schema.json", true),
            }
        );

        ExtensionProjectKeyFixture.ReplaceExtensionProjectKey(effectiveSchemaSet, "sample", "SaMpLe");

        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());
        var derived = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        var schoolModel = derived
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource.ProjectName == "Ed-Fi"
                && resource.ResourceKey.Resource.ResourceName == "School"
            )
            .RelationalModel;

        _extensionTable = schoolModel.TablesInDependencyOrder.Single(table =>
            table.Table.Schema.Value == "sample" && table.Table.Name == "SchoolExtension"
        );
    }

    /// <summary>
    /// It should derive extension tables even when _ext keys differ only by casing.
    /// </summary>
    [Test]
    public void It_should_derive_extension_tables_when_extension_project_keys_have_mixed_casing()
    {
        _extensionTable.JsonScope.Canonical.Should().Be("$._ext.SaMpLe");
    }

    /// <summary>
    /// It should apply relative extension name overrides with mixed-case key resolution.
    /// </summary>
    [Test]
    public void It_should_apply_relative_extension_name_overrides_with_mixed_case_project_keys()
    {
        _extensionTable
            .Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Contain("ExtensionFieldOverride");
    }
}

/// <summary>
/// Test type extension project key fixture.
/// </summary>
internal static class ExtensionProjectKeyFixture
{
    /// <summary>
    /// Create extension site.
    /// </summary>
    public static ExtensionSite CreateExtensionSite(params string[] projectKeys)
    {
        return new ExtensionSite(
            JsonPathExpressionCompiler.Compile("$"),
            JsonPathExpressionCompiler.Compile("$._ext"),
            projectKeys
        );
    }

    /// <summary>
    /// Create context.
    /// </summary>
    public static RelationalModelSetBuilderContext CreateContext(
        IReadOnlyList<EffectiveProjectSchema> projects,
        IReadOnlyList<ResourceKeyEntry> resourceKeys
    )
    {
        var effectiveSchemaSet = CreateEffectiveSchemaSet(projects, resourceKeys);

        return new RelationalModelSetBuilderContext(
            effectiveSchemaSet,
            SqlDialect.Pgsql,
            new PgsqlDialectRules()
        );
    }

    /// <summary>
    /// Create effective schema set.
    /// </summary>
    public static EffectiveSchemaSet CreateEffectiveSchemaSet(
        IReadOnlyList<EffectiveProjectSchema> projects,
        IReadOnlyList<ResourceKeyEntry> resourceKeys
    )
    {
        var schemaComponents = projects
            .OrderBy(project => project.ProjectEndpointName, StringComparer.Ordinal)
            .Select(project => new SchemaComponentInfo(
                project.ProjectEndpointName,
                project.ProjectName,
                project.ProjectVersion,
                project.IsExtensionProject
            ))
            .ToArray();

        var effectiveSchemaInfo = new EffectiveSchemaInfo(
            "1.0.0",
            "1.0.0",
            "deadbeef",
            resourceKeys.Count,
            new byte[] { 0x01 },
            schemaComponents,
            resourceKeys
        );

        return new EffectiveSchemaSet(effectiveSchemaInfo, projects);
    }

    /// <summary>
    /// Replaces extension project-key names under <c>_ext</c> in all project schemas.
    /// </summary>
    public static void ReplaceExtensionProjectKey(
        EffectiveSchemaSet effectiveSchemaSet,
        string originalProjectKey,
        string replacementProjectKey
    )
    {
        foreach (var project in effectiveSchemaSet.ProjectsInEndpointOrder)
        {
            RewriteExtensionProjectKey(project.ProjectSchema, originalProjectKey, replacementProjectKey);
        }
    }

    /// <summary>
    /// Rewrites extension project-key names under <c>_ext</c> recursively in a schema node.
    /// </summary>
    private static void RewriteExtensionProjectKey(
        JsonNode? schemaNode,
        string originalProjectKey,
        string replacementProjectKey
    )
    {
        if (schemaNode is JsonArray schemaArray)
        {
            foreach (var item in schemaArray)
            {
                RewriteExtensionProjectKey(item, originalProjectKey, replacementProjectKey);
            }

            return;
        }

        if (schemaNode is not JsonObject schemaObject)
        {
            return;
        }

        if (
            schemaObject.TryGetPropertyValue("properties", out var propertiesNode)
            && propertiesNode is JsonObject propertiesObject
        )
        {
            RewriteExtensionProjectKeyOnProperties(
                propertiesObject,
                originalProjectKey,
                replacementProjectKey
            );
        }

        foreach (var property in schemaObject)
        {
            RewriteExtensionProjectKey(property.Value, originalProjectKey, replacementProjectKey);
        }
    }

    /// <summary>
    /// Rewrites a direct <c>_ext.properties.{projectKey}</c> key on a properties object when present.
    /// </summary>
    private static void RewriteExtensionProjectKeyOnProperties(
        JsonObject propertiesObject,
        string originalProjectKey,
        string replacementProjectKey
    )
    {
        if (
            !propertiesObject.TryGetPropertyValue("_ext", out var extNode)
            || extNode is not JsonObject extObject
            || !extObject.TryGetPropertyValue("properties", out var extensionPropertiesNode)
            || extensionPropertiesNode is not JsonObject extensionProperties
            || !extensionProperties.TryGetPropertyValue(originalProjectKey, out var projectSchema)
        )
        {
            return;
        }

        extensionProperties.Remove(originalProjectKey);
        extensionProperties[replacementProjectKey] = projectSchema;
    }
}
