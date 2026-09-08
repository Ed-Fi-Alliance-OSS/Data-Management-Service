// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using EdFi.DataManagementService.Backend.RelationalModel.Manifest;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_A_Reference_Binding_With_Duplicate_ReferenceJsonPaths
{
    private static readonly QualifiedResourceName _sectionResource = new("Ed-Fi", "Section");
    private DocumentReferenceBinding _binding = null!;
    private string _manifest = null!;

    [SetUp]
    public void Setup()
    {
        var derivedSet = BuildDerivedSet();
        var sectionModel = derivedSet
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource == _sectionResource
            )
            .RelationalModel;

        _binding = sectionModel.DocumentReferenceBindings.Should().ContainSingle().Subject;
        _manifest = DerivedModelSetManifestEmitter.Emit(
            derivedSet,
            new HashSet<QualifiedResourceName> { _sectionResource }
        );
    }

    [Test]
    public void It_should_preserve_distinct_identity_json_paths_in_compiled_binding_order()
    {
        _binding
            .IdentityBindings.Select(binding =>
                (
                    binding.IdentityJsonPath.Canonical,
                    binding.ReferenceJsonPath.Canonical,
                    binding.Column.Value
                )
            )
            .Should()
            .Equal(
                (
                    "$.educationOrganizationId",
                    "$.educationOrganizationReference.educationOrganizationId",
                    "EducationOrganization_EducationOrganizationId"
                ),
                (
                    "$.localEducationAgencyId",
                    "$.educationOrganizationReference.educationOrganizationId",
                    "EducationOrganization_LocalEducationAgencyId"
                )
            );
    }

    [Test]
    public void It_should_emit_identity_json_paths_in_the_detailed_manifest()
    {
        var root =
            JsonNode.Parse(_manifest) as JsonObject
            ?? throw new InvalidOperationException("Expected manifest to be a JSON object.");
        var resourceDetails =
            root["resource_details"] as JsonArray
            ?? throw new InvalidOperationException("Expected resource_details to be a JSON array.");
        var resourceDetail =
            resourceDetails.Should().ContainSingle().Subject as JsonObject
            ?? throw new InvalidOperationException("Expected resource detail to be a JSON object.");
        var documentReferenceBindings =
            resourceDetail["document_reference_bindings"] as JsonArray
            ?? throw new InvalidOperationException(
                "Expected document_reference_bindings to be a JSON array."
            );
        var documentReferenceBinding =
            documentReferenceBindings.Should().ContainSingle().Subject as JsonObject
            ?? throw new InvalidOperationException(
                "Expected document_reference_bindings entry to be a JSON object."
            );
        var identityBindings =
            documentReferenceBinding["identity_bindings"] as JsonArray
            ?? throw new InvalidOperationException("Expected identity_bindings to be a JSON array.");

        identityBindings
            .Select(node =>
            {
                var identityBinding =
                    node as JsonObject
                    ?? throw new InvalidOperationException(
                        "Expected identity_bindings entry to be a JSON object."
                    );

                return (
                    IdentityJsonPath: identityBinding["identity_json_path"]!.GetValue<string>(),
                    ReferenceJsonPath: identityBinding["reference_json_path"]!.GetValue<string>(),
                    Column: identityBinding["column"]!.GetValue<string>()
                );
            })
            .Should()
            .Equal(
                (
                    "$.educationOrganizationId",
                    "$.educationOrganizationReference.educationOrganizationId",
                    "EducationOrganization_EducationOrganizationId"
                ),
                (
                    "$.localEducationAgencyId",
                    "$.educationOrganizationReference.educationOrganizationId",
                    "EducationOrganization_LocalEducationAgencyId"
                )
            );
    }

    private static DerivedRelationalModelSet BuildDerivedSet()
    {
        var projectSchema = new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["sections"] = BuildSectionSchema(),
                ["educationOrganizations"] = BuildEducationOrganizationSchema(),
            },
        };
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([project]);
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new ReferenceBindingPass(),
            }
        );

        return builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    private static JsonObject BuildSectionSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "Section",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganization"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isPartOfIdentity"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "EducationOrganization",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.educationOrganizationId",
                            ["referenceJsonPath"] =
                                "$.educationOrganizationReference.educationOrganizationId",
                        },
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.localEducationAgencyId",
                            ["referenceJsonPath"] =
                                "$.educationOrganizationReference.educationOrganizationId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["educationOrganizationReference"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                        },
                        ["required"] = new JsonArray { "educationOrganizationId" },
                    },
                },
                ["required"] = new JsonArray { "educationOrganizationReference" },
            },
        };
    }

    private static JsonObject BuildEducationOrganizationSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "EducationOrganization",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId", "$.localEducationAgencyId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isPartOfIdentity"] = true,
                    ["isRequired"] = true,
                    ["path"] = "$.educationOrganizationId",
                },
                ["LocalEducationAgencyId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isPartOfIdentity"] = true,
                    ["isRequired"] = true,
                    ["path"] = "$.localEducationAgencyId",
                },
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                    ["localEducationAgencyId"] = new JsonObject { ["type"] = "integer" },
                },
                ["required"] = new JsonArray { "educationOrganizationId", "localEducationAgencyId" },
            },
        };
    }
}
