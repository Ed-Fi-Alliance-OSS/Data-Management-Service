// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using EdFi.DmsConfigurationService.Claims.AuthorizationMetadata;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools;

internal static class ResolveCommand
{
    internal static Command Create(TextWriter error)
    {
        var command = new Command(
            "resolve",
            "Resolve a complete claims document offline to the /v3/authorizationMetadata JSON array. "
                + "The output is resolved metadata, not a complete claims document for upload. "
                + "Exit codes: 0 = success, 1 = invalid input or execution failure. "
                + "JSON is written only to the output file; failures use stderr and preserve the destination."
        );
        command.Options.Add(
            new Option<string>("--input")
            {
                Description =
                    "Path to the complete JSON claims document, including its full catalog and hierarchy.",
                Required = true,
            }
        );
        command.Options.Add(
            new Option<string>("--output")
            {
                Description = "Path for the resolved authorization metadata JSON array.",
                Required = true,
            }
        );
        command.Options.Add(
            new Option<bool>("--force")
            {
                Description = "Replace existing output only after successful validation and resolution.",
            }
        );
        command.SetAction(result =>
        {
            int exitCode = 0;
            int executionCode = CommandDiagnostics.Execute(
                error,
                "Resolution",
                () =>
                {
                    var validation = CompleteClaimsDocumentValidator.Validate(
                        File.ReadAllText(result.GetRequiredValue<string>("--input"))
                    );
                    if (validation is CompleteClaimsDocumentValidationResult.Failure invalid)
                    {
                        foreach (var failure in invalid.Failures)
                        {
                            CommandDiagnostics.ExecutionFailure(
                                error,
                                "Resolution",
                                $"{failure.Kind} at {string.Join(", ", failure.Paths)}: {failure.Message}"
                            );
                        }
                        exitCode = 1;
                        return;
                    }

                    var document = (CompleteClaimsDocumentValidationResult.Success)validation;
                    var resolved = AuthorizationMetadataResolver.Resolve(
                        document.ClaimSetNames,
                        document.Hierarchy
                    );
                    string json = ResolvedMetadataSerializer.Serialize(resolved);
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
}
