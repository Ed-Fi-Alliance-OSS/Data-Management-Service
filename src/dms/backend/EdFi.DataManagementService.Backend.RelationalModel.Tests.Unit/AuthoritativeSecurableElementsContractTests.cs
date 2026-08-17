// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Contract helpers for asserting securable-element scope invariants over the bundled
/// authoritative ApiSchema fixture inputs. Namespace-based authorization applies only to
/// resource-root Namespace fields, so authoritative artifacts must not declare
/// <c>securableElements.Namespace</c> paths inside collections (canonical paths containing
/// <c>[*]</c>) or beneath resource-extension containers (canonical paths containing
/// <c>._ext.</c>, which covers both <c>$._ext.…</c> and <c>$.collection[*]._ext.…</c> forms).
/// These are artifact-level contract checks; the generator itself determines collection and
/// extension scope from semantic metadata rather than path text.
/// </summary>
internal static class AuthoritativeApiSchemaContractHelpers
{
    public static JsonObject LoadResourceSchemas(string fixtureSetName, string fileName)
    {
        var authoritativeFixtureRoot = BackendFixturePaths.GetAuthoritativeFixtureRoot(
            TestContext.CurrentContext.TestDirectory
        );
        var inputPath = Path.Combine(authoritativeFixtureRoot, fixtureSetName, "inputs", fileName);

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException($"Authoritative fixture not found: {inputPath}", inputPath);
        }

        var root = JsonNode.Parse(File.ReadAllText(inputPath));
        if (root?["projectSchema"]?["resourceSchemas"] is not JsonObject resourceSchemas)
        {
            throw new InvalidOperationException(
                $"ApiSchema is missing projectSchema.resourceSchemas: {inputPath}"
            );
        }

        return resourceSchemas;
    }

    /// <summary>
    /// Extracts every <c>securableElements.Namespace</c> path with its declaring resource
    /// endpoint name, e.g. <c>("studentAssessments", "$.assessmentReference.namespace")</c>.
    /// </summary>
    public static IReadOnlyList<(string Resource, string Path)> GetNamespaceSecurablePaths(
        JsonObject resourceSchemas
    )
    {
        var result = new List<(string Resource, string Path)>();

        foreach (var (resourceName, resourceSchema) in resourceSchemas)
        {
            if (resourceSchema?["securableElements"]?["Namespace"] is not JsonArray namespacePaths)
            {
                continue;
            }

            foreach (var pathNode in namespacePaths)
            {
                var path =
                    pathNode?.GetValue<string>()
                    ?? throw new InvalidOperationException(
                        $"Resource '{resourceName}' has a null securableElements.Namespace entry."
                    );
                result.Add((resourceName, path));
            }
        }

        return result;
    }

    /// <summary>
    /// Recursively collects every string value beneath a resource-schema section (structural
    /// traversal of the parsed JSON, not a raw-text scan), so retention checks can assert a
    /// JSONPath is still referenced by that section regardless of the property that carries it.
    /// </summary>
    public static HashSet<string> CollectStringValues(JsonNode? node)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        Collect(node, values);
        return values;

        static void Collect(JsonNode? current, HashSet<string> accumulator)
        {
            switch (current)
            {
                case JsonObject jsonObject:
                    foreach (var (_, child) in jsonObject)
                    {
                        Collect(child, accumulator);
                    }
                    break;
                case JsonArray jsonArray:
                    foreach (var child in jsonArray)
                    {
                        Collect(child, accumulator);
                    }
                    break;
                case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var stringValue):
                    accumulator.Add(stringValue);
                    break;
            }
        }
    }

    public static IReadOnlyList<(string Resource, string Path)> CollectionScopedPaths(
        IReadOnlyList<(string Resource, string Path)> namespacePaths
    ) => [.. namespacePaths.Where(entry => entry.Path.Contains("[*]", StringComparison.Ordinal))];

    public static IReadOnlyList<(string Resource, string Path)> ExtensionScopedPaths(
        IReadOnlyList<(string Resource, string Path)> namespacePaths
    ) => [.. namespacePaths.Where(entry => entry.Path.Contains("._ext.", StringComparison.Ordinal))];
}

