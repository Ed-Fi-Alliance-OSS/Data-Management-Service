// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.Tests.Common;

public static class MaterializedDocumentFixtureCatalog
{
    public const string RepositoryRelativeRoot =
        "src/dms/backend/Fixtures/document-cache/materialized-documents";

    private const string CurrentFixtureVersion = "materialized-document-fixture-v1";
    private const string LastModifiedDateFormat = "yyyy-MM-ddTHH:mm:ss'Z'";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static IReadOnlyList<MaterializedDocumentFixture> LoadAll(string startDirectory)
    {
        var fixtureRoot = FixturePathResolver.ResolveRepositoryRelativePath(
            startDirectory,
            RepositoryRelativeRoot
        );

        if (!Directory.Exists(fixtureRoot))
        {
            throw new DirectoryNotFoundException(
                $"Materialized document fixture root not found: {fixtureRoot}"
            );
        }

        return Directory
            .EnumerateDirectories(fixtureRoot)
            .Where(directory => File.Exists(Path.Combine(directory, "fixture.json")))
            .Order(StringComparer.Ordinal)
            .Select(LoadFromCaseDirectory)
            .ToArray();
    }

    public static MaterializedDocumentFixture LoadCase(string startDirectory, string caseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseName);

        var fixtureRoot = FixturePathResolver.ResolveRepositoryRelativePath(
            startDirectory,
            RepositoryRelativeRoot
        );

