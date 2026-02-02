// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_An_Extension_Project_Key_Matching_Project_Endpoint_Name
{
    private ProjectSchemaInfo _project = default!;

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

    [Test]
    public void It_should_resolve_to_the_matching_endpoint_name()
    {
        _project.ProjectEndpointName.Should().Be("sample-ext");
    }
}

[TestFixture]
public class Given_An_Extension_Project_Key_Matching_Project_Name
{
    private ProjectSchemaInfo _project = default!;

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

    [Test]
    public void It_should_resolve_to_the_matching_project_name_when_no_endpoint_match_exists()
    {
        _project.ProjectEndpointName.Should().Be("sample-ext");
    }
}

[TestFixture]
public class Given_An_Extension_Project_Key_Matching_Endpoint_And_Project_Names
{
    private ProjectSchemaInfo _project = default!;

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

    [Test]
    public void It_should_prefer_endpoint_name_matches_over_project_name_fallbacks()
    {
        _project.ProjectEndpointName.Should().Be("alpha");
    }
}

[TestFixture]
public class Given_An_Extension_Project_Key_Matching_A_Non_Extension_Project
{
    private Exception? _exception;

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

    [Test]
    public void It_should_fail_when_the_key_resolves_to_a_non_extension_project()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("core");
        _exception.Message.Should().Contain("resource 'Core:School'");
        _exception.Message.Should().Contain("non-extension project 'core' (Core)");
    }
}

[TestFixture]
public class Given_An_EffectiveSchemaSet_With_An_Unknown_Extension_Project_Key
{
    private Exception? _exception;

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

[TestFixture]
public class Given_An_EffectiveSchemaSet_With_An_Ambiguous_Extension_Project_Key
{
    private Exception? _exception;

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

internal static class ExtensionProjectKeyFixture
{
    public static ExtensionSite CreateExtensionSite(params string[] projectKeys)
    {
        return new ExtensionSite(
            JsonPathExpressionCompiler.Compile("$"),
            JsonPathExpressionCompiler.Compile("$._ext"),
            projectKeys
        );
    }

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
}
