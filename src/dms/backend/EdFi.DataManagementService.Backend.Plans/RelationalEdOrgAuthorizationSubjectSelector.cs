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

internal sealed record RelationalEdOrgAuthorizationSubjectSelection(
    RelationalEdOrgAuthorizationSubjectSelectionOutcome Outcome,
    IReadOnlyList<RelationshipAuthorizationSubject> Subjects,
    string? FailureMessage
);

internal static class RelationalEdOrgAuthorizationSubjectSelector
{
    private static readonly RelationalEdOrgAuthorizationElementResolutionCache _elementResolutionCache =
        new();

    public static RelationalEdOrgAuthorizationSubjectSelection Select(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(configuredAuthorizationStrategies);

        var concreteResourceModel = mappingSet.GetConcreteResourceModelOrThrow(resource);
        var rootTable = concreteResourceModel.RelationalModel.Root.Table;
        var elementResolutions = _elementResolutionCache.GetOrResolveAll(mappingSet, resource);
        var configuredElements = elementResolutions.Select(static resolution => resolution.Element).ToArray();
        var resolvedCandidates = elementResolutions
            .SelectMany(static resolution => resolution.ResolvedCandidates)
            .ToArray();

        List<RelationshipAuthorizationSubject> subjects = [];
        List<EdOrgSecurableElement> unresolvedWithoutSelectedSubject = [];

        foreach (var elementResolution in elementResolutions)
        {
            var selectedCandidate = SelectPreferredConcreteRootCandidate(
                rootTable,
                elementResolution.ResolvedCandidates
            );

            if (selectedCandidate is not null)
            {
                subjects.Add(
                    new RelationshipAuthorizationSubject(
                        resource,
                        selectedCandidate.Step.SourceTable,
                        selectedCandidate.Step.SourceColumnName,
                        [
                            new RelationshipAuthorizationSubjectContributor(
                                SecurableElementKind.EducationOrganization,
                                selectedCandidate.JsonPath,
                                selectedCandidate.ReadableName
                            ),
                        ]
                    )
                );

                continue;
            }

            if (elementResolution.ResolvedCandidates.Count == 0)
            {
                unresolvedWithoutSelectedSubject.Add(elementResolution.Element);
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
                    configuredAuthorizationStrategies,
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
                configuredAuthorizationStrategies,
                resolvedCandidates,
                configuredElements
            )
        );
    }

    private static ResolvedEdOrgSecurableElementCandidate? SelectPreferredConcreteRootCandidate(
        DbTableName rootTable,
        IReadOnlyList<ResolvedEdOrgSecurableElementCandidate> candidates
    )
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .Where(candidate => candidate.Step.SourceTable == rootTable)
            .OrderBy(static candidate => candidate.JsonPath.Length)
            .ThenBy(static candidate => candidate.JsonPath, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Step.SourceColumnName.Value, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static string BuildUnresolvedFailureMessage(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies,
        IReadOnlyList<EdOrgSecurableElement> unresolvedElements
    )
    {
        var unresolvedDetails = unresolvedElements
            .Select(static element => $"'{element.MetaEdName}' at '{element.JsonPath}'")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static detail => detail, StringComparer.Ordinal);

        return $"Relational query authorization metadata is invalid for resource '{MappingSetResourceLookupExtensions.FormatResource(resource)}'. "
            + $"Effective GET-many strategies [{FormatStrategyNames(configuredAuthorizationStrategies)}] require resolvable EducationOrganization securable elements, "
            + $"but the following elements could not be resolved to relational columns in mapping set "
            + $"'{MappingSetResourceLookupExtensions.FormatMappingSetKey(mappingSet.Key)}': "
            + $"[{string.Join(", ", unresolvedDetails)}].";
    }

    private static string BuildNoApplicableSubjectsFailureMessage(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies,
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
            + $"Effective GET-many strategies [{FormatStrategyNames(configuredAuthorizationStrategies)}] require at least one applicable concrete root-table EducationOrganization authorization subject, "
            + $"but none were found in mapping set '{MappingSetResourceLookupExtensions.FormatMappingSetKey(mappingSet.Key)}'. "
            + $"{resolvedDetailText} {configuredDetailText}";
    }

    private static string FormatStrategyNames(
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies
    )
    {
        return string.Join(
            ", ",
            configuredAuthorizationStrategies
                .Select(static strategy => strategy.StrategyName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static strategyName => strategyName, StringComparer.Ordinal)
                .Select(static strategyName => $"'{strategyName}'")
        );
    }
}
