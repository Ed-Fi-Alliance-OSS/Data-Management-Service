// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core.Response;

internal static class RelationshipAuthorizationProblemDetails
{
    private const string AuthorizationType = "urn:ed-fi:api:security:authorization";
    private const string ElementUninitializedType =
        $"{AuthorizationType}:relationships:invalid-data:element-uninitialized";
    private const string ElementRequiredType =
        $"{AuthorizationType}:relationships:access-denied:element-required";
    private const string BaseDetail = "Access to the requested data could not be authorized.";

    public static RelationshipAuthorizationProblemDetailsResult Format(
        RelationshipAuthorizationFailure relationshipFailure
    )
    {
        ArgumentNullException.ThrowIfNull(relationshipFailure);

        var orderedFailures = OrderedFailures(relationshipFailure).ToArray();
        var selectedCase = SelectCase(orderedFailures);
        var selectedFailures = orderedFailures
            .Where(failure => IsSelectedFailureKind(failure.Subject.FailureKind, selectedCase))
            .ToArray();

        return selectedCase switch
        {
            RelationshipAuthorizationProblemDetailsCase.StoredValueNull => FormatStoredValueNull(
                selectedFailures
            ),
            RelationshipAuthorizationProblemDetailsCase.ProposedValueMissing => FormatProposedValueMissing(
                selectedFailures
            ),
            RelationshipAuthorizationProblemDetailsCase.NoRelationship => FormatNoRelationship(
                relationshipFailure,
                selectedFailures.Length == 0 ? orderedFailures : selectedFailures
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(relationshipFailure),
                selectedCase,
                "Unsupported relationship authorization ProblemDetails case."
            ),
        };
    }

    private static RelationshipAuthorizationProblemDetailsResult FormatStoredValueNull(
        IReadOnlyList<SelectedRelationshipAuthorizationFailure> selectedFailures
    )
    {
        var readableNames = SelectReadableNames(selectedFailures);
        var hints = SelectHints(selectedFailures);

        return new RelationshipAuthorizationProblemDetailsResult(
            ElementUninitializedType,
            AppendHints(FormatStoredValueNullDetail(readableNames), hints),
            SelectStoredValueNullErrors(selectedFailures)
        );
    }

    private static RelationshipAuthorizationProblemDetailsResult FormatProposedValueMissing(
        IReadOnlyList<SelectedRelationshipAuthorizationFailure> selectedFailures
    )
    {
        var readableNames = SelectReadableNames(selectedFailures);
        var hints = SelectHints(selectedFailures);

        return new RelationshipAuthorizationProblemDetailsResult(
            ElementRequiredType,
            AppendHints(FormatProposedValueMissingDetail(readableNames), hints),
            []
        );
    }

    private static RelationshipAuthorizationProblemDetailsResult FormatNoRelationship(
        RelationshipAuthorizationFailure relationshipFailure,
        IReadOnlyList<SelectedRelationshipAuthorizationFailure> selectedFailures
    )
    {
        var readableNames = SelectReadableNames(selectedFailures);
        var (claimNoun, claimDisplay) = FormatClaimEducationOrganizationIds(relationshipFailure);
        var hints = SelectHints(selectedFailures);

        return new RelationshipAuthorizationProblemDetailsResult(
            AuthorizationType,
            AppendHints(BaseDetail, hints),
            [FormatNoRelationshipError(claimNoun, claimDisplay, readableNames)]
        );
    }

    private static RelationshipAuthorizationProblemDetailsCase SelectCase(
        IReadOnlyList<SelectedRelationshipAuthorizationFailure> orderedFailures
    )
    {
        if (
            orderedFailures.Any(static failure =>
                failure.Subject.FailureKind == RelationshipAuthorizationSubjectFailureKind.StoredValueNull
            )
        )
        {
            return RelationshipAuthorizationProblemDetailsCase.StoredValueNull;
        }

        if (
            orderedFailures.Any(static failure =>
                failure.Subject.FailureKind
                == RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing
            )
        )
        {
            return RelationshipAuthorizationProblemDetailsCase.ProposedValueMissing;
        }

        return RelationshipAuthorizationProblemDetailsCase.NoRelationship;
    }

