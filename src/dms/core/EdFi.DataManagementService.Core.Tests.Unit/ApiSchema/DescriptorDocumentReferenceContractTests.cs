// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;

/// <summary>
/// Pins the ApiSchema fact the descriptor write authorization path depends on: no descriptor resource declares
/// a document reference.
/// </summary>
/// <remarks>
/// <para>
/// Descriptor writes bind no document references, so there is no finalized root row for a proposed custom-view
/// basis value to be read from. Only a self-basis proposed check can be settled on that path; anything else
/// fails closed as a security-configuration error rather than being skipped.
/// </para>
/// <para>
/// That fail-closed branch is unreachable today only because of this data fact —
/// <c>ReferenceBindingPass</c> applies whatever <c>documentPathsMapping</c> yields and has no storage-kind
/// guard. If a future Data Standard or extension gives a descriptor a document reference, this test fails and
/// the descriptor write path needs a real proposed-value extractor rather than the fail-closed branch.
/// </para>
/// </remarks>
[TestFixture]
[Parallelizable]
public class Given_The_Shipped_ApiSchema_Descriptor_Resources
{
    [Test]
    public void They_declare_no_document_references()
    {
        var packages = LoadShippedApiSchemaPackages();

        // A relocated or renamed package directory would otherwise make this test vacuously pass.
        packages.Should().NotBeEmpty("the shipped ApiSchema packages must be discoverable to pin this fact");

        var descriptorsWithReferences = new List<string>();
        var descriptorCount = 0;

        foreach (var (packageName, document) in packages)
        {
            foreach (var project in EnumerateProjectSchemas(document.RootElement))
            {
                if (!project.TryGetProperty("resourceSchemas", out var resourceSchemas))
                {
                    continue;
                }

                foreach (var resource in resourceSchemas.EnumerateObject())
                {
                    if (
                        !resource.Value.TryGetProperty("isDescriptor", out var isDescriptor)
                        || !isDescriptor.GetBoolean()
                    )
                    {
                        continue;
                    }

                    descriptorCount++;

                    if (DeclaresDocumentReference(resource.Value))
                    {
                        descriptorsWithReferences.Add($"{packageName}:{resource.Name}");
                    }
                }
            }
        }

        descriptorCount.Should().BePositive("the shipped packages must contain descriptor resources");
        descriptorsWithReferences.Should().BeEmpty();
    }

    private static bool DeclaresDocumentReference(JsonElement resourceSchema) =>
        resourceSchema.TryGetProperty("documentPathsMapping", out var documentPathsMapping)
        && documentPathsMapping
            .EnumerateObject()
            .Any(mapping =>
                mapping.Value.TryGetProperty("isReference", out var isReference) && isReference.GetBoolean()
            );

    /// <summary>
    /// A package document carries either a single <c>projectSchema</c> or a <c>projectSchemas</c> map, so both
    /// shapes are walked rather than assuming one.
    /// </summary>
    private static IEnumerable<JsonElement> EnumerateProjectSchemas(JsonElement root)
    {
        if (root.TryGetProperty("projectSchema", out var projectSchema))
        {
            yield return projectSchema;
        }

        if (!root.TryGetProperty("projectSchemas", out var projectSchemas))
        {
            yield break;
        }

        if (projectSchemas.TryGetProperty("resourceSchemas", out _))
        {
            yield return projectSchemas;
            yield break;
        }

        foreach (var project in projectSchemas.EnumerateObject())
        {
            yield return project.Value;
        }
    }

    private static List<(string PackageName, JsonDocument Document)> LoadShippedApiSchemaPackages()
    {
        var packagesRoot = Path.Combine(AppContext.BaseDirectory, "ApiSchema", "Packages");

        if (!Directory.Exists(packagesRoot))
        {
            return [];
        }

        List<(string, JsonDocument)> packages = [];

        foreach (var packageDirectory in Directory.EnumerateDirectories(packagesRoot))
        {
            var schemaPath = Path.Combine(packageDirectory, "ApiSchema.json");

            if (File.Exists(schemaPath))
            {
                packages.Add(
                    (Path.GetFileName(packageDirectory), JsonDocument.Parse(File.ReadAllText(schemaPath)))
                );
            }
        }

        return packages;
    }
}
