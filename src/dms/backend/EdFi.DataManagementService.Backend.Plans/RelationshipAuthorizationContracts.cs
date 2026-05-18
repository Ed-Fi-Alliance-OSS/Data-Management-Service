// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

public enum RelationshipAuthorizationStrategyComposition
{
    Or = 1,
    And = 2,
}

public sealed record ConfiguredAuthorizationStrategy(
    string StrategyName,
    int RawConfiguredIndex,
    RelationshipAuthorizationStrategyComposition Composition
);

public enum RelationshipAuthorizationHierarchyDirection
{
    Normal,
    Inverted,
}

public enum RelationshipAuthorizationValueSource
{
    Stored,
    Proposed,
}

public sealed record RelationshipAuthorizationSubjectContributor(
    SecurableElementKind Kind,
    string JsonPath,
    string ReadableName
);

public sealed record RelationshipAuthorizationSubject(
    QualifiedResourceName Resource,
    DbTableName Table,
    DbColumnName Column,
    IReadOnlyList<RelationshipAuthorizationSubjectContributor> Contributors
);

public sealed record RelationshipAuthorizationCheckSpec(
    ConfiguredAuthorizationStrategy ConfiguredStrategy,
    RelationshipAuthorizationHierarchyDirection Direction,
    RelationshipAuthorizationValueSource ValueSource,
    IReadOnlyList<RelationshipAuthorizationSubject> Subjects,
    string StableParameterSeed
);

public enum RelationshipAuthorizationFailureKind
{
    InvalidAuthorizationStrategy,
    UnknownCustomViewBasisResource,
    UnresolvedSecurableElement,
    NoApplicableRootSubject,
    NoClaimEducationOrganizationIds,
    StoredValueNull,
    ProposedValueMissing,
}

public sealed record RelationshipAuthorizationFailureLocation(
    SecurableElementKind? Kind = null,
    string? JsonPath = null,
    string? ReadableName = null,
    DbTableName? Table = null,
    DbColumnName? Column = null,
    string? AuthorizationObjectName = null
);

public sealed record RelationshipAuthorizationFailureMetadata(
    RelationshipAuthorizationFailureKind FailureKind,
    QualifiedResourceName Resource,
    ConfiguredAuthorizationStrategy? ConfiguredStrategy = null,
    RelationshipAuthorizationFailureLocation? Location = null,
    string? Hint = null
);

public abstract record RelationshipAuthorizationResult
{
    private RelationshipAuthorizationResult() { }

    public sealed record NoAuthorizationRequired(
        IReadOnlyList<ConfiguredAuthorizationStrategy> ConfiguredStrategies
    ) : RelationshipAuthorizationResult;

    public sealed record NoFurtherAuthorizationRequired(
        IReadOnlyList<ConfiguredAuthorizationStrategy> ConfiguredStrategies
    ) : RelationshipAuthorizationResult;

    public sealed record Authorized(
        IReadOnlyList<RelationshipAuthorizationCheckSpec> CheckSpecs,
        AuthorizationClaimEducationOrganizationIdParameterization? ClaimEducationOrganizationIdParameterization =
            null
    ) : RelationshipAuthorizationResult;

    public sealed record KnownButNotEnabled(IReadOnlyList<RelationshipAuthorizationFailureMetadata> Failures)
        : RelationshipAuthorizationResult;

    public sealed record SecurityConfigurationError(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> Failures
    ) : RelationshipAuthorizationResult;

    public sealed record NoClaims(
        IReadOnlyList<RelationshipAuthorizationCheckSpec> CheckSpecs,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> Failures
    ) : RelationshipAuthorizationResult;
}
