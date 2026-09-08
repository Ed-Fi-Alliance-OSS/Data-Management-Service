// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

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
    OwnershipBased,
    RelationshipsWithEdOrgsAndPeople,
    RelationshipsWithEdOrgsAndPeopleInverted,
    RelationshipsWithPeopleOnly,
    RelationshipsWithStudentsOnly,
    RelationshipsWithStudentsOnlyThroughResponsibility,
    CustomViewBased,
}

public sealed record ConfiguredAuthorizationStrategy(string StrategyName, int RawConfiguredIndex);

public sealed record RelationshipAuthorizationStrategySubjectEligibility
{
    public RelationshipAuthorizationStrategySubjectEligibility(
        SecurableElementKind kind,
        RelationshipAuthorizationPersonAuthViewKind? personAuthViewKind = null
    )
    {
        var isPersonKind =
            kind
            is SecurableElementKind.Student
                or SecurableElementKind.Contact
                or SecurableElementKind.Staff;

        if (kind is SecurableElementKind.Namespace)
        {
            throw new ArgumentException(
                "Namespace securable elements are not relationship authorization subjects.",
                nameof(kind)
            );
        }

        if (isPersonKind && personAuthViewKind is null)
        {
            throw new ArgumentException(
                "Person relationship authorization subjects require an auth view kind.",
                nameof(personAuthViewKind)
            );
        }

        if (!isPersonKind && personAuthViewKind is not null)
        {
            throw new ArgumentException(
                "Only person relationship authorization subjects can carry a person auth view kind.",
                nameof(personAuthViewKind)
            );
        }

        Kind = kind;
        PersonAuthViewKind = personAuthViewKind;
    }

    public SecurableElementKind Kind { get; init; }

    public RelationshipAuthorizationPersonAuthViewKind? PersonAuthViewKind { get; init; }
}

