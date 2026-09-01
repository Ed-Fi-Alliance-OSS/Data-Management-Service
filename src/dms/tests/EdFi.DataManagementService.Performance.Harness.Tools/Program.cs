// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Results;

if (args is ["validate-results", var resultsDirectory])
{
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
}

if (args is ["assemble-threshold-results", var assembledResultsDirectory])
{
    DocumentCacheQualificationThresholdResultGenerator.AssembleProviderEvidenceAndWriteThresholdResults(
        assembledResultsDirectory
    );
    await Console.Out.WriteLineAsync(
        $"DocumentCache qualification threshold results assembled: {Path.GetFullPath(assembledResultsDirectory)}"
    );
    return 0;
}

if (args is ["assemble-threshold-results", var ticketedResultsDirectory, var durableBaselineCursorTicket])
{
    DocumentCacheQualificationThresholdResultGenerator.AssembleProviderEvidenceAndWriteThresholdResults(
        ticketedResultsDirectory,
        durableBaselineCursorTicket
    );
    await Console.Out.WriteLineAsync(
        $"DocumentCache qualification threshold results assembled: {Path.GetFullPath(ticketedResultsDirectory)}"
    );
    return 0;
}

await Console.Error.WriteLineAsync(
    "Usage: EdFi.DataManagementService.Performance.Harness.Tools validate-results <results-directory>\n"
        + "       EdFi.DataManagementService.Performance.Harness.Tools assemble-threshold-results <results-directory> [durable-baseline-cursor-ticket]"
);
return 2;
