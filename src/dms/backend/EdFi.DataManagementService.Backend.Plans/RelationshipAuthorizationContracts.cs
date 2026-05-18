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

public enum RelationshipAuthorizationClassificationOutcome
{
    NoAuthorizationRequired,
    NoFurtherAuthorizationRequired,
    SupportedStrategies,
    KnownButNotEnabled,
    SecurityConfigurationError,
}

public enum RelationshipAuthorizationStrategyKind
{
    RelationshipsWithEdOrgsOnly,
    RelationshipsWithEdOrgsOnlyInverted,
    NamespaceBased,
    OwnershipBased,
    RelationshipsWithEdOrgsAndPeople,
    RelationshipsWithEdOrgsAndPeopleInverted,
    RelationshipsWithPeopleOnly,
    RelationshipsWithStudentsOnly,
    RelationshipsWithStudentsOnlyThroughResponsibility,
    CustomViewBased,
}

public sealed record ConfiguredAuthorizationStrategy(
    string StrategyName,
    int RawConfiguredIndex,
    RelationshipAuthorizationStrategyComposition Composition
);

public sealed record SupportedRelationshipAuthorizationStrategy(
    RelationshipAuthorizationStrategyKind Kind,
    RelationshipAuthorizationHierarchyDirection Direction,
    ConfiguredAuthorizationStrategy ConfiguredStrategy,
    int RelationshipLocalOrder
);

public sealed record KnownButNotEnabledRelationshipAuthorizationStrategy(
    RelationshipAuthorizationStrategyKind Kind,
    ConfiguredAuthorizationStrategy ConfiguredStrategy,
    int RelationshipLocalOrder,
    QualifiedResourceName? BasisResource = null
);

public sealed record RelationshipAuthorizationClassification(
    RelationshipAuthorizationClassificationOutcome Outcome,
    IReadOnlyList<SupportedRelationshipAuthorizationStrategy> SupportedStrategies,
    IReadOnlyList<ConfiguredAuthorizationStrategy> NoFurtherAuthorizationRequiredStrategies,
    IReadOnlyList<KnownButNotEnabledRelationshipAuthorizationStrategy> KnownButNotEnabledStrategies,
    IReadOnlyList<RelationshipAuthorizationFailureMetadata> SecurityConfigurationFailures
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

public abstract record RelationshipAuthorizationCheckTarget
{
    private RelationshipAuthorizationCheckTarget() { }

    public sealed record Stored(DbTableName RootTable, DbColumnName DocumentIdColumn)
        : RelationshipAuthorizationCheckTarget;

    public sealed record Proposed(
        DbTableName RootTable,
        IReadOnlyList<RelationshipAuthorizationProposedValueBinding> SubjectBindingsInOrder
    ) : RelationshipAuthorizationCheckTarget;
}

public sealed record RelationshipAuthorizationProposedValueBinding(
    DbTableName Table,
    DbColumnName Column,
    int BindingIndex,
    string LogicalKey,
    string ParameterSeed
);

public sealed record RelationshipAuthorizationCheckSpec(
    ConfiguredAuthorizationStrategy ConfiguredStrategy,
    RelationshipAuthorizationHierarchyDirection Direction,
    RelationshipAuthorizationValueSource ValueSource,
    IReadOnlyList<RelationshipAuthorizationSubject> Subjects,
    RelationshipAuthorizationCheckTarget CheckTarget
);

public enum RelationshipAuthorizationFailureKind
{
    KnownButNotEnabledStrategy,
    InvalidAuthorizationStrategy,
    UnknownCustomViewBasisResource,
    UnresolvedSecurableElement,
    NoApplicableRootSubject,
    NoClaimEducationOrganizationIds,
    MissingProposedRootBinding,
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
    int? RelationshipLocalOrder = null,
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