        return LoadFromCaseDirectory(Path.Combine(fixtureRoot, caseName));
    }

    public static MaterializedDocumentFixture LoadFromCaseDirectory(string caseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseDirectory);

        var resolvedCaseDirectory = Path.GetFullPath(caseDirectory);
        var manifestPath = Path.Combine(resolvedCaseDirectory, "fixture.json");
        var manifest = ReadJsonFile<MaterializedDocumentFixtureManifest>(manifestPath);
        ValidateManifest(manifest, manifestPath, resolvedCaseDirectory);

        var sourceSetupPath = ResolveFixtureJsonPath(
            resolvedCaseDirectory,
            manifest.SourceSetupPath,
            nameof(MaterializedDocumentFixtureManifest.SourceSetupPath)
        );
        var expectedCacheRowPath = manifest.ExpectedCacheRowPath is null
            ? null
            : ResolveFixtureJsonPath(
                resolvedCaseDirectory,
                manifest.ExpectedCacheRowPath,
                nameof(MaterializedDocumentFixtureManifest.ExpectedCacheRowPath)
            );
        var expectedStreamEtagPath = manifest.ExpectedStreamEtagPath is null
            ? null
            : ResolveFixtureJsonPath(
                resolvedCaseDirectory,
                manifest.ExpectedStreamEtagPath,
                nameof(MaterializedDocumentFixtureManifest.ExpectedStreamEtagPath)
            );
        var expectedPublicCdcDocumentPath = manifest.ExpectedPublicCdcDocumentPath is null
            ? null
            : ResolveFixtureJsonPath(
                resolvedCaseDirectory,
                manifest.ExpectedPublicCdcDocumentPath,
                nameof(MaterializedDocumentFixtureManifest.ExpectedPublicCdcDocumentPath)
            );
        var expectedProjectionFailurePath = manifest.ExpectedProjectionFailurePath is null
            ? null
            : ResolveFixtureJsonPath(
                resolvedCaseDirectory,
                manifest.ExpectedProjectionFailurePath,
                nameof(MaterializedDocumentFixtureManifest.ExpectedProjectionFailurePath)
            );

        var sourceSetup = ReadJsonFile<MaterializedDocumentSourceSetup>(sourceSetupPath);
        var expectedCacheRow = expectedCacheRowPath is null
            ? null
            : ReadJsonFile<MaterializedDocumentCacheRow>(expectedCacheRowPath);
        var streamEtagExpectation = expectedStreamEtagPath is null
            ? null
            : ReadJsonFile<MaterializedDocumentStreamEtagExpectation>(expectedStreamEtagPath);
        var expectedPublicCdcDocument = expectedPublicCdcDocumentPath is null
            ? null
            : ReadJsonFile<MaterializedDocumentPublicCdcDocument>(expectedPublicCdcDocumentPath);
        var expectedProjectionFailure = expectedProjectionFailurePath is null
            ? null
            : ReadJsonFile<MaterializedDocumentProjectionFailureExpectation>(expectedProjectionFailurePath);

        ValidateSourceSetup(sourceSetup, sourceSetupPath);
        if (expectedCacheRow is not null && streamEtagExpectation is not null)
        {
            ValidateExpectedCacheRow(expectedCacheRow, expectedCacheRowPath!);
            ValidateStreamEtagExpectation(streamEtagExpectation);
            ValidateStreamEtagConsistency(expectedCacheRow, streamEtagExpectation, expectedStreamEtagPath!);
            ValidatePublicCdcDocument(
                expectedCacheRow,
                streamEtagExpectation.StreamEtag,
                expectedPublicCdcDocument,
                expectedPublicCdcDocumentPath
            );
        }

        if (expectedProjectionFailure is not null)
        {
            ValidateProjectionFailureExpectation(expectedProjectionFailure, expectedProjectionFailurePath!);
        }

        return new(
            resolvedCaseDirectory,
            manifest,
            sourceSetup,
            expectedCacheRow,
            streamEtagExpectation?.StreamEtag,
            expectedPublicCdcDocument,
            expectedProjectionFailure
        );
    }

    private static void ValidateManifest(
        MaterializedDocumentFixtureManifest manifest,
        string manifestPath,
        string caseDirectory
    )
    {
        if (manifest.FixtureVersion != CurrentFixtureVersion)
        {
            throw new InvalidOperationException(
                $"Fixture manifest '{manifestPath}' must declare fixtureVersion '{CurrentFixtureVersion}'."
            );
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.CaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.SourceSetupPath);

        var hasSuccessExpectation =
            !string.IsNullOrWhiteSpace(manifest.ExpectedCacheRowPath)
            || !string.IsNullOrWhiteSpace(manifest.ExpectedStreamEtagPath);
        var hasCompleteSuccessExpectation =
            !string.IsNullOrWhiteSpace(manifest.ExpectedCacheRowPath)
            && !string.IsNullOrWhiteSpace(manifest.ExpectedStreamEtagPath);
        var hasProjectionFailureExpectation = !string.IsNullOrWhiteSpace(
            manifest.ExpectedProjectionFailurePath
        );

        if (hasSuccessExpectation && !hasCompleteSuccessExpectation)
        {
            throw new InvalidOperationException(
                $"Fixture manifest '{manifestPath}' must pair expectedCacheRowPath with expectedStreamEtagPath."
            );
        }

        if (hasCompleteSuccessExpectation == hasProjectionFailureExpectation)
        {
            throw new InvalidOperationException(
                $"Fixture manifest '{manifestPath}' must declare exactly one success or projection-failure expectation."
            );
        }

        if (
            !hasCompleteSuccessExpectation
            && !string.IsNullOrWhiteSpace(manifest.ExpectedPublicCdcDocumentPath)
        )
        {
            throw new InvalidOperationException(
                $"Fixture manifest '{manifestPath}' cannot declare expectedPublicCdcDocumentPath without a cache-row expectation."
            );
        }

        if (manifest.CoverageTags is not null)
        {
            foreach (var coverageTag in manifest.CoverageTags)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(coverageTag);
            }
        }

        if (Path.GetFileName(caseDirectory) != manifest.CaseName)
        {
            throw new InvalidOperationException(
                $"Fixture manifest '{manifestPath}' caseName must match its directory name."
            );
        }
    }

    private static void ValidateSourceSetup(MaterializedDocumentSourceSetup sourceSetup, string path)
    {
        if (
            sourceSetup.Documents is null
            || sourceSetup.Descriptors is null
            || sourceSetup.ConcreteRootRows is null
            || sourceSetup.ChildRows is null
            || sourceSetup.ExtensionRows is null
            || sourceSetup.ReferentialIdentityRows is null
        )
        {
            throw new InvalidOperationException(
                $"Source setup '{path}' must declare all row category arrays."
            );
        }

        foreach (var document in sourceSetup.Documents)
        {
            if (document.DocumentId <= 0)
            {
                throw new InvalidOperationException($"Source setup '{path}' has an invalid DocumentId.");
            }

            if (!Guid.TryParse(document.DocumentUuid, out _))
            {
                throw new InvalidOperationException($"Source setup '{path}' has an invalid DocumentUuid.");
            }

            if (document.ResourceKeyId <= 0)
            {
                throw new InvalidOperationException($"Source setup '{path}' has an invalid ResourceKeyId.");
            }

            if (document.ContentVersion <= 0)
            {
                throw new InvalidOperationException($"Source setup '{path}' has an invalid ContentVersion.");
            }
        }

        foreach (
            var row in sourceSetup
                .ConcreteRootRows.Concat(sourceSetup.ChildRows)
                .Concat(sourceSetup.ExtensionRows)
        )
        {
            ValidateTableRow(row, path);
        }

        foreach (var row in sourceSetup.Descriptors)
        {
            if (row.DocumentId <= 0 || row.ResourceKeyId <= 0)
            {
                throw new InvalidOperationException($"Source setup '{path}' has an invalid descriptor row.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(row.Namespace);
            ArgumentException.ThrowIfNullOrWhiteSpace(row.CodeValue);
            ArgumentException.ThrowIfNullOrWhiteSpace(row.ShortDescription);

            if (row.Values is null)
            {
                throw new InvalidOperationException(
                    $"Source setup '{path}' has a descriptor row without values."
                );
            }
        }

        foreach (var row in sourceSetup.ReferentialIdentityRows)
        {
            if (
                !Guid.TryParse(row.ReferentialId, out _)
                || row.DocumentId <= 0
                || row.ResourceKeyId <= 0
                || row.Values is null
            )
            {
                throw new InvalidOperationException(
                    $"Source setup '{path}' has an invalid referential identity row."
                );
            }
        }
    }

    private static void ValidateExpectedCacheRow(MaterializedDocumentCacheRow expectedCacheRow, string path)
    {
        if (expectedCacheRow.DocumentId <= 0)
        {
            throw new InvalidOperationException($"Expected cache row '{path}' has an invalid DocumentId.");
        }

        if (!Guid.TryParse(expectedCacheRow.DocumentUuid, out _))
        {
            throw new InvalidOperationException($"Expected cache row '{path}' has an invalid DocumentUuid.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCacheRow.ProjectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCacheRow.ResourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCacheRow.ResourceVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCacheRow.StreamEtag);

        if (expectedCacheRow.ContentVersion <= 0)
        {
            throw new InvalidOperationException(
                $"Expected cache row '{path}' has an invalid ContentVersion."
            );
        }

        if (expectedCacheRow.DocumentJson is null)
        {
            throw new InvalidOperationException($"Expected cache row '{path}' must include documentJson.");
        }

        if (expectedCacheRow.DocumentJson.ContainsKey("_etag"))
        {
            throw new InvalidOperationException(
                $"Expected cache row '{path}' documentJson must not contain _etag."
            );
        }

        if (expectedCacheRow.DocumentJson["id"]?.GetValue<string>() != expectedCacheRow.DocumentUuid)
        {
            throw new InvalidOperationException(
                $"Expected cache row '{path}' documentJson.id must match documentUuid."
            );
        }

        if (
            expectedCacheRow.DocumentJson["_lastModifiedDate"]?.GetValue<string>()
            != FormatLastModifiedDate(expectedCacheRow.LastModifiedAt)
        )
        {
            throw new InvalidOperationException(
                $"Expected cache row '{path}' documentJson._lastModifiedDate must match lastModifiedAt at whole-second UTC precision."
            );
        }
    }

    private static void ValidateStreamEtagExpectation(MaterializedDocumentStreamEtagExpectation expectation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectation.StreamEtag);
    }

    private static void ValidateProjectionFailureExpectation(
        MaterializedDocumentProjectionFailureExpectation expectation,
        string path
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectation.Reason);

        if (expectation.DocumentId <= 0 || expectation.ResourceKeyId <= 0)
        {
            throw new InvalidOperationException(
                $"Expected projection failure '{path}' has invalid document or resource-key metadata."
            );
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(expectation.ProjectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectation.ResourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectation.ResourceVersion);

        if (expectation.DiagnosticMetadata is null)
        {
            throw new InvalidOperationException(
                $"Expected projection failure '{path}' must include diagnosticMetadata."
            );
        }

        if (
            expectation.DiagnosticMetadata.ContainsKey("documentJson")
            || expectation.DiagnosticMetadata.ContainsKey("authorization")
        )
        {
            throw new InvalidOperationException(
                $"Expected projection failure '{path}' diagnosticMetadata must stay bounded and sanitized."
            );
        }
    }

    private static void ValidateStreamEtagConsistency(
        MaterializedDocumentCacheRow expectedCacheRow,
        MaterializedDocumentStreamEtagExpectation expectation,
        string streamEtagPath
    )
    {
        if (expectedCacheRow.StreamEtag != expectation.StreamEtag)
        {
            throw new InvalidOperationException(
                $"Expected stream ETag '{streamEtagPath}' must match expected cache-row streamEtag."
            );
        }
    }

    private static void ValidatePublicCdcDocument(
        MaterializedDocumentCacheRow expectedCacheRow,
        string streamEtag,
        MaterializedDocumentPublicCdcDocument? expectedPublicCdcDocument,
        string? expectedPublicCdcDocumentPath
    )
    {
        if (expectedPublicCdcDocument is null)
        {
            return;
        }

        if (expectedPublicCdcDocument.Document is null)
        {
            throw new InvalidOperationException(
                $"Expected public CDC document '{expectedPublicCdcDocumentPath}' must include document."
            );
        }

        if (expectedPublicCdcDocument.Document["_etag"]?.GetValue<string>() != streamEtag)
        {
            throw new InvalidOperationException(
                $"Expected public CDC document '{expectedPublicCdcDocumentPath}' document._etag must match streamEtag."
            );
        }

        var cacheJsonWithEtag = CloneObject(expectedCacheRow.DocumentJson);
        cacheJsonWithEtag["_etag"] = streamEtag;

        if (!JsonNode.DeepEquals(cacheJsonWithEtag, expectedPublicCdcDocument.Document))
        {
            throw new InvalidOperationException(
                $"Expected public CDC document '{expectedPublicCdcDocumentPath}' must equal documentJson plus streamEtag as _etag."
            );
        }
    }

    private static void ValidateTableRow(MaterializedDocumentSourceTableRow row, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(row.Schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.Table);

        if (row.DocumentId <= 0 || row.Values is null)
        {
            throw new InvalidOperationException($"Source setup '{path}' has an invalid table row.");
        }
    }

    private static string ResolveFixtureJsonPath(
        string caseDirectory,
        string relativePath,
        string manifestPropertyName
    )
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException(
                $"Fixture manifest property '{manifestPropertyName}' must be a relative path."
            );
        }

        var resolvedCaseDirectory = Path.GetFullPath(caseDirectory);
        var resolvedPath = Path.GetFullPath(Path.Combine(resolvedCaseDirectory, relativePath));
        var relativeResolvedPath = Path.GetRelativePath(resolvedCaseDirectory, resolvedPath);

        if (
            relativeResolvedPath.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relativeResolvedPath)
        )
        {
            throw new InvalidOperationException(
                $"Fixture manifest property '{manifestPropertyName}' escapes the fixture case directory."
            );
        }

        if (!string.Equals(Path.GetExtension(resolvedPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Fixture manifest property '{manifestPropertyName}' must reference a JSON file."
            );
        }

        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException(
                $"Fixture manifest property '{manifestPropertyName}' references a missing file: {resolvedPath}",
                resolvedPath
            );
        }

        return resolvedPath;
    }

    private static T ReadJsonFile<T>(string path)
        where T : notnull
    {
        var content = File.ReadAllText(path);
        var root =
            JsonNode.Parse(content)
            ?? throw new InvalidOperationException($"Fixture JSON file '{path}' parsed to null.");

        if (root is not JsonObject && root is not JsonArray)
        {
            throw new InvalidOperationException(
                $"Fixture JSON file '{path}' must have an object or array root."
            );
        }

        return root.Deserialize<T>(_jsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize fixture JSON file '{path}'.");
    }

    private static JsonObject CloneObject(JsonObject source) =>
        JsonNode.Parse(source.ToJsonString(_jsonOptions))?.AsObject()
        ?? throw new InvalidOperationException("Failed to clone fixture JSON object.");

    private static string FormatLastModifiedDate(DateTimeOffset lastModifiedAt) =>
        lastModifiedAt.UtcDateTime.ToString(LastModifiedDateFormat, CultureInfo.InvariantCulture);
}