    private static bool IsSelectedFailureKind(
        RelationshipAuthorizationSubjectFailureKind failureKind,
        RelationshipAuthorizationProblemDetailsCase selectedCase
    ) =>
        selectedCase switch
        {
            RelationshipAuthorizationProblemDetailsCase.StoredValueNull => failureKind
                == RelationshipAuthorizationSubjectFailureKind.StoredValueNull,
            RelationshipAuthorizationProblemDetailsCase.ProposedValueMissing => failureKind
                == RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing,
            RelationshipAuthorizationProblemDetailsCase.NoRelationship => failureKind
                == RelationshipAuthorizationSubjectFailureKind.NoRelationship,
            _ => throw new ArgumentOutOfRangeException(
                nameof(selectedCase),
                selectedCase,
                "Unsupported relationship authorization ProblemDetails case."
            ),
        };

    private static IEnumerable<SelectedRelationshipAuthorizationFailure> OrderedFailures(
        RelationshipAuthorizationFailure relationshipFailure
    ) =>
        relationshipFailure
            .FailedStrategies.OrderBy(static strategy => strategy.ConfiguredStrategyIndex)
            .ThenBy(static strategy => strategy.RelationshipLocalOrder)
            .SelectMany(static strategy =>
                strategy
                    .FailedSubjects.OrderBy(static subject => subject.SubjectIndex)
                    .Select(subject => new SelectedRelationshipAuthorizationFailure(strategy, subject))
            );

    private static string FormatStoredValueNullDetail(IReadOnlyList<string> readableNames) =>
        readableNames.Count == 1
            ? $"{BaseDetail} The existing '{readableNames[0]}' value is required for authorization purposes."
            : $"{BaseDetail} The existing values of one or more of the following properties are required for authorization purposes: {FormatReadableNameList(readableNames)}.";

    private static string FormatProposedValueMissingDetail(IReadOnlyList<string> readableNames) =>
        readableNames.Count == 1
            ? $"{BaseDetail} The '{readableNames[0]}' value is required for authorization purposes."
            : $"{BaseDetail} The values of one or more of the following properties are required for authorization purposes: {FormatReadableNameList(readableNames)}.";

    private static string FormatNoRelationshipError(
        string claimNoun,
        string claimDisplay,
        IReadOnlyList<string> readableNames
    )
    {
        if (readableNames.Count == 0)
        {
            return "No relationships have been established between the caller's education organization id "
                + $"{claimNoun} ({claimDisplay}) and the requested resource.";
        }

        if (readableNames.Count == 1)
        {
            return "No relationships have been established between the caller's education organization id "
                + $"{claimNoun} ({claimDisplay}) and the resource item's '{readableNames[0]}' value.";
        }

        return "No relationships have been established between the caller's education organization id "
            + $"{claimNoun} ({claimDisplay}) and one or more of the following properties of the resource item: "
            + $"{FormatReadableNameList(readableNames)}.";
    }

    private static string[] SelectStoredValueNullErrors(
        IReadOnlyList<SelectedRelationshipAuthorizationFailure> selectedFailures
    ) =>
        [
            .. selectedFailures
                .GroupBy(static failure =>
                    (failure.Strategy.ConfiguredStrategyIndex, failure.Strategy.RelationshipLocalOrder)
                )
                .OrderBy(static group => group.Key.ConfiguredStrategyIndex)
                .ThenBy(static group => group.Key.RelationshipLocalOrder)
                .Select(static group =>
                    "The existing resource item is inaccessible to clients using the "
                    + $"'{group.First().Strategy.StrategyName}' authorization strategy."
                ),
        ];

    private static string[] SelectReadableNames(
        IReadOnlyList<SelectedRelationshipAuthorizationFailure> selectedFailures
    ) =>
        [
            .. selectedFailures
                .SelectMany(static failure => SelectReadableNames(failure.Subject))
                .Where(static readableName => !string.IsNullOrWhiteSpace(readableName))
                .Distinct(StringComparer.Ordinal),
        ];

