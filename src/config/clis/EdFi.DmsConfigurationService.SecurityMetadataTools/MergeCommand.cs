// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using EdFi.DmsConfigurationService.Claims;
using EdFi.DmsConfigurationService.Claims.Composition;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools;

internal static class MergeCommand
{
    internal static Command Create(TextWriter error) =>
        Create(
            error,
            version => BundledClaimsProvider.Load(version),
            new ClaimsFragmentComposer(NullLogger.Instance).ComposeClaimsFromFragments
        );

    // Keep the command pipeline testable with the shared, compact composition fixtures.
    internal static Command Create(
        TextWriter error,
        Func<string, ClaimsLoadResult> loadBase,
        Func<ClaimsDocument, string, ClaimsCompositionResult> compose
    )
    {
        var command = new Command(
            "merge",
            "Merge recursive *-claims.json fragments with a bundled base into a complete claims document, offline. "
                + "Incomplete composition or invalid policy is rejected. Exit codes: 0 = success, 1 = invalid input or execution failure. "
                + "Failures use stderr and preserve the destination."
        );
        command.Options.Add(
            new Option<string>("--fragments")
            {
                Description = "Directory containing claim-set fragments (may be empty).",
                Required = true,
            }
        );
        command.Options.Add(
            new Option<string>("--output")
            {
                Description = "Path for the complete JSON claims document.",
                Required = true,
            }
        );
        command.Options.Add(
            new Option<string>("--data-standard-version")
            {
                Description = "Bundled Data Standard version: 5.2 (default) or 6.1.",
            }
        );
        command.Options.Add(
            new Option<bool>("--force")
            {
                Description = "Replace existing output only after complete composition and validation.",
            }
        );
        command.SetAction(result =>
        {
            int exitCode = 0;
            int executionCode = CommandDiagnostics.Execute(
                error,
                "Merge",
                () =>
                {
                    var loaded = loadBase(result.GetValue<string>("--data-standard-version") ?? "");
                    if (ReportFailures(loaded, error))
                    {
                        exitCode = 1;
                        return;
                    }

                    var composed = compose(loaded.Nodes!, result.GetRequiredValue<string>("--fragments"));
                    foreach (var diagnostic in composed.Diagnostics.Where(d => d.IsIncomplete))
                    {
                        CommandDiagnostics.ExecutionFailure(
                            error,
                            "Merge",
                            $"{diagnostic.Code}: source '{diagnostic.SourcePath}', claim '{diagnostic.ClaimName}', "
                                + $"claim set '{diagnostic.ClaimSetName}', path '{diagnostic.JsonPath}'."
                        );
                    }
                    bool loadFailed = ReportFailures(composed.LoadResult, error);
                    if (!composed.IsComplete || loadFailed)
                    {
                        exitCode = 1;
                        return;
                    }

                    ClaimsDocument document = composed.LoadResult.Nodes!;
                    string json = new JsonObject
                    {
                        ["claimSets"] = document.ClaimSetsNode.DeepClone(),
                        ["claimsHierarchy"] = document.ClaimsHierarchyNode.DeepClone(),
                    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    if (
                        CompleteClaimsDocumentValidator.Validate(json)
                        is CompleteClaimsDocumentValidationResult.Failure invalid
                    )
                    {
                        foreach (var failure in invalid.Failures)
                        {
                            CommandDiagnostics.ExecutionFailure(
                                error,
                                "Merge",
                                $"{failure.Kind} at {string.Join(", ", failure.Paths)}: {failure.Message}"
                            );
                        }
                        exitCode = 1;
                        return;
                    }

                    ProtectedFileOutput.Write(
                        result.GetRequiredValue<string>("--output"),
                        json,
                        result.GetValue<bool>("--force")
                    );
                }
            );
            return executionCode == 0 ? exitCode : executionCode;
        });
        return command;
    }

    private static bool ReportFailures(ClaimsLoadResult result, TextWriter error)
    {
        foreach (var failure in result.Failures)
        {
            CommandDiagnostics.ExecutionFailure(
                error,
                "Merge",
                $"{failure.FailureType} at '{failure.Path}': {failure.Message}"
            );
        }
        if (result.Nodes is null && result.Failures.Count == 0)
        {
            CommandDiagnostics.ExecutionFailure(error, "Merge", "No claims document was produced.");
        }
        return result.Failures.Count > 0 || result.Nodes is null;
    }
}
