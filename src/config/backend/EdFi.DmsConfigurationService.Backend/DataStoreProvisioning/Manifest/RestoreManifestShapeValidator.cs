// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;

/// <summary>
/// Validates a strictly parsed restore manifest against the version-1 contract, rule for rule and in the
/// order of the delivered <c>Assert-RestoreManifestShape</c>, and materializes the <see cref="RestoreManifest"/>.
/// Beyond the delivered rules it rejects any member that is not part of the contract, at every level (6.2
/// step 5). Every integer must be a JSON number with no fraction or exponent; every text field a JSON string;
/// a JSON null counts as absent. The first violated rule throws <see cref="RestoreManifestException"/> naming
/// the field or entry position, never a value. The element must come from
/// <see cref="TrustContract.TryParse(string, out JsonDocument?, out TrustContract.ParseFailure)"/>, so that
/// member names are unique and every string decodes.
/// </summary>
internal static partial class RestoreManifestShapeValidator
{
    private const string EngineRule = "'postgresql' or 'mssql'";

    private static readonly HashSet<string> _contractMembers = new(StringComparer.Ordinal)
    {
        "version",
        "packageId",
        "packageVersion",
        "databaseEngine",
        "templateKind",
        "dataStandardVersion",
        "contentProfile",
        "projects",
        "apiSchemaFormatVersion",
        "effectiveSchemaHash",
        "resourceKeyCount",
        "resourceKeySeedHashB64",
        "relationalMappingVersion",
        "engineVersion",
        "databaseCompatibilityLevel",
        "documentJsonColumnType",
        "inventory",
        "inventorySha256",
        "artifactFileName",
        "artifactSha256",
    };

    public static RestoreManifest Validate(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new RestoreManifestException("The restore manifest must be a JSON object.");
        }

        RejectUnknownMembers(root, _contractMembers, "The restore manifest");

        int version = RequireVersion(root);
        string packageId = RequireString(root, "packageId");
        string packageVersion = RequireString(root, "packageVersion");

        string databaseEngine = RequireString(root, "databaseEngine");
        if (databaseEngine is not (RestoreManifest.PostgresqlEngine or RestoreManifest.MssqlEngine))
        {
            throw new RestoreManifestException(
                $"The restore manifest field 'databaseEngine' must be exactly {EngineRule}."
            );
        }

        string templateKind = RequireString(root, "templateKind");
        if (
            templateKind is not (RestoreManifest.MinimalTemplateKind or RestoreManifest.PopulatedTemplateKind)
        )
        {
            throw new RestoreManifestException(
                "The restore manifest field 'templateKind' must be exactly 'Minimal' or 'Populated'."
            );
        }

        string dataStandardVersion = RequireString(root, "dataStandardVersion");

        string contentProfile = RequireString(root, "contentProfile");
        if (contentProfile != RestoreManifest.ContentProfileDmsDatastoreOnly)
        {
            throw new RestoreManifestException(
                $"The restore manifest field 'contentProfile' must be exactly '{RestoreManifest.ContentProfileDmsDatastoreOnly}'; only DMS-datastore-only template packages are eligible for restore."
            );
        }

        IReadOnlyList<string> projects = RequireProjects(root);
        string apiSchemaFormatVersion = RequireString(root, "apiSchemaFormatVersion");
        string effectiveSchemaHash = RequireSha256(root, "effectiveSchemaHash");
        int resourceKeyCount = RequireResourceKeyCount(root);
        string resourceKeySeedHashB64 = RequireSeedHash(root);
        string relationalMappingVersion = RequireString(root, "relationalMappingVersion");
        string engineVersion = RequireString(root, "engineVersion");
        int? databaseCompatibilityLevel = ReadCompatibilityLevel(root, databaseEngine);
        string documentJsonColumnType = RequireString(root, "documentJsonColumnType");
        string inventorySha256 = RequireSha256(root, "inventorySha256");

        CanonicalInventory inventory = CanonicalInventoryJson.Read(RequireMember(root, "inventory"));
        if (
            !string.Equals(
                CanonicalInventoryHash.Compute(inventory),
                inventorySha256,
                StringComparison.Ordinal
            )
        )
        {
            throw new RestoreManifestException(
                "The restore manifest is internally inconsistent: the recomputed canonical inventory hash does not match the recorded 'inventorySha256'."
            );
        }

        string artifactFileName = RequireArtifactFileName(root, databaseEngine);
        string artifactSha256 = RequireSha256(root, "artifactSha256");

