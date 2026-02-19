// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel.Manifest;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture verifying that openApiFragments sections in resource schemas
/// are excluded from relational model derivation.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaSet_With_OpenApiFragments_Added
{
    private string _baselineManifest = default!;
    private string _withOpenApiFragmentsManifest = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var baselineSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();

        var withOpenApiSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        InjectOpenApiFragments(withOpenApiSchemaSet);

        var baselineBuilder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());
        var withOpenApiBuilder = new DerivedRelationalModelSetBuilder(
            RelationalModelSetPasses.CreateDefault()
        );

        var baselineModelSet = baselineBuilder.Build(
            baselineSchemaSet,
            SqlDialect.Pgsql,
            new PgsqlDialectRules()
        );
        var withOpenApiModelSet = withOpenApiBuilder.Build(
            withOpenApiSchemaSet,
            SqlDialect.Pgsql,
            new PgsqlDialectRules()
        );

        _baselineManifest = DerivedModelSetManifestEmitter.Emit(baselineModelSet);
        _withOpenApiFragmentsManifest = DerivedModelSetManifestEmitter.Emit(withOpenApiModelSet);
    }

    /// <summary>
    /// It should produce identical manifests regardless of openApiFragments presence.
    /// </summary>
    [Test]
    public void It_should_produce_identical_manifests_regardless_of_openApiFragments()
    {
        _withOpenApiFragmentsManifest.Should().Be(_baselineManifest);
    }

    /// <summary>
    /// Injects openApiFragments into every resource schema in every project of the effective schema set.
    /// </summary>
    private static void InjectOpenApiFragments(EffectiveSchemaSet effectiveSchemaSet)
    {
        foreach (var project in effectiveSchemaSet.ProjectsInEndpointOrder)
        {
            var projectSchema = project.ProjectSchema;
            if (projectSchema is null)
            {
                continue;
            }

            if (projectSchema["resourceSchemas"] is JsonObject resourceSchemas)
            {
                InjectOpenApiFragmentsInto(resourceSchemas);
            }

            if (projectSchema["abstractResources"] is JsonObject abstractResources)
            {
                InjectOpenApiFragmentsInto(abstractResources);
            }
        }
    }

    /// <summary>
    /// Adds an openApiFragments node to each resource schema entry.
    /// </summary>
    private static void InjectOpenApiFragmentsInto(JsonObject schemas)
    {
        foreach (var entry in schemas)
        {
            if (entry.Value is JsonObject resourceSchema)
            {
                resourceSchema["openApiFragments"] = new JsonObject
                {
                    ["get"] = new JsonObject
                    {
                        ["description"] = "Retrieves a specific resource using the resource identifier.",
                        ["responses"] = new JsonObject
                        {
                            ["200"] = new JsonObject { ["description"] = "The requested resource." },
                            ["404"] = new JsonObject { ["description"] = "Not found." },
                        },
                    },
                    ["post"] = new JsonObject
                    {
                        ["description"] = "Creates or updates a resource.",
                        ["responses"] = new JsonObject
                        {
                            ["201"] = new JsonObject { ["description"] = "Created." },
                            ["400"] = new JsonObject { ["description"] = "Bad request." },
                        },
                    },
                };
            }
        }
    }
}

/// <summary>
/// Test fixture verifying that modifying existing openApiFragments sections
/// does not affect relational model derivation.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaSet_With_OpenApiFragments_Modified
{
    private string _smallFragmentsManifest = default!;
    private string _largeFragmentsManifest = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var smallSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        InjectOpenApiFragments(smallSchemaSet, "Retrieves a resource.");

        var largeSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        InjectOpenApiFragments(
            largeSchemaSet,
            "Retrieves a specific resource using the resource's unique identifier "
                + "with detailed error information and complete response body including all nested objects."
        );

        var smallBuilder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());
        var largeBuilder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());

        var smallModelSet = smallBuilder.Build(smallSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        var largeModelSet = largeBuilder.Build(largeSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _smallFragmentsManifest = DerivedModelSetManifestEmitter.Emit(smallModelSet);
        _largeFragmentsManifest = DerivedModelSetManifestEmitter.Emit(largeModelSet);
    }

    /// <summary>
    /// It should produce identical manifests when only openApiFragments content differs.
    /// </summary>
    [Test]
    public void It_should_produce_identical_manifests_when_only_openApiFragments_differ()
    {
        _largeFragmentsManifest.Should().Be(_smallFragmentsManifest);
    }

    /// <summary>
    /// Injects openApiFragments with the given description into every resource schema.
    /// </summary>
    private static void InjectOpenApiFragments(EffectiveSchemaSet effectiveSchemaSet, string description)
    {
        foreach (var project in effectiveSchemaSet.ProjectsInEndpointOrder)
        {
            var projectSchema = project.ProjectSchema;
            if (projectSchema is null)
            {
                continue;
            }

            if (projectSchema["resourceSchemas"] is JsonObject resourceSchemas)
            {
                InjectOpenApiFragmentsInto(resourceSchemas, description);
            }

            if (projectSchema["abstractResources"] is JsonObject abstractResources)
            {
                InjectOpenApiFragmentsInto(abstractResources, description);
            }
        }
    }

    private static void InjectOpenApiFragmentsInto(JsonObject schemas, string description)
    {
        foreach (var entry in schemas)
        {
            if (entry.Value is JsonObject resourceSchema)
            {
                resourceSchema["openApiFragments"] = new JsonObject
                {
                    ["get"] = new JsonObject { ["description"] = description },
                };
            }
        }
    }
}
