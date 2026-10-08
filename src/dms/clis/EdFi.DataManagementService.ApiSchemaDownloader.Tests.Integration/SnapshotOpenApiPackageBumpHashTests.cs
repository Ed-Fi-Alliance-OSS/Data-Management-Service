// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.ApiSchemaDownloader.Services;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.ApiSchemaDownloader.Tests.Unit;

/// <summary>
/// Proves the package bump that brought DMS the snapshot OpenAPI contract (DMS-1369) was OpenAPI-only,
/// by hashing the published package sets on both sides of it.
/// </summary>
/// <remarks>
/// <para>
/// The story requires the bump to be hash-neutral: no effective-schema hash movement, and so no
/// <c>apiSchemaVersion</c> bump and no churn in the DDL, plan, or mapping goldens derived from the
/// hashed schema. A hash change would mean the upstream package carried more than the OpenAPI
/// contract.
/// </para>
/// <para>
/// This is the historical proof, and it is deliberately anchored to the two versions the bump ran
/// between rather than to whatever is pinned now. 1.0.333 is the last version published before the
/// upstream snapshot work, and 1.0.334 is the version that introduced it. The pin has since moved to
/// a version carrying an unrelated and separately reviewed schema change, so comparing today's pin
/// against the pre-snapshot baseline would ask two questions at once and fail for a reason that has
/// nothing to do with the snapshot contract.
/// </para>
/// <para>
/// It complements rather than replaces the structural proof in
/// <c>SnapshotOpenApiHashNeutralityTests</c>. That fixture shows the pinned packages carry nothing the
/// hash can see once the snapshot artifacts are stripped, which holds for any future snapshot-only
/// change; this one shows that the bump which actually happened moved nothing else. Neither is
/// sufficient alone: the structural proof would pass if an unrelated schema change shipped alongside
/// the contract, because that change would sit in both of its operands.
/// </para>
/// <para>
/// This lives in the file-based intake suite because it is the only one that can read two published
/// versions: central package management pins a single version per package, so a unit test can only
/// ever see the current one.
/// </para>
/// </remarks>
public abstract class SnapshotOpenApiPackageBumpHashTests
{
    private const string FeedUrl =
        "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json";

    /// <summary>
    /// The last version published before the upstream snapshot work (DMS-1371).
    /// </summary>
    private const string PreSnapshotContractVersion = "1.0.333";

    /// <summary>
    /// The version that introduced the snapshot OpenAPI contract, adopted by <c>b0b68972c</c>.
    /// </summary>
    private const string SnapshotIntroducingContractVersion = "1.0.334";

    /// <summary>
    /// The core package of this effective schema set.
    /// </summary>
    protected abstract string CorePackageId { get; }

    /// <summary>
    /// The extension packages that complete it. DMS hashes each package as its own project in the
    /// manifest <see cref="EffectiveSchemaHashProvider" /> hashes, so a set is the unit that has to be
    /// proven rather than a core package alone.
    /// </summary>
    protected abstract IReadOnlyList<string> ExtensionPackageIds { get; }

    /// <summary>
    /// The reusable component names the upstream snapshot contract introduces. Matched by name rather
    /// than by a substring sweep, so the count stays confined to the snapshot contract.
    /// </summary>
    private static readonly string[] _snapshotComponentNames =
    [
        "Use-Snapshot",
        "SnapshotNotFound",
        "SnapshotMethodNotAllowed",
    ];

    /// <summary>
    /// The exact <c>$ref</c> targets operations use to reach those components. Matched in full, so a
    /// resource that merely has "Snapshot" in its name is never counted as snapshot contract.
    /// </summary>
    private static readonly string[] _snapshotComponentReferences =
    [
        "#/components/parameters/Use-Snapshot",
        "#/components/responses/SnapshotNotFound",
        "#/components/responses/SnapshotMethodNotAllowed",
    ];

    /// <summary>
    /// What one published package carried, kept per package so a package that contributed nothing to
    /// the proof is named rather than averaged away by the others.
    /// </summary>
    private sealed record PackagedDocumentFacts(
        string PackageId,
        int ResourceSchemaCount,
        int SnapshotArtifactCount
    );

    private sealed record PackagedSetFacts(
        string EffectiveSchemaHash,
        IReadOnlyList<PackagedDocumentFacts> Documents
    );

