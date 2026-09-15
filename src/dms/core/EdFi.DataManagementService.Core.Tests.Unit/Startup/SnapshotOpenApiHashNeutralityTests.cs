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
/// </remarks>
public abstract class SnapshotOpenApiHashNeutralityTests
{
    /// <summary>
    /// The <c>AssemblyMetadata</c> key under which the build recorded this package's restored root.
    /// </summary>
    protected abstract string PackageRootMetadataKey { get; }

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

    private string _apiSchemaVersion = string.Empty;
    private bool _packagedOpenApiBaseDocumentsPresent;
    private int _packagedResourceSchemaCount;
    private int _packagedResourceSchemasWithOpenApiFragments;
    private int _packagedAbstractResourcesWithOpenApiFragment;
    private int _snapshotArtifactsRemoved;

    private string _normalizedJsonDigest = string.Empty;
    private string _normalizedJsonDigestWithoutSnapshotContent = string.Empty;
    private string _effectiveSchemaHash = string.Empty;
    private string _effectiveSchemaHashWithoutSnapshotContent = string.Empty;
    private string _normalizedJson = string.Empty;
    private JsonObject _normalizedProjectSchema = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        JsonNode rootNode = PackagedApiSchemaContract.LoadPackagedRootNode(PackageRootMetadataKey);

        _apiSchemaVersion = rootNode["apiSchemaVersion"]?.GetValue<string>() ?? string.Empty;
        RecordPackagedOpenApiPayloadCounts(rootNode);

        // The packaged schema as published, normalized and hashed the way startup does it.
        (_normalizedJson, _normalizedProjectSchema, _effectiveSchemaHash) = NormalizeAndHash(rootNode);
        _normalizedJsonDigest = Sha256Hex(_normalizedJson);

        // The same schema with the snapshot contract deleted from its OpenAPI payloads. Mutated in
        // place rather than on a clone: the pre-mutation state is already captured above, and a second
        // full copy of a multi-megabyte document buys nothing.
        _snapshotArtifactsRemoved = RemoveSnapshotOpenApiContent(rootNode);

        (string withoutSnapshotJson, _, _effectiveSchemaHashWithoutSnapshotContent) = NormalizeAndHash(
            rootNode
        );
        _normalizedJsonDigestWithoutSnapshotContent = Sha256Hex(withoutSnapshotJson);
    }

    [Test]
    public void It_declares_the_apiSchemaVersion_the_snapshot_contract_did_not_bump()
    {
        _apiSchemaVersion
            .Should()
            .Be(
                "1.0.0",
                "an OpenAPI-only package change must not move apiSchemaVersion; a change here means the "
                    + "package carried more than the snapshot OpenAPI contract"
            );
    }

    [Test]
    public void It_carries_snapshot_openapi_artifacts_to_neutralize()
    {
        // Compared against the resource count rather than zero. Every packaged resource carries the
        // contract on several operations, so a sweep that found merely "something" would still be
        // consistent with having missed almost all of it, and neutrality proven over almost nothing is
        // not neutrality proven. Against a pre-DMS-1371 package this finds nothing at all and fails.
        _snapshotArtifactsRemoved
            .Should()
            .BeGreaterThan(
                _packagedResourceSchemaCount,
                "the pinned package publishes the snapshot contract on multiple operations of every "
                    + "resource, so the neutralized artifact count must exceed the resource count"
            );
    }

    [Test]
    public void It_strips_every_openapi_payload_before_hashing()
    {
        _packagedOpenApiBaseDocumentsPresent
            .Should()
            .BeTrue("the packaged schema must publish base documents");
        _packagedResourceSchemasWithOpenApiFragments
            .Should()
            .Be(
                _packagedResourceSchemaCount,
                "every packaged resource schema carries OpenAPI fragments, so stripping them is not a no-op"
            );
        _packagedAbstractResourcesWithOpenApiFragment
            .Should()
            .BeGreaterThan(0, "the packaged abstract resources carry an OpenAPI fragment");

        _normalizedProjectSchema.Should().NotContainKey("openApiBaseDocuments");
        CountChildrenWithProperty(_normalizedProjectSchema["resourceSchemas"], "openApiFragments")
            .Should()
            .Be(0);
        CountChildrenWithProperty(_normalizedProjectSchema["abstractResources"], "openApiFragment")
            .Should()
            .Be(0);
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
    /// returning the normalized core document's JSON, its projectSchema, and the effective schema hash.
    /// </summary>
    private static (
        string NormalizedJson,
        JsonObject NormalizedProjectSchema,
        string EffectiveSchemaHash
    ) NormalizeAndHash(JsonNode rootNode)
    {
        ApiSchemaInputNormalizer normalizer = new(NullLogger<ApiSchemaInputNormalizer>.Instance);
        ApiSchemaNormalizationResult result = normalizer.Normalize(new ApiSchemaDocumentNodes(rootNode, []));

        if (result is not ApiSchemaNormalizationResult.SuccessResult success)
        {
            throw new InvalidOperationException(
                $"Normalizing the packaged ApiSchema failed with {result.GetType().Name}."
            );
        }

        JsonNode normalizedCore = success.NormalizedNodes.CoreApiSchemaRootNode;
        JsonObject normalizedProjectSchema =
            normalizedCore["projectSchema"]?.AsObject()
            ?? throw new InvalidOperationException("Normalized schema is missing projectSchema.");

        EffectiveSchemaHashProvider hashProvider = new(NullLogger<EffectiveSchemaHashProvider>.Instance);

        return (
            normalizedCore.ToJsonString(),
            normalizedProjectSchema,
            hashProvider.ComputeHash(success.NormalizedNodes)
        );
    }

    private void RecordPackagedOpenApiPayloadCounts(JsonNode rootNode)
    {
        JsonObject projectSchema =
            rootNode["projectSchema"]?.AsObject()
            ?? throw new InvalidOperationException("Packaged ApiSchema is missing projectSchema.");

        _packagedOpenApiBaseDocumentsPresent = projectSchema.ContainsKey("openApiBaseDocuments");
        _packagedResourceSchemaCount = (projectSchema["resourceSchemas"] as JsonObject)?.Count ?? 0;
        _packagedResourceSchemasWithOpenApiFragments = CountChildrenWithProperty(
            projectSchema["resourceSchemas"],
            "openApiFragments"
        );
        _packagedAbstractResourcesWithOpenApiFragment = CountChildrenWithProperty(
            projectSchema["abstractResources"],
            "openApiFragment"
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
    private static int RemoveSnapshotOpenApiContent(JsonNode rootNode)
    {
        if (rootNode["projectSchema"] is not JsonObject projectSchema)
        {
            return 0;
        }

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
/// Hash-neutrality of the snapshot OpenAPI contract in the pinned Data Standard 5.2 core package.
/// </summary>
[TestFixture]
public class Given_the_packaged_DataStandard52_core_ApiSchema_snapshot_contract
    : SnapshotOpenApiHashNeutralityTests
{
    /// <inheritdoc />
    protected override string PackageRootMetadataKey => "DataStandard52ApiSchemaPackageRoot";
}

/// <summary>
/// Hash-neutrality of the snapshot OpenAPI contract in the pinned Data Standard 6.1 core package.
/// Data Standard 6.1 folds TPDM into core, so this covers a materially larger served surface than 5.2.
/// </summary>
[TestFixture]
public class Given_the_packaged_DataStandard61_core_ApiSchema_snapshot_contract
    : SnapshotOpenApiHashNeutralityTests
{
    /// <inheritdoc />
    protected override string PackageRootMetadataKey => "DataStandard61ApiSchemaPackageRoot";
}
