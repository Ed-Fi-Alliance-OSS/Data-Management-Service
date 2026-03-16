// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// Validates that the database's dms.ResourceKey contents match the expected
/// resource key seed. Uses a fast path (count + hash comparison) and falls back
/// to a slow path (row-by-row diff) on mismatch.
/// </summary>
internal sealed class ResourceKeyValidator(
    IResourceKeyRowReader resourceKeyRowReader,
    ILogger<ResourceKeyValidator> logger
) : IResourceKeyValidator
{
    private const int MaxRowsPerSection = 20;

    public async Task<ResourceKeyValidationResult> ValidateAsync(
        DatabaseFingerprint dbFingerprint,
        short expectedResourceKeyCount,
        ImmutableArray<byte> expectedResourceKeySeedHash,
        IReadOnlyList<ResourceKeyRow> expectedResourceKeysInIdOrder,
        string connectionString,
        CancellationToken cancellationToken = default
    )
    {
        // Fast path: compare count and hash
        if (
            dbFingerprint.ResourceKeyCount == expectedResourceKeyCount
            && dbFingerprint.ResourceKeySeedHash.AsSpan().SequenceEqual(expectedResourceKeySeedHash.AsSpan())
        )
        {
            return new ResourceKeyValidationResult.ValidationSuccess();
        }

        // Slow path: read actual rows and diff
        logger.LogDebug("Resource key fingerprint mismatch detected, performing row-level validation");
        var actualRows = await resourceKeyRowReader.ReadResourceKeyRowsAsync(
            connectionString,
            cancellationToken
        );
        var diffReport = BuildDiffReport(actualRows, expectedResourceKeysInIdOrder);
        return new ResourceKeyValidationResult.ValidationFailure(diffReport);
    }

    private static string BuildDiffReport(
        IReadOnlyList<ResourceKeyRow> actualRows,
        IReadOnlyList<ResourceKeyRow> expectedKeys
    )
    {
        var actualById = new Dictionary<short, ResourceKeyRow>();
        foreach (var row in actualRows)
        {
            actualById.TryAdd(row.ResourceKeyId, row);
        }

        var missingKeys = new List<string>();
        var modifiedDetails = new List<string>();

        foreach (var expected in expectedKeys)
        {
            if (!actualById.TryGetValue(expected.ResourceKeyId, out var actual))
            {
                missingKeys.Add(FormatRowTuple(expected));
            }
            else
            {
                var diff = GetResourceKeyDiff(expected, actual);
                if (diff is not null)
                {
                    modifiedDetails.Add($"ResourceKeyId/{expected.ResourceKeyId}: {diff}");
                }
            }
        }

        // Find extra rows: in DB but not in expected
        var expectedIds = new HashSet<short>(expectedKeys.Select(e => e.ResourceKeyId));
        var unexpectedKeys = actualById
            .Where(kvp => !expectedIds.Contains(kvp.Key))
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => FormatRowTuple(kvp.Value))
            .ToList();

        return FormatDiffReport(missingKeys, unexpectedKeys, modifiedDetails);
    }

    private static string? GetResourceKeyDiff(ResourceKeyRow expected, ResourceKeyRow actual)
    {
        var diffs = new List<string>();

        if (!string.Equals(expected.ProjectName, actual.ProjectName, StringComparison.Ordinal))
        {
            diffs.Add(
                $"ProjectName expected/{Sanitize(expected.ProjectName)}/ actual/{Sanitize(actual.ProjectName)}/"
            );
        }
        if (!string.Equals(expected.ResourceName, actual.ResourceName, StringComparison.Ordinal))
        {
            diffs.Add(
                $"ResourceName expected/{Sanitize(expected.ResourceName)}/ actual/{Sanitize(actual.ResourceName)}/"
            );
        }
        if (!string.Equals(expected.ResourceVersion, actual.ResourceVersion, StringComparison.Ordinal))
        {
            diffs.Add(
                $"ResourceVersion expected/{Sanitize(expected.ResourceVersion)}/ actual/{Sanitize(actual.ResourceVersion)}/"
            );
        }

        return diffs.Count > 0 ? string.Join(", ", diffs) : null;
    }

    private static string FormatRowTuple(ResourceKeyRow row) =>
        $"({row.ResourceKeyId}, {Sanitize(row.ProjectName)}, {Sanitize(row.ResourceName)}, {Sanitize(row.ResourceVersion)})";

    private static string Sanitize(string? input) => LoggingSanitizer.SanitizeForLogging(input);

    private static string FormatDiffReport(
        IReadOnlyList<string> missingKeys,
        IReadOnlyList<string> unexpectedKeys,
        IReadOnlyList<string> modifiedDetails
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine("Seed data mismatch in dms.ResourceKey:");

        if (missingKeys.Count > 0)
        {
            var displayed = missingKeys.Take(MaxRowsPerSection);
            sb.AppendLine($"  Missing rows - expected but not in database: {string.Join(" ", displayed)}");
            if (missingKeys.Count > MaxRowsPerSection)
            {
                sb.AppendLine($"    ... and {missingKeys.Count - MaxRowsPerSection} more");
            }
        }

        if (unexpectedKeys.Count > 0)
        {
            var displayed = unexpectedKeys.Take(MaxRowsPerSection);
            sb.AppendLine($"  Unexpected rows - in database but not expected: {string.Join(" ", displayed)}");
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
}