    private string _workspace = null!;
    private PackagedSetFacts _preSnapshot = null!;
    private PackagedSetFacts _snapshotIntroducing = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"dms1369-bump-{Guid.NewGuid()}");
        Directory.CreateDirectory(_workspace);

        IApiSchemaDownloader downloader = new Services.ApiSchemaDownloader(
            A.Fake<ILogger<Services.ApiSchemaDownloader>>()
        );

        // One version at a time. Each set is several multi-megabyte documents, and nothing below needs
        // the parsed documents once the hash and the counts have been taken.
        _preSnapshot = await LoadSetAsync(downloader, PreSnapshotContractVersion);
        _snapshotIntroducing = await LoadSetAsync(downloader, SnapshotIntroducingContractVersion);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, true);
        }
    }

    [Test]
    public void It_produces_an_identical_effective_schema_hash_across_the_snapshot_introducing_bump()
    {
        _snapshotIntroducing
            .EffectiveSchemaHash.Should()
            .Be(
                _preSnapshot.EffectiveSchemaHash,
                "adopting the snapshot OpenAPI contract must need no re-provisioning and churn no DDL, "
                    + "plan, or mapping golden; a difference here means {0} carried more than the "
                    + "snapshot contract and the bump must be investigated before it is accepted",
                SnapshotIntroducingContractVersion
            );
    }

    [Test]
    public void It_finds_no_snapshot_artifacts_in_the_pre_snapshot_packages()
    {
        // Half of what makes the hash comparison mean anything: two versions that carried the same
        // contract would hash the same and prove nothing.
        foreach (PackagedDocumentFacts document in _preSnapshot.Documents)
        {
            document
                .SnapshotArtifactCount.Should()
                .Be(
                    0,
                    "'{0}' at {1} predates the upstream snapshot contract, so it must carry none of it",
                    document.PackageId,
                    PreSnapshotContractVersion
                );
        }
    }

    [Test]
    public void It_finds_snapshot_artifacts_in_the_snapshot_introducing_packages()
    {
        // The other half. Compared per package against its resource count rather than against zero: a
        // sweep that found merely "something" would still be consistent with having missed almost all
        // of it, and neutrality proven over almost nothing is not neutrality proven.
        foreach (PackagedDocumentFacts document in _snapshotIntroducing.Documents)
        {
            document
                .SnapshotArtifactCount.Should()
                .BeGreaterThan(
                    document.ResourceSchemaCount,
                    "'{0}' at {1} publishes the snapshot contract on multiple operations of every "
                        + "resource, so its artifact count must exceed its resource count",
                    document.PackageId,
                    SnapshotIntroducingContractVersion
                );
        }
    }

    /// <summary>
    /// Downloads every package of this set at <paramref name="version" />, records what each carries,
    /// and hashes the set through the production normalizer and hash provider.
    /// </summary>
    private async Task<PackagedSetFacts> LoadSetAsync(IApiSchemaDownloader downloader, string version)
    {
        JsonNode coreNode = await DownloadRootNodeAsync(downloader, CorePackageId, version);

        List<JsonNode> extensionNodes = [];

        foreach (string extensionPackageId in ExtensionPackageIds)
        {
            extensionNodes.Add(await DownloadRootNodeAsync(downloader, extensionPackageId, version));
        }

        // Counted before normalizing, so the counts describe what the package published rather than
        // what survived a normalizer that exists to strip exactly this content.
        List<PackagedDocumentFacts> documents =
        [
            DocumentFactsOf(CorePackageId, coreNode),
            .. ExtensionPackageIds
                .Zip(extensionNodes)
                .Select(pair => DocumentFactsOf(pair.First, pair.Second)),
        ];

        ApiSchemaDocumentNodes nodes = new(coreNode, [.. extensionNodes]);

        return new PackagedSetFacts(HashOf(nodes), documents);
    }

    private async Task<JsonNode> DownloadRootNodeAsync(
        IApiSchemaDownloader downloader,
        string packageId,
        string version
    )
    {
        string packageWorkspace = Path.Combine(_workspace, $"{packageId}.{version}");
        Directory.CreateDirectory(packageWorkspace);

        string packagePath = await downloader.DownloadNuGetPackageAsync(
            packageId,
            version,
            FeedUrl,
            packageWorkspace
        );
        downloader.ExtractApiSchemaFiles(packageId, packagePath, packageWorkspace);

        string apiSchemaPath = Path.Combine(packageWorkspace, "Packages", packageId, "ApiSchema.json");
        JsonNode root =
            JsonNode.Parse(await File.ReadAllTextAsync(apiSchemaPath))
            ?? throw new InvalidOperationException($"{packageId} {version} ApiSchema.json parsed to null.");

        // The extracted copy is the largest thing on disk here and the parsed document has replaced it.
        File.Delete(apiSchemaPath);

        return root;
    }

    /// <summary>
    /// Normalizes through the production normalizer and hashes through the production hash provider,
    /// so this measures what DMS would hash rather than a restatement of it.
    /// </summary>
    private static string HashOf(ApiSchemaDocumentNodes nodes)
    {
        ApiSchemaInputNormalizer normalizer = new(NullLogger<ApiSchemaInputNormalizer>.Instance);
        ApiSchemaNormalizationResult result = normalizer.Normalize(nodes);

        if (result is not ApiSchemaNormalizationResult.SuccessResult success)
        {
            throw new InvalidOperationException(
                $"Normalizing the packaged ApiSchema set failed with {result.GetType().Name}."
            );
        }

        EffectiveSchemaHashProvider hashProvider = new(NullLogger<EffectiveSchemaHashProvider>.Instance);

        return hashProvider.ComputeHash(success.NormalizedNodes);
    }

    private static PackagedDocumentFacts DocumentFactsOf(string packageId, JsonNode rootNode)
    {
        JsonObject projectSchema =
            rootNode["projectSchema"]?.AsObject()
            ?? throw new InvalidOperationException(
                $"Packaged ApiSchema '{packageId}' is missing projectSchema."
            );

        return new PackagedDocumentFacts(
            packageId,
            ResourceSchemaCount: (projectSchema["resourceSchemas"] as JsonObject)?.Count ?? 0,
            SnapshotArtifactCount: CountSnapshotArtifacts(projectSchema)
        );
    }

    /// <summary>
    /// Counts the snapshot artifacts in the three OpenAPI payload regions, which are exactly the
    /// regions the normalizer strips before hashing.
    /// </summary>
    private static int CountSnapshotArtifacts(JsonObject projectSchema)
    {
        int found = 0;

        if (projectSchema["openApiBaseDocuments"] is JsonNode baseDocuments)
        {
            found += CountSnapshotArtifactsIn(baseDocuments);
        }

        found += CountSnapshotArtifactsInChildren(projectSchema["resourceSchemas"], "openApiFragments");
        found += CountSnapshotArtifactsInChildren(projectSchema["abstractResources"], "openApiFragment");

        return found;
    }

    private static int CountSnapshotArtifactsInChildren(JsonNode? container, string payloadPropertyName)
    {
        if (container is not JsonObject containerObject)
        {
            return 0;
        }

        int found = 0;

        foreach ((string _, JsonNode? child) in containerObject)
        {
            if (child?[payloadPropertyName] is JsonNode payload)
            {
                found += CountSnapshotArtifactsIn(payload);
            }
        }

        return found;
    }

    private static int CountSnapshotArtifactsIn(JsonNode node) =>
        node switch
        {
            JsonObject jsonObject => CountSnapshotArtifactsInObject(jsonObject),
            JsonArray jsonArray => CountSnapshotArtifactsInArray(jsonArray),
            _ => 0,
        };

    /// <summary>
    /// Counts the component declarations and the response entries that reference them, then recurses
    /// into whatever was not itself an artifact.
    /// </summary>
    private static int CountSnapshotArtifactsInObject(JsonObject jsonObject)
    {
        int found = 0;

        foreach ((string key, JsonNode? value) in jsonObject)
        {
            if (_snapshotComponentNames.Contains(key, StringComparer.Ordinal) || IsSnapshotReference(value))
            {
                found++;
                continue;
            }

            if (value is not null)
            {
                found += CountSnapshotArtifactsIn(value);
            }
        }

        return found;
    }

    /// <summary>
    /// Counts the parameter references an operation lists, then recurses.
    /// </summary>
    private static int CountSnapshotArtifactsInArray(JsonArray jsonArray)
    {
        int found = 0;

        foreach (JsonNode? item in jsonArray)
        {
            if (IsSnapshotReference(item))
            {
                found++;
                continue;
            }

            if (item is not null)
            {
                found += CountSnapshotArtifactsIn(item);
            }
        }

        return found;
    }

    private static bool IsSnapshotReference(JsonNode? node) =>
        node is JsonObject jsonObject
        && jsonObject["$ref"] is JsonValue referenceValue
        && referenceValue.TryGetValue(out string? reference)
        && _snapshotComponentReferences.Contains(reference, StringComparer.Ordinal);
}