public sealed record MaterializedDocumentFixture(
    string CaseDirectory,
    MaterializedDocumentFixtureManifest Manifest,
    MaterializedDocumentSourceSetup SourceSetup,
    MaterializedDocumentCacheRow? ExpectedCacheRow,
    string? ExpectedStreamEtag,
    MaterializedDocumentPublicCdcDocument? ExpectedPublicCdcDocument,
    MaterializedDocumentProjectionFailureExpectation? ExpectedProjectionFailure
)
{
    public string CaseName => Manifest.CaseName;

    public bool HasSuccessExpectation => ExpectedCacheRow is not null;

    public bool HasProjectionFailureExpectation => ExpectedProjectionFailure is not null;
}

public sealed record MaterializedDocumentFixtureManifest(
    string FixtureVersion,
    string CaseName,
    IReadOnlyList<string>? CoverageTags,
    string SourceSetupPath,
    string? ExpectedCacheRowPath,
    string? ExpectedStreamEtagPath,
    string? ExpectedPublicCdcDocumentPath,
    string? ExpectedProjectionFailurePath
);

public sealed record MaterializedDocumentSourceSetup(
    MaterializedDocumentSourceDocument[] Documents,
    MaterializedDocumentSourceDescriptorRow[] Descriptors,
    MaterializedDocumentSourceTableRow[] ConcreteRootRows,
    MaterializedDocumentSourceTableRow[] ChildRows,
    MaterializedDocumentSourceTableRow[] ExtensionRows,
    MaterializedDocumentSourceReferentialIdentityRow[] ReferentialIdentityRows
);

