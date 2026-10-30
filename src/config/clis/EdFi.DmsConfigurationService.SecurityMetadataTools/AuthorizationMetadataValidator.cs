// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools;

internal enum AuthorizationMetadataFailureKind
{
    InvalidJson,
    InvalidShape,
    DuplicateProperty,
    DuplicateClaimSetName,
    DuplicateAuthorizationId,
    DuplicateActionName,
    DuplicateClaimName,
    UnknownAuthorizationId,
}

// Original values stay structured; command diagnostics sanitize them at the rendering boundary.
internal sealed record AuthorizationMetadataFailure(
    AuthorizationMetadataFailureKind Kind,
    string Message,
    IReadOnlyList<string> Paths,
    string Value = ""
);

internal sealed record ValidatedResourceClaim(string Name, IReadOnlyList<ClaimSetMetadata.Action> Actions);

internal sealed record ValidatedClaimSetMetadata(
    string ClaimSetName,
    IReadOnlyList<ValidatedResourceClaim> Claims
);

internal abstract record AuthorizationMetadataValidationResult
{
    internal sealed record Success(IReadOnlyList<ValidatedClaimSetMetadata> ClaimSets)
        : AuthorizationMetadataValidationResult;

    internal sealed record Failure(IReadOnlyList<AuthorizationMetadataFailure> Failures)
        : AuthorizationMetadataValidationResult;
}