/// <summary>
/// The snapshot-introducing bump across the full Data Standard 5.2 set: the core package with the
/// TPDM, Sample, and Homograph extensions, which is what the file-based intake path serves and what
/// the bundled core-plus-TPDM path is a subset of.
/// </summary>
[TestFixture]
public class Given_the_DataStandard52_core_TPDM_Sample_and_Homograph_snapshot_introducing_bump
    : SnapshotOpenApiPackageBumpHashTests
{
    /// <inheritdoc />
    protected override string CorePackageId => "EdFi.DataStandard52.ApiSchema";

    /// <inheritdoc />
    protected override IReadOnlyList<string> ExtensionPackageIds =>
        [
            "EdFi.DataStandard52.TPDM.ApiSchema",
            "EdFi.DataStandard52.Sample.ApiSchema",
            "EdFi.DataStandard52.Homograph.ApiSchema",
        ];
}

/// <summary>
/// The snapshot-introducing bump across the full Data Standard 6.1 set: the core package with the
/// Sample and Homograph extensions. Data Standard 6.1 folds TPDM into core, so there is no TPDM
/// package to include.
/// </summary>
[TestFixture]
public class Given_the_DataStandard61_core_Sample_and_Homograph_snapshot_introducing_bump
    : SnapshotOpenApiPackageBumpHashTests
{
    /// <inheritdoc />
    protected override string CorePackageId => "EdFi.DataStandard61.ApiSchema";

    /// <inheritdoc />
    protected override IReadOnlyList<string> ExtensionPackageIds =>
        ["EdFi.DataStandard61.Sample.ApiSchema", "EdFi.DataStandard61.Homograph.ApiSchema"];
}
