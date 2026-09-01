// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Results;

if (args is not ["validate-results", var resultsDirectory])
{
    await Console.Error.WriteLineAsync(
        "Usage: EdFi.DataManagementService.Performance.Harness.Tools validate-results <results-directory>"
    );
    return 2;
}

IReadOnlyList<DocumentCacheQualificationValidationFailure> failures =
    DocumentCacheQualificationArtifactValidator.ValidateDirectory(resultsDirectory);

if (failures.Count > 0)
{
    await Console.Error.WriteLineAsync("DocumentCache qualification result validation failed:");
    foreach (DocumentCacheQualificationValidationFailure failure in failures)
    {
        await Console.Error.WriteLineAsync($"- {failure}");
    }

    return 1;
}

await Console.Out.WriteLineAsync(
    $"DocumentCache qualification result validation passed: {Path.GetFullPath(resultsDirectory)}"
);
return 0;