/// <summary>
/// Validates the entire received array before exposing dereferenced permissions to comparison.
/// Names and strategy sequences are preserved; IDs have meaning only inside their claim set.
/// </summary>
internal static class AuthorizationMetadataValidator
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static AuthorizationMetadataValidationResult Validate(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            return new AuthorizationMetadataValidationResult.Failure([
                new(AuthorizationMetadataFailureKind.InvalidJson, exception.Message, [exception.Path ?? "$"]),
            ]);
        }

        using (document)
        {
            List<AuthorizationMetadataFailure> failures = [];
            ValidateArray(document.RootElement, "$", ValidateClaimSet, failures);
            if (failures.Count > 0)
            {
                return new AuthorizationMetadataValidationResult.Failure(failures);
            }

            // Explicit shape validation prevents omitted/null values from becoming empty permissions.
            var metadata = document.RootElement.Deserialize<List<ClaimSetMetadata>>(_jsonOptions)!;
            List<ValidatedClaimSetMetadata> validated = [];
            Dictionary<string, string> catalogPaths = new(StringComparer.OrdinalIgnoreCase);
            for (int setIndex = 0; setIndex < metadata.Count; setIndex++)
            {
                var set = metadata[setIndex];
                string setPath = $"$[{setIndex}]";
                CheckDuplicate(
                    catalogPaths,
                    set.ClaimSetName,
                    $"{setPath}.claimSetName",
                    AuthorizationMetadataFailureKind.DuplicateClaimSetName,
                    "claim set name",
                    failures
                );

                Dictionary<int, ClaimSetMetadata.Authorization> authorizations = [];
                Dictionary<int, string> authorizationPaths = [];
                for (
                    int authorizationIndex = 0;
                    authorizationIndex < set.Authorizations.Count;
                    authorizationIndex++
                )
                {
                    var authorization = set.Authorizations[authorizationIndex];
                    string authorizationPath = $"{setPath}.authorizations[{authorizationIndex}]";
                    CheckDuplicate(
                        authorizationPaths,
                        authorization.Id,
                        $"{authorizationPath}.id",
                        AuthorizationMetadataFailureKind.DuplicateAuthorizationId,
                        "authorization ID",
                        failures
                    );
                    authorizations.TryAdd(authorization.Id, authorization);

                    Dictionary<string, string> actionPaths = new(StringComparer.Ordinal);
                    for (int actionIndex = 0; actionIndex < authorization.Actions.Length; actionIndex++)
                    {
                        CheckDuplicate(
                            actionPaths,
                            authorization.Actions[actionIndex].Name,
                            $"{authorizationPath}.actions[{actionIndex}].name",
                            AuthorizationMetadataFailureKind.DuplicateActionName,
                            "action name",
                            failures
                        );
                    }
                }

                List<ValidatedResourceClaim> claims = [];
                Dictionary<string, string> claimPaths = new(StringComparer.Ordinal);
                for (int claimIndex = 0; claimIndex < set.Claims.Count; claimIndex++)
                {
                    var claim = set.Claims[claimIndex];
                    string claimPath = $"{setPath}.claims[{claimIndex}]";
                    CheckDuplicate(
                        claimPaths,
                        claim.Name,
                        $"{claimPath}.name",
                        AuthorizationMetadataFailureKind.DuplicateClaimName,
                        "resource claim URI",
                        failures
                    );
                    if (!authorizations.TryGetValue(claim.AuthorizationId, out var authorization))
                    {
                        string id = claim.AuthorizationId.ToString(CultureInfo.InvariantCulture);
                        failures.Add(
                            new(
                                AuthorizationMetadataFailureKind.UnknownAuthorizationId,
                                $"Resource claim '{claim.Name}' in claim set '{set.ClaimSetName}' references unknown authorization ID '{id}'.",
                                [$"{claimPath}.authorizationId"],
                                id
                            )
                        );
                        continue;
                    }

                    claims.Add(new(claim.Name, authorization.Actions));
                }

                validated.Add(new(set.ClaimSetName, claims));
            }

            return failures.Count > 0
                ? new AuthorizationMetadataValidationResult.Failure(failures)
                : new AuthorizationMetadataValidationResult.Success(validated);
        }
    }

    private static void CheckDuplicate<TKey>(
        Dictionary<TKey, string> paths,
        TKey value,
        string path,
        AuthorizationMetadataFailureKind kind,
        string description,
        List<AuthorizationMetadataFailure> failures
    )
        where TKey : notnull
    {
        if (!paths.TryAdd(value, path))
        {
            string text = Convert.ToString(value, CultureInfo.InvariantCulture)!;
            failures.Add(new(kind, $"Duplicate {description} '{text}'.", [paths[value], path], text));
        }
    }

    private static void ValidateClaimSet(
        JsonElement set,
        string path,
        List<AuthorizationMetadataFailure> failures
    )
    {
        if (!ValidateObject(set, path, failures))
        {
            return;
        }

        ValidateProperty(set, "claimSetName", path, ValidateString, failures);
        ValidateProperty(
            set,
            "claims",
            path,
            (array, arrayPath, errors) => ValidateArray(array, arrayPath, ValidateClaim, errors),
            failures
        );
        ValidateProperty(
            set,
            "authorizations",
            path,
            (array, arrayPath, errors) => ValidateArray(array, arrayPath, ValidateAuthorization, errors),
            failures
        );
    }

    private static void ValidateClaim(
        JsonElement claim,
        string path,
        List<AuthorizationMetadataFailure> failures
    )
    {
        if (!ValidateObject(claim, path, failures))
        {
            return;
        }

        ValidateProperty(claim, "name", path, ValidateString, failures);
        ValidateProperty(claim, "authorizationId", path, ValidateInteger, failures);
    }

    private static void ValidateAuthorization(
        JsonElement authorization,
        string path,
        List<AuthorizationMetadataFailure> failures
    )
    {
        if (!ValidateObject(authorization, path, failures))
        {
            return;
        }

        ValidateProperty(authorization, "id", path, ValidateInteger, failures);
        ValidateProperty(
            authorization,
            "actions",
            path,
            (array, arrayPath, errors) => ValidateArray(array, arrayPath, ValidateAction, errors),
            failures
        );
    }

    private static void ValidateAction(
        JsonElement action,
        string path,
        List<AuthorizationMetadataFailure> failures
    )
    {
        if (!ValidateObject(action, path, failures))
        {
            return;
        }

        ValidateProperty(action, "name", path, ValidateString, failures);
        ValidateProperty(
            action,
            "authorizationStrategies",
            path,
            (array, arrayPath, errors) => ValidateArray(array, arrayPath, ValidateStrategy, errors),
            failures
        );
    }

    private static void ValidateStrategy(
        JsonElement strategy,
        string path,
        List<AuthorizationMetadataFailure> failures
    )
    {
        if (ValidateObject(strategy, path, failures))
        {
            ValidateProperty(strategy, "name", path, ValidateString, failures);
        }
    }

    private static bool ValidateObject(
        JsonElement element,
        string path,
        List<AuthorizationMetadataFailure> failures
    )
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            InvalidShape(path, "an object", failures);
            return false;
        }

        HashSet<string> properties = new(StringComparer.Ordinal);
        foreach (string propertyName in element.EnumerateObject().Select(property => property.Name))
        {
            if (!properties.Add(propertyName))
            {
                failures.Add(
                    new(
                        AuthorizationMetadataFailureKind.DuplicateProperty,
                        $"Duplicate JSON property '{propertyName}'.",
                        [path],
                        propertyName
                    )
                );
            }
        }

        return true;
    }

    private static void ValidateProperty(
        JsonElement parent,
        string name,
        string path,
        Action<JsonElement, string, List<AuthorizationMetadataFailure>> validate,
        List<AuthorizationMetadataFailure> failures
    )
    {
        string propertyPath = $"{path}.{name}";
        if (!parent.TryGetProperty(name, out var property))
        {
            failures.Add(
                new(
                    AuthorizationMetadataFailureKind.InvalidShape,
                    $"Required property '{name}' is missing.",
                    [propertyPath]
                )
            );
            return;
        }

        validate(property, propertyPath, failures);
    }

    private static void ValidateArray(
        JsonElement element,
        string path,
        Action<JsonElement, string, List<AuthorizationMetadataFailure>> validateItem,
        List<AuthorizationMetadataFailure> failures
    )
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            InvalidShape(path, "an array", failures);
            return;
        }

        int index = 0;
        foreach (var item in element.EnumerateArray())
        {
            validateItem(item, $"{path}[{index++}]", failures);
        }
    }

    private static void ValidateString(
        JsonElement element,
        string path,
        List<AuthorizationMetadataFailure> failures
    )
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            InvalidShape(path, "a string", failures);
        }
    }

    private static void ValidateInteger(
        JsonElement element,
        string path,
        List<AuthorizationMetadataFailure> failures
    )
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out _))
        {
            InvalidShape(path, "a 32-bit integer", failures);
        }
    }

    private static void InvalidShape(
        string path,
        string expected,
        List<AuthorizationMetadataFailure> failures
    ) => failures.Add(new(AuthorizationMetadataFailureKind.InvalidShape, $"Expected {expected}.", [path]));
}
