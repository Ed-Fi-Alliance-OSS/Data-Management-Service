// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Provisioning;

/// <summary>
/// Represents a row from dms.ResourceKey as stored in the database.
/// Columns: ResourceKeyId (PK), ProjectName, ResourceName, ResourceVersion.
/// </summary>
public sealed record ResourceKeyRow(
    short ResourceKeyId,
    string ProjectName,
    string ResourceName,
    string ResourceVersion
);

/// <summary>
/// Represents a row from dms.SchemaComponent as stored in the database.
/// Columns: ProjectEndpointName (part of PK), ProjectName, ProjectVersion, IsExtensionProject.
/// Note: EffectiveSchemaHash is the other part of the PK but is used as a query filter, not a comparison field.
/// </summary>
public sealed record SchemaComponentRow(
    string ProjectEndpointName,
    string ProjectName,
    string ProjectVersion,
    bool IsExtensionProject
);

/// <summary>
/// Centralizes row-level seed-data comparison logic shared by all database provisioners.
/// Compares actual database rows against expected seed data and throws
/// <see cref="InvalidOperationException"/> with a detailed diff report on mismatch.
/// </summary>
public static class SeedValidator
{
    private const int MaxRowsPerSection = 20;

    /// <summary>
    /// Compares actual ResourceKey rows from the database against expected entries.
    /// Throws <see cref="InvalidOperationException"/> with a row-level diff report on mismatch.
    /// </summary>
    public static void ValidateResourceKeysOrThrow(
        IReadOnlyList<ResourceKeyRow> actualRows,
        IReadOnlyList<ResourceKeyEntry> expectedKeys,
        ILogger logger
    )
    {
        var actualByKey = actualRows.ToDictionary(r => r.ResourceKeyId);
        var expectedByKey = expectedKeys.ToDictionary(
            e => e.ResourceKeyId,
            e => new ResourceKeyRow(
                e.ResourceKeyId,
                e.Resource.ProjectName,
                e.Resource.ResourceName,
                e.ResourceVersion
            )
        );

        var missingKeys = new List<string>();
        var modifiedDetails = new List<string>();

        foreach (var (id, expected) in expectedByKey)
        {
            if (!actualByKey.TryGetValue(id, out var actual))
            {
                missingKeys.Add(id.ToString());
            }
            else
            {
                var diff = GetResourceKeyDiff(expected, actual);
                if (diff is not null)
                {
                    modifiedDetails.Add($"ResourceKeyId={id}: {diff}");
                }
            }
        }

        var unexpectedKeys = actualByKey
            .Keys.Where(id => !expectedByKey.ContainsKey(id))
            .Select(id => id.ToString())
            .ToList();

        if (missingKeys.Count == 0 && unexpectedKeys.Count == 0 && modifiedDetails.Count == 0)
        {
            logger.LogInformation("Preflight ResourceKey seed validation passed");
            return;
        }

        var report = BuildDiffReport("ResourceKey", missingKeys, unexpectedKeys, modifiedDetails);
        throw new InvalidOperationException(report);
    }

    /// <summary>
    /// Compares actual SchemaComponent rows from the database against expected entries.
    /// Throws <see cref="InvalidOperationException"/> with a row-level diff report on mismatch.
    /// </summary>
    public static void ValidateSchemaComponentsOrThrow(
        IReadOnlyList<SchemaComponentRow> actualRows,
        IReadOnlyList<SchemaComponentInfo> expectedComponents,
        ILogger logger
    )
    {
        var actualByKey = actualRows.ToDictionary(r => r.ProjectEndpointName, StringComparer.Ordinal);
        var expectedByKey = expectedComponents.ToDictionary(
            e => e.ProjectEndpointName,
            e => new SchemaComponentRow(
                e.ProjectEndpointName,
                e.ProjectName,
                e.ProjectVersion,
                e.IsExtensionProject
            ),
            StringComparer.Ordinal
        );

        var missingKeys = new List<string>();
        var modifiedDetails = new List<string>();

        foreach (var (name, expected) in expectedByKey)
        {
            if (!actualByKey.TryGetValue(name, out var actual))
            {
                missingKeys.Add(Sanitize(name));
            }
            else
            {
                var diff = GetSchemaComponentDiff(expected, actual);
                if (diff is not null)
                {
                    modifiedDetails.Add($"ProjectEndpointName={Sanitize(name)}: {diff}");
                }
            }
        }

        var unexpectedKeys = actualByKey
            .Keys.Where(name => !expectedByKey.ContainsKey(name))
            .Select(name => Sanitize(name))
            .ToList();

        if (missingKeys.Count == 0 && unexpectedKeys.Count == 0 && modifiedDetails.Count == 0)
        {
            logger.LogInformation("Preflight SchemaComponent seed validation passed");
            return;
        }

        var report = BuildDiffReport("SchemaComponent", missingKeys, unexpectedKeys, modifiedDetails);
        throw new InvalidOperationException(report);
    }

