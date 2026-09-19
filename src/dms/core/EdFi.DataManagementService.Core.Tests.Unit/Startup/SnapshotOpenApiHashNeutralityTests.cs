// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

/// <summary>
/// Proves the snapshot OpenAPI contract (DMS-1369) cannot influence the effective schema hash,
/// against the pinned ApiSchema packages exactly as NuGet restored them.
/// </summary>
/// <remarks>
/// <para>
/// The snapshot contract reaches DMS only inside published ApiSchema packages, so adopting it is a
/// package change rather than a code change. The story requires that adoption to be OpenAPI-only:
/// no effective-schema hash movement, no <c>apiSchemaVersion</c> bump, and no churn in the DDL,
/// plan, or mapping goldens derived from the hashed schema.
/// </para>
/// <para>
/// This verifies that expectation rather than assuming it, and it does so structurally rather than
/// by comparing two package versions. Comparing versions would only say that one particular bump
/// happened to be neutral; instead each fixture takes the packaged schema, deletes every snapshot
/// artifact from the OpenAPI payloads, and proves the normalized schema and its hash are byte-for-byte
/// unchanged. That holds for any future snapshot-only OpenAPI change, and it fails loudly if a
/// package ever carries snapshot content outside the payloads
/// <see cref="ApiSchemaInputNormalizer" /> strips.
/// </para>
/// <para>
/// DMS hashes the selected core package together with its extension packages, each as its own
/// project in the manifest <see cref="EffectiveSchemaHashProvider" /> hashes, so each fixture covers
/// a complete supported effective schema set rather than a core package alone. Because the per-project
/// hashes are independent, neutrality over a full set also proves it for every subset a deployment
/// might select, the bundled core-plus-TPDM set included.
/// </para>
/// <para>
/// This is the structural half of the story's hash-neutrality coverage and deliberately not the whole
/// of it. An unrelated schema change that shipped alongside the snapshot contract would sit in both
/// operands here and pass unnoticed. The historical half is
/// <c>SnapshotOpenApiPackageBumpHashTests</c> in the ApiSchemaDownloader integration suite, which
/// hashes the published package sets on both sides of the bump that introduced the contract. It lives
/// there because central package management pins one version per package, so this fixture can only
/// ever see the current one.
/// </para>
/// </remarks>
public abstract class SnapshotOpenApiHashNeutralityTests
{
    /// <summary>
    /// The <c>AssemblyMetadata</c> key under which the build recorded the core package's restored root.
    /// </summary>
    protected abstract string CorePackageRootMetadataKey { get; }

    /// <summary>
    /// The extension packages that complete this effective schema set, read from the packaged
    /// documents the build staged into this project's output.
    /// </summary>
    protected abstract IReadOnlyList<string> ExtensionPackageIds { get; }

    /// <summary>
    /// The reusable component names the upstream snapshot contract introduces. Deleting the
    /// declarations by name, rather than by a substring sweep, keeps the mutation confined to the
    /// snapshot contract.
    /// </summary>
    private static readonly string[] _snapshotComponentNames =
    [
        "Use-Snapshot",
        "SnapshotNotFound",
        "SnapshotMethodNotAllowed",
    ];

    /// <summary>
    /// The exact <c>$ref</c> targets operations use to reach those components. Matched in full, so a
    /// resource that merely has "Snapshot" in its name is never mistaken for snapshot contract.
    /// </summary>
    private static readonly string[] _snapshotComponentReferences =
    [
        "#/components/parameters/Use-Snapshot",
        "#/components/responses/SnapshotNotFound",
        "#/components/responses/SnapshotMethodNotAllowed",
    ];

    /// <summary>
    /// What one packaged document carried before its snapshot content was removed, and how much of
    /// that content the removal found. Kept per package so a package that contributed nothing to the
    /// proof is named rather than averaged away by the others.
    /// </summary>
    private sealed record PackagedDocumentFacts(
        string PackageId,
        bool IsCore,
        bool OpenApiBaseDocumentsPresent,
        int ResourceSchemaCount,
        int ResourceSchemasWithOpenApiFragments,
        int AbstractResourcesWithOpenApiFragment,
        int SnapshotArtifactsRemoved
    );