/// <summary>
/// Test fixture for the Namespace securable-element scope contract of the authoritative
/// Data Standard 5.2 ApiSchema artifact.
/// </summary>
[TestFixture]
public class Given_The_Authoritative_Ds52_ApiSchema_Securable_Elements
{
    /// <summary>
    /// The collection-scoped Namespace paths that Data Standard 5.2 artifacts historically
    /// declared and that must never reappear in <c>securableElements.Namespace</c>. Each remains
    /// valid reference metadata elsewhere in its resource schema.
    /// </summary>
    private static readonly (string Resource, string Path)[] _removedCollectionPaths =
    [
        ("assessmentAdministrations", "$.assessmentBatteryParts[*].assessmentBatteryPartReference.namespace"),
        ("assessmentBatteryParts", "$.objectiveAssessments[*].objectiveAssessmentReference.namespace"),
        ("graduationPlans", "$.requiredAssessments[*].assessmentReference.namespace"),
        ("objectiveAssessments", "$.assessmentItems[*].assessmentItemReference.namespace"),
        ("studentAssessments", "$.items[*].assessmentItemReference.namespace"),
        ("studentAssessments", "$.studentObjectiveAssessments[*].objectiveAssessmentReference.namespace"),
    ];

    private JsonObject _resourceSchemas = default!;
    private IReadOnlyList<(string Resource, string Path)> _namespacePaths = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _resourceSchemas = AuthoritativeApiSchemaContractHelpers.LoadResourceSchemas(
            "ds-5.2",
            "ds-5.2-api-schema-authoritative.json"
        );
        _namespacePaths = AuthoritativeApiSchemaContractHelpers.GetNamespaceSecurablePaths(_resourceSchemas);
    }

    [Test]
    public void It_declares_no_collection_scoped_namespace_securable_elements()
    {
        AuthoritativeApiSchemaContractHelpers
            .CollectionScopedPaths(_namespacePaths)
            .Should()
            .BeEmpty("Namespace authorization applies only to resource-root fields");
    }

    [Test]
    public void It_declares_no_extension_namespace_securable_elements()
    {
        AuthoritativeApiSchemaContractHelpers
            .ExtensionScopedPaths(_namespacePaths)
            .Should()
            .BeEmpty("fields beneath an _ext container must not be Namespace securable elements");
    }

    [Test]
    public void It_declares_none_of_the_removed_collection_paths()
    {
        _namespacePaths.Should().NotContain(_removedCollectionPaths);
    }

    [Test]
    public void It_retains_root_scope_namespace_securable_elements_on_the_affected_resources()
    {
        var namespacePathsByResource = _namespacePaths
            .GroupBy(entry => entry.Resource, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Path).ToArray(),
                StringComparer.Ordinal
            );

        namespacePathsByResource["assessmentAdministrations"]
            .Should()
            .Equal("$.assessmentReference.namespace");
        namespacePathsByResource["assessmentBatteryParts"].Should().Equal("$.assessmentReference.namespace");
        namespacePathsByResource
            .Should()
            .NotContainKey(
                "graduationPlans",
                "graduationPlans has no root-scope Namespace securable element"
            );
        namespacePathsByResource["objectiveAssessments"]
            .Should()
            .Equal("$.assessmentReference.namespace", "$.parentObjectiveAssessmentReference.namespace");
        namespacePathsByResource["studentAssessments"].Should().Equal("$.assessmentReference.namespace");
    }

    [Test]
    public void It_retains_collection_reference_metadata_for_the_removed_paths()
    {
        foreach (var (resource, path) in _removedCollectionPaths)
        {
            var resourceSchema = _resourceSchemas[resource];
            resourceSchema.Should().NotBeNull($"resource '{resource}' must exist in the artifact");

            var documentPathsMappingValues = AuthoritativeApiSchemaContractHelpers.CollectStringValues(
                resourceSchema!["documentPathsMapping"]
            );
            documentPathsMappingValues
                .Should()
                .Contain(path, $"'{resource}' must keep the reference metadata for '{path}'");
        }
    }

    [Test]
    public void It_retains_equality_constraints_for_the_removed_reference_paths()
    {
        // graduationPlans is the one affected resource whose removed path participates in no
        // equality constraint; its collection scope is carried by arrayUniquenessConstraints.
        foreach (
            var (resource, path) in _removedCollectionPaths.Where(entry =>
                entry.Resource is not "graduationPlans"
            )
        )
        {
            var equalityConstraintValues = AuthoritativeApiSchemaContractHelpers.CollectStringValues(
                _resourceSchemas[resource]!["equalityConstraints"]
            );
            equalityConstraintValues
                .Should()
                .Contain(path, $"'{resource}' must keep the equality constraint for '{path}'");
        }
    }

    [Test]
    public void It_retains_array_uniqueness_constraints_for_the_removed_collection_identity_paths()
    {
        (string Resource, string Path)[] arrayUniquenessBackedPaths =
        [
            ("graduationPlans", "$.requiredAssessments[*].assessmentReference.namespace"),
            ("studentAssessments", "$.items[*].assessmentItemReference.namespace"),
            ("studentAssessments", "$.studentObjectiveAssessments[*].objectiveAssessmentReference.namespace"),
        ];

        foreach (var (resource, path) in arrayUniquenessBackedPaths)
        {
            var arrayUniquenessValues = AuthoritativeApiSchemaContractHelpers.CollectStringValues(
                _resourceSchemas[resource]!["arrayUniquenessConstraints"]
            );
            arrayUniquenessValues
                .Should()
                .Contain(path, $"'{resource}' must keep the array uniqueness constraint for '{path}'");
        }
    }
}

