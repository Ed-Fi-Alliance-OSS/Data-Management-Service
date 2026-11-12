// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.SecurityMetadataTools;

/// <summary>Stable, safely rendered reports about the metadata compared by this request.</summary>
internal static class VerificationReportFormatter
{
    internal static string Format(EffectivePermissionsComparison comparison)
    {
        if (comparison.IsMatch)
        {
            return "CMS authorization metadata matches the expected effective permissions.";
        }

        var lines = comparison
            .Differences.OrderBy(difference => difference.ClaimSetName, StringComparer.Ordinal)
            .ThenBy(difference => difference.ResourceClaimName, StringComparer.Ordinal)
            .ThenBy(difference => difference.ActionName, StringComparer.Ordinal)
            .ThenBy(difference => difference.Kind)
            .Select(FormatDifference);
        return "CMS authorization metadata differs from the expected effective permissions.\n"
            + string.Join("\n", lines);
    }

    private static string FormatDifference(EffectivePermissionsDifference difference)
    {
        string context = $"claim set {Quote(difference.ClaimSetName)}";
        if (
            difference.Kind
            is not (
                EffectivePermissionsDifferenceKind.MissingClaimSet
                or EffectivePermissionsDifferenceKind.UnexpectedClaimSet
            )
        )
        {
            context += $", claim {Quote(difference.ResourceClaimName)}";
        }
        if (
            difference.Kind
            is EffectivePermissionsDifferenceKind.MissingAction
                or EffectivePermissionsDifferenceKind.UnexpectedAction
                or EffectivePermissionsDifferenceKind.StrategySequence
        )
        {
            context += $", action {Quote(difference.ActionName)}";
        }

        string label = difference.Kind switch
        {
            EffectivePermissionsDifferenceKind.MissingClaimSet => "Missing claim set",
            EffectivePermissionsDifferenceKind.UnexpectedClaimSet => "Unexpected claim set",
            EffectivePermissionsDifferenceKind.MissingClaim => "Missing claim",
            EffectivePermissionsDifferenceKind.UnexpectedClaim => "Unexpected claim",
            EffectivePermissionsDifferenceKind.MissingAction => "Missing action",
            EffectivePermissionsDifferenceKind.UnexpectedAction => "Unexpected action",
            EffectivePermissionsDifferenceKind.StrategySequence => "Different strategies",
            _ => throw new ArgumentOutOfRangeException(nameof(difference)),
        };
        bool isSequence = difference.Kind is EffectivePermissionsDifferenceKind.StrategySequence;
        return $"{label}: {context}; expected {Values(difference.Expected, isSequence)}; actual {Values(difference.Actual, isSequence)}.";
    }

    private static string Values(IReadOnlyList<string> values, bool isSequence) =>
        values.Count == 0 && !isSequence ? "<missing>" : $"[{string.Join(", ", values.Select(Quote))}]";

    private static string Quote(string value) =>
        $"\"{CommandDiagnostics.Normalize(value.Replace("\\", "\\\\").Replace("\"", "\\\""))}\"";
}