    private string _apiSchemaVersion = string.Empty;
    private readonly List<PackagedDocumentFacts> _packagedDocuments = [];

    private string _normalizedJsonDigest = string.Empty;
    private string _normalizedJsonDigestWithoutSnapshotContent = string.Empty;
    private string _effectiveSchemaHash = string.Empty;
    private string _effectiveSchemaHashWithoutSnapshotContent = string.Empty;
    private string _normalizedJson = string.Empty;
    private IReadOnlyList<JsonObject> _normalizedProjectSchemas = [];

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        JsonNode coreNode = PackagedApiSchemaContract.LoadPackagedRootNode(CorePackageRootMetadataKey);
        JsonNode[] extensionNodes =
        [
            .. ExtensionPackageIds.Select(PackagedApiSchemaContract.LoadStagedExtensionRootNode),
        ];
        ApiSchemaDocumentNodes nodes = new(coreNode, extensionNodes);

        _apiSchemaVersion = coreNode["apiSchemaVersion"]?.GetValue<string>() ?? string.Empty;

        // The packaged set as published, normalized and hashed the way startup does it.
        (_normalizedJson, _normalizedProjectSchemas, _effectiveSchemaHash) = NormalizeAndHash(nodes);
        _normalizedJsonDigest = Sha256Hex(_normalizedJson);

        // The same set with the snapshot contract deleted from every document's OpenAPI payloads.
        // Mutated in place rather than on clones: the pre-mutation state is already captured above,
        // and a second full copy of several multi-megabyte documents buys nothing.
        _packagedDocuments.Add(
            RecordAndRemoveSnapshotOpenApiContent(CorePackageRootMetadataKey, coreNode, isCore: true)
        );

        foreach ((string packageId, JsonNode extensionNode) in ExtensionPackageIds.Zip(extensionNodes))
        {
            _packagedDocuments.Add(
                RecordAndRemoveSnapshotOpenApiContent(packageId, extensionNode, isCore: false)
            );
        }