    private static string BuildDiffReport(
        string tableName,
        IReadOnlyList<string> missingKeys,
        IReadOnlyList<string> unexpectedKeys,
        IReadOnlyList<string> modifiedDetails
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Seed data mismatch in dms.{tableName}:");

        if (missingKeys.Count > 0)
        {
            var displayed = missingKeys.Take(MaxRowsPerSection);
            sb.AppendLine($"  Missing rows (expected but not in database): [{string.Join(", ", displayed)}]");
            if (missingKeys.Count > MaxRowsPerSection)
            {
                sb.AppendLine($"    ... and {missingKeys.Count - MaxRowsPerSection} more");
            }
        }

        if (unexpectedKeys.Count > 0)
        {
            var displayed = unexpectedKeys.Take(MaxRowsPerSection);
            sb.AppendLine(
                $"  Unexpected rows (in database but not expected): [{string.Join(", ", displayed)}]"
            );
            if (unexpectedKeys.Count > MaxRowsPerSection)
            {
                sb.AppendLine($"    ... and {unexpectedKeys.Count - MaxRowsPerSection} more");
            }
        }

        if (modifiedDetails.Count > 0)
        {
            sb.AppendLine("  Modified rows:");
            foreach (var detail in modifiedDetails.Take(MaxRowsPerSection))
            {
                sb.AppendLine($"    {detail}");
            }
            if (modifiedDetails.Count > MaxRowsPerSection)
            {
                sb.AppendLine($"    ... and {modifiedDetails.Count - MaxRowsPerSection} more");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string? GetResourceKeyDiff(ResourceKeyRow expected, ResourceKeyRow actual)
    {
        var diffs = new List<string>();
        if (!string.Equals(expected.ProjectName, actual.ProjectName, StringComparison.Ordinal))
        {
            diffs.Add(
                $"ProjectName expected='{Sanitize(expected.ProjectName)}' actual='{Sanitize(actual.ProjectName)}'"
            );
        }
        if (!string.Equals(expected.ResourceName, actual.ResourceName, StringComparison.Ordinal))
        {
            diffs.Add(
                $"ResourceName expected='{Sanitize(expected.ResourceName)}' actual='{Sanitize(actual.ResourceName)}'"
            );
        }
        if (!string.Equals(expected.ResourceVersion, actual.ResourceVersion, StringComparison.Ordinal))
        {
            diffs.Add(
                $"ResourceVersion expected='{Sanitize(expected.ResourceVersion)}' actual='{Sanitize(actual.ResourceVersion)}'"
            );
        }
        return diffs.Count > 0 ? string.Join(", ", diffs) : null;
    }

    private static string? GetSchemaComponentDiff(SchemaComponentRow expected, SchemaComponentRow actual)
    {
        var diffs = new List<string>();
        if (!string.Equals(expected.ProjectName, actual.ProjectName, StringComparison.Ordinal))
        {
            diffs.Add(
                $"ProjectName expected='{Sanitize(expected.ProjectName)}' actual='{Sanitize(actual.ProjectName)}'"
            );
        }
        if (!string.Equals(expected.ProjectVersion, actual.ProjectVersion, StringComparison.Ordinal))
        {
            diffs.Add(
                $"ProjectVersion expected='{Sanitize(expected.ProjectVersion)}' actual='{Sanitize(actual.ProjectVersion)}'"
            );
        }
        if (expected.IsExtensionProject != actual.IsExtensionProject)
        {
            diffs.Add(
                $"IsExtensionProject expected='{expected.IsExtensionProject}' actual='{actual.IsExtensionProject}'"
            );
        }
        return diffs.Count > 0 ? string.Join(", ", diffs) : null;
    }

    private static string Sanitize(string? input) => LoggingSanitizer.SanitizeForLogging(input);
}
