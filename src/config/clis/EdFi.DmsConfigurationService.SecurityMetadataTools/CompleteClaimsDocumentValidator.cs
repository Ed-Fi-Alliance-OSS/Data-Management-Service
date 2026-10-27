// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Claims;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools;

internal enum CompleteClaimsFailureKind
{
    InvalidJson,
    Schema,
    InvalidCatalogName,
    DuplicateCatalogName,
    UndeclaredClaimSet,
    DuplicateClaimName,
}

// Keep original values and locations structured. Command diagnostics sanitize them when rendered.
internal sealed record CompleteClaimsFailure(
    CompleteClaimsFailureKind Kind,
    string Message,
    IReadOnlyList<string> Paths,
    string Value = ""
);

internal abstract record CompleteClaimsDocumentValidationResult
{
    internal sealed record Success(
        ClaimsDocument Document,
        IReadOnlyList<string> ClaimSetNames,
        IReadOnlyList<Claim> Hierarchy
    ) : CompleteClaimsDocumentValidationResult;

    internal sealed record Failure(IReadOnlyList<CompleteClaimsFailure> Failures)
        : CompleteClaimsDocumentValidationResult;
}

/// <summary>
/// CLI-only integrity rules for complete documents used by offline resolution.
/// CMS loading and ODS conversion continue to use shared schema validation alone.
/// </summary>
internal static class CompleteClaimsDocumentValidator
{
    private static readonly ClaimsValidator _schemaValidator = new(NullLogger<ClaimsValidator>.Instance);

    internal static CompleteClaimsDocumentValidationResult Validate(string json)
    {
        JsonNode document;
        try
        {
            if (JsonNode.Parse(json) is not { } parsed)
            {
                return new CompleteClaimsDocumentValidationResult.Failure([
                    new(
                        CompleteClaimsFailureKind.Schema,
                        "A complete claims document must be an object.",
                        ["$"]
                    ),
                ]);
            }

            document = parsed;
        }
        catch (JsonException exception)
        {
            return new CompleteClaimsDocumentValidationResult.Failure([
                new(CompleteClaimsFailureKind.InvalidJson, exception.Message, [exception.Path ?? "$"]),
            ]);
        }

        List<ClaimsValidationFailure> schemaFailures = _schemaValidator.Validate(document);
        if (schemaFailures.Count > 0)
        {
            return new CompleteClaimsDocumentValidationResult.Failure(
                schemaFailures
                    .SelectMany(failure =>
                        failure.FailureMessages.Select(message => new CompleteClaimsFailure(
                            CompleteClaimsFailureKind.Schema,
                            message,
                            [failure.FailurePath.Value]
                        ))
                    )
                    .ToArray()
            );
        }

        // The shared schema guarantees these collections and every value read below.
        JsonArray catalog = document["claimSets"]!.AsArray();
        JsonArray hierarchy = document["claimsHierarchy"]!.AsArray();
        List<CompleteClaimsFailure> failures = [];
        List<string> claimSetNames = [];
        Dictionary<string, string> catalogPaths = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> claimPaths = new(StringComparer.Ordinal);

        for (int index = 0; index < catalog.Count; index++)
        {
            string name = catalog[index]!["claimSetName"]!.GetValue<string>();
            string path = $"$.claimSets[{index}].claimSetName";
            claimSetNames.Add(name);

            if (!ClaimSetNameValidator.IsValid(name))
            {
                failures.Add(
                    new(
                        CompleteClaimsFailureKind.InvalidCatalogName,
                        $"Claim set name '{name}' must contain 1 to {ValidationConstants.ClaimSetNameMaxLength} characters and no whitespace.",
                        [path],
                        name
                    )
                );
            }

            if (!catalogPaths.TryAdd(name, path))
            {
                failures.Add(
                    new(
                        CompleteClaimsFailureKind.DuplicateCatalogName,
                        $"Duplicate catalog claim set '{name}'.",
                        [catalogPaths[name], path],
                        name
                    )
                );
            }
        }

        ValidateHierarchy(hierarchy, "$.claimsHierarchy");
        if (failures.Count > 0)
        {
            return new CompleteClaimsDocumentValidationResult.Failure(failures);
        }

        return new CompleteClaimsDocumentValidationResult.Success(
            new ClaimsDocument(catalog, hierarchy),
            claimSetNames,
            hierarchy.Deserialize<List<Claim>>()!
        );

        void ValidateHierarchy(JsonArray claims, string collectionPath)
        {
            for (int index = 0; index < claims.Count; index++)
            {
                JsonNode claim = claims[index]!;
                string path = $"{collectionPath}[{index}]";
                string name = claim["name"]!.GetValue<string>();
                string namePath = $"{path}.name";
                if (!claimPaths.TryAdd(name, namePath))
                {
                    failures.Add(
                        new(
                            CompleteClaimsFailureKind.DuplicateClaimName,
                            $"Duplicate hierarchy claim '{name}'.",
                            [claimPaths[name], namePath],
                            name
                        )
                    );
                }

                if (claim["claimSets"] is JsonArray references)
                {
                    for (int referenceIndex = 0; referenceIndex < references.Count; referenceIndex++)
                    {
                        string referenceName = references[referenceIndex]!["name"]!.GetValue<string>();
                        if (!catalogPaths.ContainsKey(referenceName))
                        {
                            failures.Add(
                                new(
                                    CompleteClaimsFailureKind.UndeclaredClaimSet,
                                    $"Hierarchy claim '{name}' references undeclared claim set '{referenceName}'.",
                                    [$"{path}.claimSets[{referenceIndex}].name"],
                                    referenceName
                                )
                            );
                        }
                    }
                }

                if (claim["claims"] is JsonArray children)
                {
                    ValidateHierarchy(children, $"{path}.claims");
                }
            }
        }
    }
}