        return new RestoreManifest
        {
            Version = version,
            PackageId = packageId,
            PackageVersion = packageVersion,
            DatabaseEngine = databaseEngine,
            TemplateKind = templateKind,
            DataStandardVersion = dataStandardVersion,
            ContentProfile = contentProfile,
            Projects = projects,
            ApiSchemaFormatVersion = apiSchemaFormatVersion,
            EffectiveSchemaHash = effectiveSchemaHash,
            ResourceKeyCount = resourceKeyCount,
            ResourceKeySeedHashB64 = resourceKeySeedHashB64,
            RelationalMappingVersion = relationalMappingVersion,
            EngineVersion = engineVersion,
            DatabaseCompatibilityLevel = databaseCompatibilityLevel,
            DocumentJsonColumnType = documentJsonColumnType,
            Inventory = inventory,
            InventorySha256 = inventorySha256,
            ArtifactFileName = artifactFileName,
            ArtifactSha256 = artifactSha256,
        };
    }

    /// <summary>Throws when <paramref name="element"/> declares a member outside <paramref name="contractMembers"/>; the message gives the member's position, not its name.</summary>
    internal static void RejectUnknownMembers(
        JsonElement element,
        HashSet<string> contractMembers,
        string subject
    )
    {
        int position = 0;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            position++;
            if (!contractMembers.Contains(property.Name))
            {
                throw new RestoreManifestException(
                    $"{subject} declares a member at position {position} that is not part of the version {RestoreManifest.SupportedVersion} contract."
                );
            }
        }
    }

    private static int RequireVersion(JsonElement root)
    {
        JsonElement member = RequireMember(root, "version");
        if (member.ValueKind != JsonValueKind.Number || !member.TryGetInt32(out int version))
        {
            throw new RestoreManifestException(
                "The restore manifest field 'version' must be an integer manifest format version."
            );
        }

        if (version != RestoreManifest.SupportedVersion)
        {
            throw new RestoreManifestException(
                $"The restore manifest field 'version' is unsupported; only version {RestoreManifest.SupportedVersion} is supported."
            );
        }

        return version;
    }

    private static IReadOnlyList<string> RequireProjects(JsonElement root)
    {
        JsonElement member = RequireMember(root, "projects");
        if (member.ValueKind != JsonValueKind.Array || member.GetArrayLength() == 0)
        {
            throw new RestoreManifestException(
                "The restore manifest field 'projects' must be a non-empty JSON array of project schema names."
            );
        }

        List<string> projects = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement entry in member.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString()))
            {
                throw new RestoreManifestException(
                    "The restore manifest field 'projects' contains an entry that is not a non-empty JSON string."
                );
            }

            string project = entry.GetString()!;
            if (!seen.Add(project))
            {
                throw new RestoreManifestException(
                    "The restore manifest field 'projects' contains a duplicate entry."
                );
            }

            projects.Add(project);
        }

        return projects;
    }

    private static int RequireResourceKeyCount(JsonElement root)
    {
        JsonElement member = RequireMember(root, "resourceKeyCount");
        if (member.ValueKind != JsonValueKind.Number || !member.TryGetInt32(out int count) || count < 1)
        {
            throw new RestoreManifestException(
                "The restore manifest field 'resourceKeyCount' must be a positive integer."
            );
        }

        return count;
    }

    private static string RequireSeedHash(JsonElement root)
    {
        string seedHashB64 = RequireString(root, "resourceKeySeedHashB64");
        byte[] buffer = new byte[seedHashB64.Length];
        if (!Convert.TryFromBase64String(seedHashB64, buffer, out int decodedLength))
        {
            throw new RestoreManifestException(
                "The restore manifest field 'resourceKeySeedHashB64' is not valid base64."
            );
        }

        if (decodedLength != 32)
        {
            throw new RestoreManifestException(
                "The restore manifest field 'resourceKeySeedHashB64' must decode to exactly 32 bytes (SHA-256)."
            );
        }

        return seedHashB64;
    }

    private static int? ReadCompatibilityLevel(JsonElement root, string databaseEngine)
    {
        bool present =
            root.TryGetProperty("databaseCompatibilityLevel", out JsonElement member)
            && member.ValueKind != JsonValueKind.Null;
        if (databaseEngine == RestoreManifest.MssqlEngine)
        {
            if (
                !present
                || member.ValueKind != JsonValueKind.Number
                || !member.TryGetInt32(out int level)
                || level < RestoreManifest.MinimumMssqlCompatibilityLevel
            )
            {
                throw new RestoreManifestException(
                    $"The restore manifest field 'databaseCompatibilityLevel' is required for mssql and must be an integer SQL Server compatibility level ({RestoreManifest.MinimumMssqlCompatibilityLevel} or higher)."
                );
            }

            return level;
        }

        if (present)
        {
            throw new RestoreManifestException(
                "The restore manifest field 'databaseCompatibilityLevel' must be omitted or null for postgresql."
            );
        }

        return null;
    }

    private static string RequireArtifactFileName(JsonElement root, string databaseEngine)
    {
        string artifactFileName = RequireString(root, "artifactFileName");
        if (!ArtifactFileNamePattern().IsMatch(artifactFileName))
        {
            throw new RestoreManifestException(
                "The restore manifest field 'artifactFileName' contains unsupported characters; only letters, digits, dots, dashes, and underscores are allowed."
            );
        }

        string expectedExtension = RestoreManifest.ArtifactExtensionFor(databaseEngine);
        if (!artifactFileName.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new RestoreManifestException(
                $"The restore manifest field 'artifactFileName' must end with '{expectedExtension}' for databaseEngine '{databaseEngine}'."
            );
        }

        return artifactFileName;
    }

    private static JsonElement RequireMember(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement member) || member.ValueKind == JsonValueKind.Null)
        {
            throw new RestoreManifestException($"The restore manifest is missing required field '{name}'.");
        }

        return member;
    }

    private static string RequireString(JsonElement root, string name)
    {
        JsonElement member = RequireMember(root, name);
        if (member.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(member.GetString()))
        {
            throw new RestoreManifestException(
                $"The restore manifest field '{name}' must be a non-empty JSON string."
            );
        }

        return member.GetString()!;
    }

    private static string RequireSha256(JsonElement root, string name)
    {
        string value = RequireString(root, name);
        if (!TrustContract.IsLowercaseSha256Hex(value))
        {
            throw new RestoreManifestException(
                $"The restore manifest field '{name}' must be a 64-character lowercase hex SHA-256."
            );
        }

        return value;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_.-]+\z")]
    private static partial Regex ArtifactFileNamePattern();
}