public sealed record SupportedRelationshipAuthorizationStrategy(
    RelationshipAuthorizationStrategyKind Kind,
    RelationshipAuthorizationHierarchyDirection Direction,
    ConfiguredAuthorizationStrategy ConfiguredStrategy,
    int RelationshipLocalOrder,
    IReadOnlyList<RelationshipAuthorizationStrategySubjectEligibility> EligibleSubjects
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

public enum RelationshipAuthorizationPersonKind
{
    Student,
    Contact,
    Staff,
}

public enum RelationshipAuthorizationPersonAuthViewKind
{
    Student,
    Contact,
    Staff,
    StudentThroughResponsibility,
}

public sealed record RelationshipAuthorizationAuthObject(
    DbTableName Name,
    DbColumnName SubjectValueColumn,
    DbColumnName ClaimEducationOrganizationIdColumn,
    bool AllowsDirectClaimMatch = false,
    string? FailureHint = null
)
{
    public static RelationshipAuthorizationAuthObject CreateEdOrgHierarchy(
        RelationshipAuthorizationHierarchyDirection direction
    ) =>
        direction switch
        {
            RelationshipAuthorizationHierarchyDirection.Normal => new RelationshipAuthorizationAuthObject(
                AuthNames.EdOrgIdToEdOrgId,
                AuthNames.TargetEdOrgId,
                AuthNames.SourceEdOrgId,
                AllowsDirectClaimMatch: true
            ),
            RelationshipAuthorizationHierarchyDirection.Inverted => new RelationshipAuthorizationAuthObject(
                AuthNames.EdOrgIdToEdOrgId,
                AuthNames.SourceEdOrgId,
                AuthNames.TargetEdOrgId,
                AllowsDirectClaimMatch: true
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(direction),
                direction,
                "Unsupported relationship authorization hierarchy direction."
            ),
        };

    public static RelationshipAuthorizationAuthObject CreatePerson(
        RelationshipAuthorizationPersonAuthViewKind authViewKind
    )
    {
        var definition = AuthObjectDefinitions.GetPeopleAuthViewDefinition(
            MapPeopleAuthViewKind(authViewKind)
        );

        return new RelationshipAuthorizationAuthObject(
            definition.View,
            definition.PersonDocumentIdOutputColumn,
            definition.ClaimEducationOrganizationIdColumn,
            FailureHint: definition.FailureHint
        );
    }

    private static AuthPeopleViewKind MapPeopleAuthViewKind(
        RelationshipAuthorizationPersonAuthViewKind authViewKind
    ) =>
        authViewKind switch
        {
            RelationshipAuthorizationPersonAuthViewKind.Student => AuthPeopleViewKind.Student,
            RelationshipAuthorizationPersonAuthViewKind.Contact => AuthPeopleViewKind.Contact,
            RelationshipAuthorizationPersonAuthViewKind.Staff => AuthPeopleViewKind.Staff,
            RelationshipAuthorizationPersonAuthViewKind.StudentThroughResponsibility =>
                AuthPeopleViewKind.StudentThroughResponsibility,
            _ => throw new ArgumentOutOfRangeException(
                nameof(authViewKind),
                authViewKind,
                "Unsupported person relationship authorization view kind."
            ),
        };
}

public sealed record RelationshipAuthorizationSubjectContributor(
    SecurableElementKind Kind,
    string JsonPath,
    string ReadableName,
    int ContributionOrder = 0
);

public enum RelationshipAuthorizationSkippedSubjectReason
{
    ChildCollectionPersonPathOutsideSubjectScope,
}

public sealed record RelationshipAuthorizationSkippedSubjectContributor(
    SecurableElementKind Kind,
    string JsonPath,
    string ReadableName,
    int ContributionOrder,
    RelationshipAuthorizationSkippedSubjectReason Reason,
    RelationshipAuthorizationPersonKind? PersonKind = null,
    RelationshipAuthorizationAuthObject? AuthObject = null,
    DbTableName? Table = null,
    DbColumnName? Column = null
);

public enum RelationshipAuthorizationPersonSubjectPathKind
{
    DirectRootColumn,
    TransitiveJoinPath,
    SelfRootDocumentId,
}

public sealed record RelationshipAuthorizationPersonSubjectPath
{
    public RelationshipAuthorizationPersonSubjectPath(
        RelationshipAuthorizationPersonSubjectPathKind kind,
        IReadOnlyList<ColumnPathStep> steps
    )
    {
        ArgumentNullException.ThrowIfNull(steps);

        switch (kind)
        {
            case RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn when steps.Count != 1:
                throw new ArgumentException(
                    "Direct person root-column bindings require exactly one column path step.",
                    nameof(steps)
                );
            case RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath when steps.Count < 2:
                throw new ArgumentException(
                    "Transitive person bindings require at least two ordered column path steps.",
                    nameof(steps)
                );
            case RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId when steps.Count != 0:
                throw new ArgumentException(
                    "Self person root DocumentId bindings must be represented as a zero-hop path.",
                    nameof(steps)
                );
        }

        Kind = kind;
        Steps = steps;
    }

    public RelationshipAuthorizationPersonSubjectPathKind Kind { get; init; }

    public IReadOnlyList<ColumnPathStep> Steps { get; init; }
}

public sealed record RelationshipAuthorizationPersonStoredAnchor(
    DbTableName RootTable,
    DbColumnName RootDocumentIdColumn
);

public enum RelationshipAuthorizationPersonProposedAnchorKind
{
    RootRow,
    FirstHop,
    ExistingTargetDocumentId,
}

public sealed record RelationshipAuthorizationPersonProposedAnchor(
    RelationshipAuthorizationPersonProposedAnchorKind Kind,
    RelationshipAuthorizationProposedValueBinding Binding
);

public sealed record RelationshipAuthorizationPersonSubjectMetadata(
    RelationshipAuthorizationPersonKind PersonKind,
    RelationshipAuthorizationPersonSubjectPath Path,
    RelationshipAuthorizationPersonStoredAnchor StoredAnchor,
    RelationshipAuthorizationPersonProposedAnchor? ProposedAnchor
);

public sealed record RelationshipAuthorizationSubject(
    QualifiedResourceName Resource,
    DbTableName Table,
    DbColumnName Column,
    RelationshipAuthorizationAuthObject AuthObject,
    IReadOnlyList<RelationshipAuthorizationSubjectContributor> Contributors,
    RelationshipAuthorizationPersonSubjectMetadata? PersonMetadata = null
)
{
    public bool IsPersonSubject => PersonMetadata is not null;
}

public enum RelationshipAuthorizationSubjectIneligibilityReason
{
    SelfPersonDocumentIdUnavailableForCreateNew,
}

public sealed record RelationshipAuthorizationIneligibleSubject(
    RelationshipAuthorizationSubject Subject,
    RelationshipAuthorizationSubjectIneligibilityReason Reason,
    string Hint
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
    int RelationshipLocalOrder,
    RelationshipAuthorizationHierarchyDirection Direction,
    RelationshipAuthorizationValueSource ValueSource,
    IReadOnlyList<RelationshipAuthorizationSubject> Subjects,
    RelationshipAuthorizationCheckTarget CheckTarget
)
{
    public IReadOnlyList<RelationshipAuthorizationIneligibleSubject> IneligibleSubjects { get; init; } = [];

    public IReadOnlyList<RelationshipAuthorizationSkippedSubjectContributor> SkippedContributors { get; init; } =
    [];
}

public enum RelationshipAuthorizationFailureKind
{
    KnownButNotEnabledStrategy,
    InvalidAuthorizationStrategy,
    UnknownCustomViewBasisResource,
    UnresolvedSecurableElement,
    NoApplicableRootSubject,
    NoExecutableSubjects,
    NoClaimEducationOrganizationIds,
    MissingProposedRootBinding,
    StoredValueNull,
    ProposedValueMissing,
    MissingPeopleAuthViewAssociations,
}

public sealed record RelationshipAuthorizationFailureLocation(
    SecurableElementKind? Kind = null,
    string? JsonPath = null,
    string? ReadableName = null,
    DbTableName? Table = null,
    DbColumnName? Column = null,
    string? AuthorizationObjectName = null
);

public sealed record RelationshipAuthorizationPersonFailureMetadata(
    RelationshipAuthorizationPersonKind PersonKind,
    RelationshipAuthorizationAuthObject AuthObject,
    RelationshipAuthorizationPersonSubjectPath? Path = null
);

public sealed record RelationshipAuthorizationFailureMetadata(
    RelationshipAuthorizationFailureKind FailureKind,
    QualifiedResourceName Resource,
    ConfiguredAuthorizationStrategy? ConfiguredStrategy = null,
    int? RelationshipLocalOrder = null,
    RelationshipAuthorizationValueSource? ValueSource = null,
    RelationshipAuthorizationAuthObject? AuthObject = null,
    RelationshipAuthorizationFailureLocation? Location = null,
    string? Hint = null
)
{
    public RelationshipAuthorizationPersonFailureMetadata? PersonMetadata { get; init; }

    public IReadOnlyList<RelationshipAuthorizationSubjectContributor> Contributors { get; init; } = [];

    public IReadOnlyList<RelationshipAuthorizationSkippedSubjectContributor> SkippedContributors { get; init; } =
    [];

    public IReadOnlyList<RelationshipAuthorizationIneligibleSubject> IneligibleSubjects { get; init; } = [];
}

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
            null,
        RelationshipAuthorizationExecutableShape? ExecutableShape = null
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

public sealed record RelationshipAuthorizationUpdatePlan(
    RelationshipAuthorizationResult StoredValues,
    RelationshipAuthorizationResult ProposedValues,
    IReadOnlyList<RelationshipAuthorizationFailureMetadata> SecurityConfigurationFailures,
    IReadOnlyList<RelationshipAuthorizationFailureMetadata> KnownButNotEnabledFailures
);
