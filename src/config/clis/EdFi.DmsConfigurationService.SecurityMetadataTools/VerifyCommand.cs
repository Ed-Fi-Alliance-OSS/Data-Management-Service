// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.Text.Json;
using EdFi.DmsConfigurationService.Claims.AuthorizationMetadata;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools;

internal static class VerifyCommand
{
    internal static Command Create(TextWriter output, TextWriter error, Func<HttpClient> createHttpClient)
    {
        var command = new Command(
            "verify",
            "Compare a complete claims document with CMS authorization metadata returned by this request. "
                + "Does not establish cache freshness or live Ed-Fi API enforcement. "
                + "Exit codes: 0 = match, 2 = mismatch, 1 = invalid input or execution failure. "
                + "Match and mismatch reports use stdout; failures use stderr."
        );
        command.Options.Add(
            new Option<string>("--input")
            {
                Description = "Path to the complete JSON claims document.",
                Required = true,
            }
        );
        command.Options.Add(
            new Option<string>("--cms-url")
            {
                Description = "CMS base HTTP(S) URL, including any deployment prefix.",
                Required = true,
            }
        );
        command.Options.Add(
            new Option<string>("--access-token-env")
            {
                Description = "Name of the environment variable containing an already-issued bearer token.",
                Required = true,
            }
        );
        command.Options.Add(
            new Option<string>("--tenant") { Description = "Optional tenant name sent in the Tenant header." }
        );
        command.SetAction(
            (result, cancellationToken) =>
                ExecuteAsync(
                    result.GetRequiredValue<string>("--input"),
                    result.GetRequiredValue<string>("--cms-url"),
                    result.GetRequiredValue<string>("--access-token-env"),
                    result.GetValue<string>("--tenant") ?? "",
                    output,
                    error,
                    createHttpClient,
                    cancellationToken
                )
        );
        return command;
    }

    private static async Task<int> ExecuteAsync(
        string inputPath,
        string cmsUrl,
        string accessTokenEnvironmentVariable,
        string tenant,
        TextWriter output,
        TextWriter error,
        Func<HttpClient> createHttpClient,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var validation = CompleteClaimsDocumentValidator.Validate(
                await File.ReadAllTextAsync(inputPath, cancellationToken)
            );
            if (validation is CompleteClaimsDocumentValidationResult.Failure invalid)
            {
                foreach (var failure in invalid.Failures)
                {
                    CommandDiagnostics.ExecutionFailure(
                        error,
                        "Verification",
                        $"{failure.Kind} at {string.Join(", ", failure.Paths)}: {failure.Message}"
                    );
                }
                return 1;
            }

            var document = (CompleteClaimsDocumentValidationResult.Success)validation;
            var resolved = AuthorizationMetadataResolver.Resolve(document.ClaimSetNames, document.Hierarchy);
            // Resolver output is trusted. Dereference each set directly without serializing and reparsing it.
            var expected = new AuthorizationMetadataValidationResult.Success(
                resolved
                    .ClaimSets.Select(set =>
                    {
                        var actionsById = set.Authorizations.ToDictionary(authorization => authorization.Id);
                        return new ValidatedClaimSetMetadata(
                            set.ClaimSetName,
                            set.Claims.Select(claim => new ValidatedResourceClaim(
                                    claim.Name,
                                    actionsById[claim.AuthorizationId].Actions
                                ))
                                .ToArray()
                        );
                    })
                    .ToArray()
            );

            using var httpClient = createHttpClient();
            var response = await new CmsAuthorizationMetadataClient(httpClient).GetAsync(
                cmsUrl,
                accessTokenEnvironmentVariable,
                tenant,
                cancellationToken
            );
            if (response is CmsAuthorizationMetadataResult.Failure failureResponse)
            {
                return CommandDiagnostics.ExecutionFailure(error, "Verification", failureResponse.Message);
            }

            var actual = ((CmsAuthorizationMetadataResult.Success)response).Metadata;
            var comparison = EffectivePermissionsComparer.Compare(expected, actual);
            await output.WriteLineAsync(VerificationReportFormatter.Format(comparison));
            return comparison.IsMatch ? 0 : 2;
        }
        catch (OperationCanceledException)
        {
            return CommandDiagnostics.ExecutionFailure(error, "Verification", "Verification was canceled.");
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or NotSupportedException
                        or InvalidOperationException
                        or JsonException
            )
        {
            return CommandDiagnostics.ExecutionFailure(error, "Verification", exception.Message);
        }
    }
}
