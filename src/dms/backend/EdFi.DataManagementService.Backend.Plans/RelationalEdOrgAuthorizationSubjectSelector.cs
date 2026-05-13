// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

internal enum RelationalEdOrgAuthorizationSubjectSelectionOutcome
{
    Success,
    SecurityConfigurationError,
}

internal sealed record RelationalEdOrgAuthorizationSubject(
    string JsonPath,
    string ReadableName,
    DbTableName Table,
    DbColumnName Column
);

internal sealed record RelationalEdOrgAuthorizationSubjectSelection(
    RelationalEdOrgAuthorizationSubjectSelectionOutcome Outcome,
    IReadOnlyList<RelationalEdOrgAuthorizationSubject> Subjects,
    string? FailureMessage
);

internal static class RelationalEdOrgAuthorizationSubjectSelector
{
    public static RelationalEdOrgAuthorizationSubjectSelection Select(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<string> strategyNames
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(strategyNames);

        var concreteResourceModel = mappingSet.GetConcreteResourceModelOrThrow(resource);
        var rootTable = concreteResourceModel.RelationalModel.Root.Table;
        var candidateResolution = SecurableElementColumnPathResolver.ResolveEducationOrganizationCandidates(
            concreteResourceModel
        );

        var resolvedCandidatesByName = candidateResolution
            .ResolvedCandidates.GroupBy(static candidate => candidate.ReadableName, StringComparer.Ordinal)
            .ToDictionary(
                static grouping => grouping.Key,
                static grouping => grouping.ToArray(),
                StringComparer.Ordinal
            );

        var unresolvedElementsByName = candidateResolution
            .UnresolvedElements.GroupBy(static element => element.MetaEdName, StringComparer.Ordinal)
            .ToDictionary(
                static grouping => grouping.Key,
                static grouping => grouping.ToArray(),
                StringComparer.Ordinal
            );

        List<RelationalEdOrgAuthorizationSubject> subjects = [];
        List<EdOrgSecurableElement> unresolvedWithoutSelectedSubject = [];

        foreach (
            var metaEdName in concreteResourceModel
                .SecurableElements.EducationOrganization.Select(static element => element.MetaEdName)
                .Distinct(StringComparer.Ordinal)
        )
        {
            var selectedCandidate = resolvedCandidatesByName.TryGetValue(metaEdName, out var candidates)
                ? candidates
                    .Where(candidate => candidate.Step.SourceTable == rootTable)
                    .OrderBy(static candidate => candidate.JsonPath.Length)
                    .ThenBy(static candidate => candidate.JsonPath, StringComparer.Ordinal)
                    .ThenBy(static candidate => candidate.Step.SourceColumnName.Value, StringComparer.Ordinal)
                    .FirstOrDefault()
                : null;

            if (selectedCandidate is not null)
            {
                subjects.Add(
                    new RelationalEdOrgAuthorizationSubject(
                        selectedCandidate.JsonPath,
                        selectedCandidate.ReadableName,
                        selectedCandidate.Step.SourceTable,
                        selectedCandidate.Step.SourceColumnName
                    )
                );

                continue;
            }

            if (unresolvedElementsByName.TryGetValue(metaEdName, out var unresolvedElements))
            {
                unresolvedWithoutSelectedSubject.AddRange(unresolvedElements);
            }
        }

        if (unresolvedWithoutSelectedSubject.Count > 0)
        {
            return new RelationalEdOrgAuthorizationSubjectSelection(
                RelationalEdOrgAuthorizationSubjectSelectionOutcome.SecurityConfigurationError,
                [],
                BuildUnresolvedFailureMessage(
                    mappingSet,
                    resource,
                    strategyNames,
                    unresolvedWithoutSelectedSubject
                )
            );
        }

        if (subjects.Count > 0)
        {
            return new RelationalEdOrgAuthorizationSubjectSelection(
                RelationalEdOrgAuthorizationSubjectSelectionOutcome.Success,
                subjects,
                null
            );
        }

        return new RelationalEdOrgAuthorizationSubjectSelection(
            RelationalEdOrgAuthorizationSubjectSelectionOutcome.SecurityConfigurationError,
            [],
            BuildNoApplicableSubjectsFailureMessage(
                mappingSet,
                resource,
                strategyNames,
                candidateResolution.ResolvedCandidates,
                concreteResourceModel.SecurableElements.EducationOrganization
            )
        );
    }

    private static string BuildUnresolvedFailureMessage(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<string> strategyNames,
        IReadOnlyList<EdOrgSecurableElement> unresolvedElements
    )
    {
        var unresolvedDetails = unresolvedElements
            .Select(static element => $"'{element.MetaEdName}' at '{element.JsonPath}'")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static detail => detail, StringComparer.Ordinal);

        return $"Relational query authorization metadata is invalid for resource '{MappingSetResourceLookupExtensions.FormatResource(resource)}'. "
            + $"Effective GET-many strategies [{FormatStrategyNames(strategyNames)}] require resolvable EducationOrganization securable elements, "
            + $"but the following elements could not be resolved to relational columns in mapping set "
            + $"'{MappingSetResourceLookupExtensions.FormatMappingSetKey(mappingSet.Key)}': "
            + $"[{string.Join(", ", unresolvedDetails)}].";
    }

    private static string BuildNoApplicableSubjectsFailureMessage(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<string> strategyNames,
        IReadOnlyList<ResolvedEdOrgSecurableElementCandidate> resolvedCandidates,
        IReadOnlyList<EdOrgSecurableElement> configuredElements
    )
    {
        var resolvedDetails = resolvedCandidates
            .Select(static candidate =>
                $"'{candidate.ReadableName}' at '{candidate.JsonPath}' -> '{candidate.Step.SourceTable}.{candidate.Step.SourceColumnName.Value}'"
            )
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static detail => detail, StringComparer.Ordinal)
            .ToArray();

        var configuredDetails = configuredElements
            .Select(static element => $"'{element.MetaEdName}' at '{element.JsonPath}'")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static detail => detail, StringComparer.Ordinal)
            .ToArray();

        var resolvedDetailText =
            resolvedDetails.Length == 0
                ? "No EducationOrganization securable elements resolved to relational columns."
                : $"Resolved candidates: [{string.Join(", ", resolvedDetails)}].";
        var configuredDetailText =
            configuredDetails.Length == 0
                ? "No EducationOrganization securable elements are configured for this resource."
                : $"Configured elements: [{string.Join(", ", configuredDetails)}].";

        return $"Relational query authorization metadata is invalid for resource '{MappingSetResourceLookupExtensions.FormatResource(resource)}'. "
            + $"Effective GET-many strategies [{FormatStrategyNames(strategyNames)}] require at least one applicable root/base EducationOrganization authorization subject, "
            + $"but none were found in mapping set '{MappingSetResourceLookupExtensions.FormatMappingSetKey(mappingSet.Key)}'. "
            + $"{resolvedDetailText} {configuredDetailText}";
    }

    private static string FormatStrategyNames(IReadOnlyList<string> strategyNames)
    {
        return string.Join(
            ", ",
            strategyNames
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static strategyName => strategyName, StringComparer.Ordinal)
                .Select(static strategyName => $"'{strategyName}'")
        );
    }
}
