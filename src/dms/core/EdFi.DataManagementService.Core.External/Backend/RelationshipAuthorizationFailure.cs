// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// Identifies the authorization value family evaluated by a relationship authorization check.
/// </summary>
public enum RelationshipAuthorizationFailureValueSource
{
    Stored,
    Proposed,
}

/// <summary>
/// Identifies the failed relationship authorization condition for a strategy subject.
/// </summary>
public enum RelationshipAuthorizationSubjectFailureKind
{
    StoredValueNull,
    ProposedValueMissing,
    NoClaimEducationOrganizationIds,
    NoRelationship,
}

/// <summary>
/// Cross-boundary metadata for a failed relationship authorization OR group.
/// </summary>
public sealed record RelationshipAuthorizationFailure(
    RelationshipAuthorizationFailureValueSource ValueSource,
    int EmittedAuth1Index,
    RelationshipAuthorizationFailedStrategy[] FailedStrategies,
    EducationOrganizationId[] ClaimEducationOrganizationIds
);

/// <summary>
/// Metadata for one configured relationship authorization strategy that failed in the OR group.
/// </summary>
public sealed record RelationshipAuthorizationFailedStrategy(
    int ConfiguredStrategyIndex,
    int RelationshipLocalOrder,
    string StrategyName,
    string StrategyKind,
    RelationshipAuthorizationAuthObjectInfo? AuthObject,
    RelationshipAuthorizationFailedSubject[] FailedSubjects,
    string? Hint = null
);

/// <summary>
/// Metadata for one failed root-table subject within a relationship authorization strategy.
/// </summary>
public sealed record RelationshipAuthorizationFailedSubject(
    int SubjectIndex,
    RelationshipAuthorizationSubjectFailureKind FailureKind,
    RelationshipAuthorizationRootBinding RootBinding,
    RelationshipAuthorizationAuthObjectInfo AuthObject,
    RelationshipAuthorizationSecurableElement[] SecurableElements,
    string? Hint = null
)
{
    public RelationshipAuthorizationPersonSubjectInfo? PersonSubject { get; init; }
}

/// <summary>
/// User-readable and schema-position metadata for a contributing securable element.
/// </summary>
public sealed record RelationshipAuthorizationSecurableElement(
    string Kind,
    string JsonPath,
    string ReadableName
);

/// <summary>
/// Relational root/proposed anchor binding used by a relationship authorization subject.
/// </summary>
public sealed record RelationshipAuthorizationRootBinding(
    string ResourceName,
    string TableName,
    string ColumnName
);

/// <summary>
/// Authorization object metadata used by the failed relationship strategy.
/// </summary>
public sealed record RelationshipAuthorizationAuthObjectInfo(
    string Name,
    string SubjectValueColumn,
    string ClaimEducationOrganizationIdColumn,
    string? FailureHint = null
);

/// <summary>
/// Person-specific authorization metadata for a failed relationship authorization subject.
/// </summary>
public sealed record RelationshipAuthorizationPersonSubjectInfo(
    string PersonKind,
    string PathKind,
    RelationshipAuthorizationPersonDocumentIdPathStepInfo[] DocumentIdPath,
    RelationshipAuthorizationPersonStoredAnchorInfo StoredAnchor,
    RelationshipAuthorizationPersonProposedAnchorInfo? ProposedAnchor,
    string? Hint = null
);

/// <summary>
/// One ordered hop in the DocumentId path used to resolve a person relationship authorization value.
/// </summary>
public sealed record RelationshipAuthorizationPersonDocumentIdPathStepInfo(
    string SourceTableName,
    string SourceColumnName,
    string? TargetTableName,
    string? TargetColumnName
);

/// <summary>
/// Stored-value root row anchor for a person relationship authorization subject.
/// </summary>
public sealed record RelationshipAuthorizationPersonStoredAnchorInfo(
    string RootTableName,
    string RootDocumentIdColumnName
);

/// <summary>
/// Proposed-value anchor for a person relationship authorization subject.
/// </summary>
public sealed record RelationshipAuthorizationPersonProposedAnchorInfo(
    string Kind,
    RelationshipAuthorizationPersonProposedValueBindingInfo Binding
);

/// <summary>
/// Proposed root-row binding used as the anchor for a person relationship authorization subject.
/// </summary>
public sealed record RelationshipAuthorizationPersonProposedValueBindingInfo(
    string TableName,
    string ColumnName,
    int BindingIndex,
    string LogicalKey,
    string ParameterSeed
);