        (string withoutSnapshotJson, _, _effectiveSchemaHashWithoutSnapshotContent) = NormalizeAndHash(nodes);
        _normalizedJsonDigestWithoutSnapshotContent = Sha256Hex(withoutSnapshotJson);
    }

    [Test]
    public void It_declares_the_apiSchemaVersion_the_snapshot_contract_did_not_bump()
    {
        // The core's declaration stands for the set: the normalizer rejects an extension whose
        // apiSchemaVersion differs from the core's, so a normalized set has a single version.
        _apiSchemaVersion
            .Should()
            .Be(
                "1.0.0",
                "an OpenAPI-only package change must not move apiSchemaVersion; a change here means the "
                    + "package carried more than the snapshot OpenAPI contract"
            );
    }

    [Test]
    public void It_normalizes_every_package_in_the_effective_schema_set()
    {
        // The normalizer drops nothing silently, but the fixture's claim is about the set it names, so
        // the set that reached the hash is checked against that list rather than trusted.
        _normalizedProjectSchemas
            .Should()
            .HaveCount(
                1 + ExtensionPackageIds.Count,
                "the core package and each named extension package must each reach the hashed set as "
                    + "its own project"
            );
    }

    [Test]
    public void It_carries_snapshot_openapi_artifacts_to_neutralize_in_every_package()
    {
        // Compared per package against its resource count rather than against zero. Every packaged
        // resource carries the contract on several operations, so a sweep that found merely
        // "something" would still be consistent with having missed almost all of it, and neutrality
        // proven over almost nothing is not neutrality proven. Checked per package so an extension
        // whose fragments the sweep never reached cannot hide behind the core's count. Against a
        // pre-DMS-1371 package this finds nothing at all and fails.
        foreach (PackagedDocumentFacts document in _packagedDocuments)
        {
            document
                .SnapshotArtifactsRemoved.Should()
                .BeGreaterThan(
                    document.ResourceSchemaCount,
                    "the pinned package '{0}' publishes the snapshot contract on multiple operations of "
                        + "every resource, so the neutralized artifact count must exceed the resource count",
                    document.PackageId
                );
        }
    }

    [Test]
    public void It_strips_every_openapi_payload_before_hashing()
    {
        foreach (PackagedDocumentFacts document in _packagedDocuments)
        {
            if (document.IsCore)
            {
                document
                    .OpenApiBaseDocumentsPresent.Should()
                    .BeTrue("the packaged core schema must publish base documents");
                document
                    .AbstractResourcesWithOpenApiFragment.Should()
                    .BeGreaterThan(0, "the packaged core abstract resources carry an OpenAPI fragment");
            }

            document
                .ResourceSchemaCount.Should()
                .BeGreaterThan(0, "the packaged schema '{0}' must publish resources", document.PackageId);
            document
                .ResourceSchemasWithOpenApiFragments.Should()
                .Be(
                    document.ResourceSchemaCount,
                    "every packaged resource schema in '{0}' carries OpenAPI fragments, so stripping "
                        + "them is not a no-op",
                    document.PackageId
                );
        }

        foreach (JsonObject normalizedProjectSchema in _normalizedProjectSchemas)
        {
            normalizedProjectSchema.Should().NotContainKey("openApiBaseDocuments");
            CountChildrenWithProperty(normalizedProjectSchema["resourceSchemas"], "openApiFragments")
                .Should()
                .Be(0);
            CountChildrenWithProperty(normalizedProjectSchema["abstractResources"], "openApiFragment")
                .Should()
                .Be(0);
        }
    }

    [Test]
    public void It_leaves_no_snapshot_artifact_in_the_hashed_schema()
    {
        foreach (string componentName in _snapshotComponentNames)
        {
            _normalizedJson
                .Should()
                .NotContain(
                    componentName,
                    "'{0}' is OpenAPI-only content and must not survive into the hashed schema",
                    componentName
                );
        }
    }

    [Test]
    public void It_produces_an_identical_normalized_schema_without_the_snapshot_openapi_content()
    {
        _normalizedJsonDigestWithoutSnapshotContent
            .Should()
            .Be(
                _normalizedJsonDigest,
                "removing the snapshot contract changes only stripped OpenAPI payloads, so the normalized "
                    + "schema that feeds hashing, model derivation, DDL generation, and mapping-pack "
                    + "selection must be byte-for-byte identical"
            );
    }

    [Test]
    public void It_produces_an_identical_effective_schema_hash_without_the_snapshot_openapi_content()
    {
        _effectiveSchemaHashWithoutSnapshotContent
            .Should()
            .Be(
                _effectiveSchemaHash,
                "the snapshot OpenAPI contract must be hash-neutral, so adopting it needs no "
                    + "re-provisioning and churns no DDL, plan, or mapping golden"
            );
    }

    /// <summary>
    /// Normalizes through the production normalizer and hashes through the production hash provider,
    /// returning the JSON of every normalized document in hashed order, their projectSchemas, and the
    /// effective schema hash.
    /// </summary>
    private static (
        string NormalizedJson,
        IReadOnlyList<JsonObject> NormalizedProjectSchemas,
        string EffectiveSchemaHash
    ) NormalizeAndHash(ApiSchemaDocumentNodes nodes)
    {
        ApiSchemaInputNormalizer normalizer = new(NullLogger<ApiSchemaInputNormalizer>.Instance);
        ApiSchemaNormalizationResult result = normalizer.Normalize(nodes);

        if (result is not ApiSchemaNormalizationResult.SuccessResult success)
        {
            throw new InvalidOperationException(
                $"Normalizing the packaged ApiSchema set failed with {result.GetType().Name}."
            );
        }

        JsonNode[] normalizedNodes =
        [
            success.NormalizedNodes.CoreApiSchemaRootNode,
            .. success.NormalizedNodes.ExtensionApiSchemaRootNodes,
        ];

        JsonObject[] normalizedProjectSchemas =
        [
            .. normalizedNodes.Select(normalizedNode =>
                normalizedNode["projectSchema"]?.AsObject()
                ?? throw new InvalidOperationException("Normalized schema is missing projectSchema.")
            ),
        ];

        EffectiveSchemaHashProvider hashProvider = new(NullLogger<EffectiveSchemaHashProvider>.Instance);

        return (
            string.Join('\n', normalizedNodes.Select(normalizedNode => normalizedNode.ToJsonString())),
            normalizedProjectSchemas,
            hashProvider.ComputeHash(success.NormalizedNodes)
        );
    }

    /// <summary>
    /// Records what the packaged document carries, then deletes its snapshot content in place.
    /// </summary>
    private static PackagedDocumentFacts RecordAndRemoveSnapshotOpenApiContent(
        string packageId,
        JsonNode rootNode,
        bool isCore
    )
    {
        JsonObject projectSchema =
            rootNode["projectSchema"]?.AsObject()
            ?? throw new InvalidOperationException(
                $"Packaged ApiSchema '{packageId}' is missing projectSchema."
            );

        return new PackagedDocumentFacts(
            packageId,
            isCore,
            OpenApiBaseDocumentsPresent: projectSchema.ContainsKey("openApiBaseDocuments"),
            ResourceSchemaCount: (projectSchema["resourceSchemas"] as JsonObject)?.Count ?? 0,
            ResourceSchemasWithOpenApiFragments: CountChildrenWithProperty(
                projectSchema["resourceSchemas"],
                "openApiFragments"
            ),
            AbstractResourcesWithOpenApiFragment: CountChildrenWithProperty(
                projectSchema["abstractResources"],
                "openApiFragment"
            ),
            SnapshotArtifactsRemoved: RemoveSnapshotOpenApiContent(projectSchema)
        );
    }

    private static int CountChildrenWithProperty(JsonNode? container, string propertyName)
    {
        if (container is not JsonObject containerObject)
        {
            return 0;
        }

        return containerObject.Count(pair =>
            pair.Value is JsonObject child && child.ContainsKey(propertyName)
        );
    }

    /// <summary>
    /// Deletes every snapshot artifact from the three OpenAPI payload regions, returning how many were
    /// removed. Confined to those regions on purpose: the point is to show that content the normalizer
    /// strips cannot reach the hash, so touching anything outside them would prove nothing.
    /// </summary>
    private static int RemoveSnapshotOpenApiContent(JsonObject projectSchema)
    {
        int removed = 0;

        if (projectSchema["openApiBaseDocuments"] is JsonNode baseDocuments)
        {
            removed += RemoveSnapshotArtifacts(baseDocuments);
        }

        removed += RemoveSnapshotArtifactsFromChildren(projectSchema["resourceSchemas"], "openApiFragments");
        removed += RemoveSnapshotArtifactsFromChildren(projectSchema["abstractResources"], "openApiFragment");

        return removed;
    }

    private static int RemoveSnapshotArtifactsFromChildren(JsonNode? container, string payloadPropertyName)
    {
        if (container is not JsonObject containerObject)
        {
            return 0;
        }

        int removed = 0;

        foreach ((string _, JsonNode? child) in containerObject)
        {
            if (child?[payloadPropertyName] is JsonNode payload)
            {
                removed += RemoveSnapshotArtifacts(payload);
            }
        }

        return removed;
    }

    /// <summary>
    /// Removes the reusable component declarations by name and the operation-level references to them
    /// by exact <c>$ref</c> target, recursing through whatever is left.
    /// </summary>
    private static int RemoveSnapshotArtifacts(JsonNode node) =>
        node switch
        {
            JsonObject jsonObject => RemoveSnapshotArtifactsFromObject(jsonObject),
            JsonArray jsonArray => RemoveSnapshotArtifactsFromArray(jsonArray),
            _ => 0,
        };

    /// <summary>
    /// Drops the component declarations and the response entries that reference them, then recurses.
    /// Keys are collected before removal because the object is mutated as it is walked.
    /// </summary>
    private static int RemoveSnapshotArtifactsFromObject(JsonObject jsonObject)
    {
        List<string> keysToRemove =
        [
            .. jsonObject
                .Where(pair =>
                    _snapshotComponentNames.Contains(pair.Key, StringComparer.Ordinal)
                    || IsSnapshotReference(pair.Value)
                )
                .Select(pair => pair.Key),
        ];

        int removed = keysToRemove.Count;

        foreach (string key in keysToRemove)
        {
            jsonObject.Remove(key);
        }

        foreach ((string _, JsonNode? value) in jsonObject)
        {
            if (value is not null)
            {
                removed += RemoveSnapshotArtifacts(value);
            }
        }

        return removed;
    }

    /// <summary>
    /// Drops the parameter references an operation lists, then recurses. Walked back to front so a
    /// removal does not shift the indexes still to be examined.
    /// </summary>
    private static int RemoveSnapshotArtifactsFromArray(JsonArray jsonArray)
    {
        int removed = 0;

        for (int index = jsonArray.Count - 1; index >= 0; index--)
        {
            if (IsSnapshotReference(jsonArray[index]))
            {
                jsonArray.RemoveAt(index);
                removed++;
            }
        }

        foreach (JsonNode? item in jsonArray)
        {
            if (item is not null)
            {
                removed += RemoveSnapshotArtifacts(item);
            }
        }

        return removed;
    }

    private static bool IsSnapshotReference(JsonNode? node) =>
        node is JsonObject jsonObject
        && jsonObject["$ref"] is JsonValue referenceValue
        && referenceValue.TryGetValue(out string? reference)
        && _snapshotComponentReferences.Contains(reference, StringComparer.Ordinal);

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>
/// Hash-neutrality of the snapshot OpenAPI contract across the full pinned Data Standard 5.2 set: the
/// core package with the TPDM, Sample, and Homograph extensions, which is what the file-based intake
/// path serves and what the bundled core-plus-TPDM path is a subset of.
/// </summary>
[TestFixture]
public class Given_the_packaged_DataStandard52_core_TPDM_Sample_and_Homograph_ApiSchema_snapshot_contract
    : SnapshotOpenApiHashNeutralityTests
{
    /// <inheritdoc />
    protected override string CorePackageRootMetadataKey => "DataStandard52ApiSchemaPackageRoot";

    /// <inheritdoc />
    protected override IReadOnlyList<string> ExtensionPackageIds =>
        [
            "EdFi.DataStandard52.TPDM.ApiSchema",
            "EdFi.DataStandard52.Sample.ApiSchema",
            "EdFi.DataStandard52.Homograph.ApiSchema",
        ];
}

/// <summary>
/// Hash-neutrality of the snapshot OpenAPI contract across the full pinned Data Standard 6.1 set: the
/// core package with the Sample and Homograph extensions. Data Standard 6.1 folds TPDM into core, so
/// there is no TPDM package to include and the core covers a materially larger surface than 5.2's.
/// </summary>
[TestFixture]
public class Given_the_packaged_DataStandard61_core_Sample_and_Homograph_ApiSchema_snapshot_contract
    : SnapshotOpenApiHashNeutralityTests
{
    /// <inheritdoc />
    protected override string CorePackageRootMetadataKey => "DataStandard61ApiSchemaPackageRoot";

    /// <inheritdoc />
    protected override IReadOnlyList<string> ExtensionPackageIds =>
        ["EdFi.DataStandard61.Sample.ApiSchema", "EdFi.DataStandard61.Homograph.ApiSchema"];
}