public sealed record MaterializedDocumentSourceDocument(
    long DocumentId,
    string DocumentUuid,
    short ResourceKeyId,
    long ContentVersion,
    DateTimeOffset ContentLastModifiedAt
);

public sealed record MaterializedDocumentSourceTableRow(
    string Schema,
    string Table,
    long DocumentId,
    JsonObject Values
);

public sealed record MaterializedDocumentSourceDescriptorRow(
    long DocumentId,
    short ResourceKeyId,
    string Namespace,
    string CodeValue,
    string ShortDescription,
    JsonObject Values
);

public sealed record MaterializedDocumentSourceReferentialIdentityRow(
    string ReferentialId,
    long DocumentId,
    short ResourceKeyId,
    JsonObject Values
);

public sealed record MaterializedDocumentCacheRow(
    long DocumentId,
    string DocumentUuid,
    string ProjectName,
    string ResourceName,
    string ResourceVersion,
    long ContentVersion,
    DateTimeOffset LastModifiedAt,
    string StreamEtag,
    JsonObject DocumentJson
);

public sealed record MaterializedDocumentPublicCdcDocument(JsonObject Document);

public sealed record MaterializedDocumentProjectionFailureExpectation(
    string Reason,
    long DocumentId,
    short ResourceKeyId,
    string ProjectName,
    string ResourceName,
    string ResourceVersion,
    JsonObject DiagnosticMetadata
);

internal sealed record MaterializedDocumentStreamEtagExpectation(string StreamEtag);
