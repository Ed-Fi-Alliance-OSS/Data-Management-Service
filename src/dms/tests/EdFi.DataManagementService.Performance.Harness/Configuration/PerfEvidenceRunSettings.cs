// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;

namespace EdFi.DataManagementService.Performance.Harness.Configuration;

/// <summary>
/// Environment-supplied settings that make a run acceptable as evidence: the validated image
/// pin, the storage caveat, and the guardrails — whether CI execution is permitted (its tmpfs
/// databases invalidate I/O measurement) and which dirty worktree paths are allowed (the
/// overlay itself and nothing else, so a contaminated subject tree cannot produce baseline
/// artifacts). A single empty prefix deliberately allows any dirty path, for smoke runs on a
/// development tree.
/// </summary>
public sealed partial record PerfEvidenceRunSettings(
    string ImageTag,
    string ImageDigest,
    string StorageNote,
    bool AllowCi,
    IReadOnlyList<string> AllowedDirtyPrefixes
)
{
    public const string DefaultAllowedDirtyPrefix =
        "src/dms/tests/EdFi.DataManagementService.Performance.Harness";

    public static PerfEvidenceRunSettings FromEnvironment() => Load(Environment.GetEnvironmentVariable);

    public static PerfEvidenceRunSettings Load(Func<string, string?> readVariable)
    {
        List<string> errors = [];

        string? Read(string name)
        {
            string? value = readVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        string? imageTag = Read(PerfEnvironmentVariables.ImageTag);
        if (imageTag is null)
        {
            errors.Add($"{PerfEnvironmentVariables.ImageTag} is required.");
        }

        string? imageDigest = Read(PerfEnvironmentVariables.ImageDigest);
        if (imageDigest is null)
        {
            errors.Add($"{PerfEnvironmentVariables.ImageDigest} is required.");
        }
        else if (!DigestRegex().IsMatch(imageDigest))
        {
            errors.Add(
                $"{PerfEnvironmentVariables.ImageDigest} must match sha256:<64 lowercase hex>; got '{imageDigest}'."
            );
        }

        string? storageNote = Read(PerfEnvironmentVariables.StorageNote);
        if (storageNote is null)
        {
            errors.Add($"{PerfEnvironmentVariables.StorageNote} is required.");
        }

        bool allowCi = false;
        string? allowCiText = Read(PerfEnvironmentVariables.AllowCi);
        if (allowCiText is not null)
        {
            if (allowCiText is not ("true" or "false"))
            {
                errors.Add(
                    $"{PerfEnvironmentVariables.AllowCi} must be 'true' or 'false'; got '{allowCiText}'."
                );
            }

            allowCi = allowCiText == "true";
        }

        IReadOnlyList<string> allowedDirtyPrefixes = [DefaultAllowedDirtyPrefix];
        string? prefixesText = readVariable(PerfEnvironmentVariables.AllowedDirtyPrefixes);
        if (!string.IsNullOrWhiteSpace(prefixesText))
        {
            allowedDirtyPrefixes = [.. prefixesText.Split(';').Select(prefix => prefix.Trim())];
        }

        if (errors.Count > 0)
        {
            throw new PerfConfigurationException(errors);
        }

        return new PerfEvidenceRunSettings(
            imageTag!,
            imageDigest!,
            storageNote!,
            allowCi,
            allowedDirtyPrefixes
        );
    }

    [GeneratedRegex("^sha256:[0-9a-f]{64}$")]
    private static partial Regex DigestRegex();
}