/// <summary>
/// Test fixture for the Namespace securable-element scope contract of the authoritative
/// TPDM extension ApiSchema artifact.
/// </summary>
[TestFixture]
public class Given_The_Authoritative_Tpdm_ApiSchema_Securable_Elements
{
    private IReadOnlyList<(string Resource, string Path)> _namespacePaths = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var resourceSchemas = AuthoritativeApiSchemaContractHelpers.LoadResourceSchemas(
            "ds-5.2-tpdm",
            "tpdm-api-schema-authoritative.json"
        );
        _namespacePaths = AuthoritativeApiSchemaContractHelpers.GetNamespaceSecurablePaths(resourceSchemas);
    }

    [Test]
    public void It_declares_no_collection_scoped_namespace_securable_elements()
    {
        AuthoritativeApiSchemaContractHelpers
            .CollectionScopedPaths(_namespacePaths)
            .Should()
            .BeEmpty("Namespace authorization applies only to resource-root fields");
    }

    [Test]
    public void It_declares_no_extension_namespace_securable_elements()
    {
        AuthoritativeApiSchemaContractHelpers
            .ExtensionScopedPaths(_namespacePaths)
            .Should()
            .BeEmpty("fields beneath an _ext container must not be Namespace securable elements");
    }
}

/// <summary>
/// Test fixture for the Namespace securable-element scope contract of the authoritative
/// Sample extension ApiSchema artifact.
/// </summary>
[TestFixture]
public class Given_The_Authoritative_Sample_ApiSchema_Securable_Elements
{
    private IReadOnlyList<(string Resource, string Path)> _namespacePaths = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var resourceSchemas = AuthoritativeApiSchemaContractHelpers.LoadResourceSchemas(
            "sample",
            "sample-api-schema-authoritative.json"
        );
        _namespacePaths = AuthoritativeApiSchemaContractHelpers.GetNamespaceSecurablePaths(resourceSchemas);
    }

    [Test]
    public void It_declares_no_collection_scoped_namespace_securable_elements()
    {
        AuthoritativeApiSchemaContractHelpers
            .CollectionScopedPaths(_namespacePaths)
            .Should()
            .BeEmpty("Namespace authorization applies only to resource-root fields");
    }

    [Test]
    public void It_declares_no_extension_namespace_securable_elements()
    {
        AuthoritativeApiSchemaContractHelpers
            .ExtensionScopedPaths(_namespacePaths)
            .Should()
            .BeEmpty("fields beneath an _ext container must not be Namespace securable elements");
    }
}