    private static string[] SelectHints(
        IReadOnlyList<SelectedRelationshipAuthorizationFailure> selectedFailures
    )
    {
        HashSet<string> seenHints = new(StringComparer.Ordinal);
        List<string> hints = [];

        foreach (var failure in selectedFailures)
        {
            foreach (var hint in SelectHints(failure).Select(NormalizeHint))
            {
                if (string.IsNullOrWhiteSpace(hint) || !seenHints.Add(hint))
                {
                    continue;
                }

                hints.Add(hint);
            }
        }

        return [.. hints];
    }

    private static IEnumerable<string?> SelectHints(SelectedRelationshipAuthorizationFailure failure)
    {
        yield return failure.Subject.AuthObject.FailureHint;
    }

    private static string NormalizeHint(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return string.Empty;
        }

        var normalizedHint = hint.Trim();

        return normalizedHint.StartsWith("Hint:", StringComparison.Ordinal)
            ? normalizedHint["Hint:".Length..].TrimStart()
            : normalizedHint;
    }

    private static string AppendHints(string detail, IReadOnlyList<string> hints) =>
        hints.Count == 0 ? detail : $"{detail} Hint: {string.Join(" ", hints)}";

    private static IEnumerable<string> SelectReadableNames(RelationshipAuthorizationFailedSubject subject)
    {
        if (subject.SecurableElements.Length == 0)
        {
            yield return SelectReadableName(subject.RootBinding);
            yield break;
        }

        foreach (var securableElement in subject.SecurableElements)
        {
            yield return SelectReadableName(securableElement);
        }
    }

    private static string SelectReadableName(RelationshipAuthorizationSecurableElement securableElement) =>
        !string.IsNullOrWhiteSpace(securableElement.ReadableName)
            ? securableElement.ReadableName
            : DeriveReadableNameFromJsonPath(securableElement.JsonPath);

    private static string SelectReadableName(RelationshipAuthorizationRootBinding rootBinding) =>
        rootBinding.ColumnName;

    private static string DeriveReadableNameFromJsonPath(string jsonPath)
    {
        var lastSegment = jsonPath
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(static segment => segment != "$");

        if (string.IsNullOrWhiteSpace(lastSegment))
        {
            return jsonPath;
        }

        var arrayMarkerIndex = lastSegment.IndexOf('[', StringComparison.Ordinal);
        if (arrayMarkerIndex > 0)
        {
            lastSegment = lastSegment[..arrayMarkerIndex];
        }

        return lastSegment.Length switch
        {
            0 => jsonPath,
            1 => lastSegment.ToUpperInvariant(),
            _ => string.Concat(lastSegment[..1].ToUpperInvariant(), lastSegment.AsSpan(1)),
        };
    }

    private static (string ClaimNoun, string ClaimDisplay) FormatClaimEducationOrganizationIds(
        RelationshipAuthorizationFailure relationshipFailure
    )
    {
        var claimEducationOrganizationIds = relationshipFailure
            .ClaimEducationOrganizationIds.Select(static claim => claim.Value)
            .Distinct()
            .Order()
            .ToArray();

        if (claimEducationOrganizationIds.Length == 0)
        {
            return ("claims", "none");
        }

        var displayedClaims = claimEducationOrganizationIds.Take(5).Select(static id => $"'{id}'").ToList();

        if (claimEducationOrganizationIds.Length > 5)
        {
            displayedClaims.Add("...");
        }

        return (
            claimEducationOrganizationIds.Length == 1 ? "claim" : "claims",
            string.Join(", ", displayedClaims)
        );
    }

    private static string FormatReadableNameList(IReadOnlyList<string> readableNames) =>
        string.Join(", ", readableNames.Select(static readableName => $"'{readableName}'"));

    private enum RelationshipAuthorizationProblemDetailsCase
    {
        StoredValueNull,
        ProposedValueMissing,
        NoRelationship,
    }

    private sealed record SelectedRelationshipAuthorizationFailure(
        RelationshipAuthorizationFailedStrategy Strategy,
        RelationshipAuthorizationFailedSubject Subject
    );
}

internal sealed record RelationshipAuthorizationProblemDetailsResult(
    string Type,
    string Detail,
    string[] Errors
);
