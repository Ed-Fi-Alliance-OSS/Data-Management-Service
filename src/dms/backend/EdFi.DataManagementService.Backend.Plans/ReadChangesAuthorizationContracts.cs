// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>One securable-subject predicate within a ReadChanges relationship strategy.</summary>
public sealed record ReadChangesAuthorizationSubject(
    DbColumnName TrackedOldColumn, // c.OldX column the predicate filters
    DbTableName AuthView, // auth view to probe
    DbColumnName AuthViewSubjectColumn, // selected output column (Target EdOrg id, or *_DocumentId)
    DbColumnName AuthViewClaimColumn // WHERE column for claim EdOrg ids (SourceEducationOrganizationId)
);

/// <summary>One supported ReadChanges relationship strategy's AND-composed subjects.</summary>
public sealed record ReadChangesRelationshipCheckSpec(
    ConfiguredAuthorizationStrategy ConfiguredStrategy,
    IReadOnlyList<ReadChangesAuthorizationSubject> Subjects
);

/// <summary>The NamespaceBased check for a tracked-change resource/descriptor.</summary>
public sealed record ReadChangesNamespaceCheckSpec(DbColumnName TrackedOldNamespaceColumn);

/// <summary>
/// One custom view-based (<c>{Basis}With{Description}</c>) check for a tracked-change resource. Custom
/// views are AND filters executed in CMS-configured order after the relationship OR-group and the
/// namespace predicate.
/// </summary>
/// <param name="ConfiguredStrategy">The CMS-configured strategy.</param>
/// <param name="AuthorizationLocalOrder">CMS order of this check among the request's custom views.</param>
/// <param name="BasisResource">The basis resource named by the strategy prefix.</param>
/// <param name="View">The <c>auth.{StrategyName}</c> view, suffix included.</param>
/// <param name="ProbeBasisTombstones">
/// Whether the strategy name carries the <c>IncludingDeletes</c> suffix. Only live-seek bases act on it;
/// stored-DocumentId bases never seek the basis row, so the flag changes nothing for them.
/// </param>
/// <param name="Basis">How the basis <c>DocumentId</c> is obtained for each tracked-change row.</param>
public sealed record ReadChangesCustomViewCheckSpec(
    ConfiguredAuthorizationStrategy ConfiguredStrategy,
    int AuthorizationLocalOrder,
    QualifiedResourceName BasisResource,
    DbTableName View,
    bool ProbeBasisTombstones,
    ReadChangesCustomViewBasis Basis
);

/// <summary>
/// One basis identity part of a ReadChanges custom-view live seek: the basis column holding the value and
/// the subject tombstone's <c>Old*</c> column holding the same value for the tracked row. On a probe arm
/// <paramref name="BasisColumn"/> is the basis tombstone's own <c>Old*</c> column instead.
/// </summary>
/// <param name="BasisColumn">The column on the basis table (or basis tracked-change table) to seek.</param>
/// <param name="TrackedOldColumn">The subject tombstone's <c>Old*</c> column carrying the old basis key value.</param>
public sealed record ReadChangesCustomViewKeyPair(DbColumnName BasisColumn, DbColumnName TrackedOldColumn);

/// <summary>
/// One descriptor identity part of a ReadChanges custom-view live seek. The basis stores the descriptor as a
/// <c>*_DescriptorId</c> FK while the tombstone stores its old <c>Namespace</c> and <c>CodeValue</c>, so the
/// emitter matches a <c>dms.Descriptor</c> row on the tombstone pair (discriminated by
/// <paramref name="DescriptorResource"/>) and compares its <c>DocumentId</c> to the basis FK column.
/// </summary>
/// <param name="BasisFkColumn">The descriptor FK column on the basis root table.</param>
/// <param name="TrackedOldNamespaceColumn">The subject tombstone's old descriptor <c>Namespace</c> column.</param>
/// <param name="TrackedOldCodeValueColumn">The subject tombstone's old descriptor <c>CodeValue</c> column.</param>
/// <param name="DescriptorResource">The descriptor resource the FK column references.</param>
public sealed record ReadChangesCustomViewDescriptorKeyPair(
    DbColumnName BasisFkColumn,
    DbColumnName TrackedOldNamespaceColumn,
    DbColumnName TrackedOldCodeValueColumn,
    QualifiedResourceName DescriptorResource
);

/// <summary>
/// One descriptor identity part of a tombstone probe arm. Both sides store the old <c>Namespace</c> and
/// <c>CodeValue</c>, so the arm compares them directly with no <c>dms.Descriptor</c> join.
/// </summary>
public sealed record ReadChangesCustomViewProbeDescriptorKeyPair(
    DbColumnName BasisOldNamespaceColumn,
    DbColumnName BasisOldCodeValueColumn,
    DbColumnName TrackedOldNamespaceColumn,
    DbColumnName TrackedOldCodeValueColumn
);

/// <summary>
/// One arm of a tombstone probe: seeks a basis tracked-change table by its <c>Old*</c> identity columns with
/// the subject tombstone's paired old values and returns that table's <c>DocumentId</c> system column, so a
/// deleted basis can still be found. An abstract basis contributes one arm per concrete member.
/// </summary>
/// <param name="BasisTrackedChangeTable">The basis (or member) <c>tracked_changes_*</c> table.</param>
/// <param name="BasisDocumentIdColumn">That table's <c>DocumentId</c> system column.</param>
/// <param name="KeyPairs">Scalar identity parts, basis <c>Old*</c> column paired with the subject's.</param>
/// <param name="DescriptorKeyPairs">Descriptor identity parts, old Namespace/CodeValue on both sides.</param>
public sealed record ReadChangesCustomViewProbeArm(
    DbTableName BasisTrackedChangeTable,
    DbColumnName BasisDocumentIdColumn,
    IReadOnlyList<ReadChangesCustomViewKeyPair> KeyPairs,
    IReadOnlyList<ReadChangesCustomViewProbeDescriptorKeyPair> DescriptorKeyPairs
);

/// <summary>
/// The shared descriptor tracked-change table as a descriptor-basis probe reads it: seek the old
/// <c>Namespace</c>/<c>CodeValue</c> pair under the basis descriptor's discriminator and return the row's
/// <c>DocumentId</c> system column.
/// </summary>
/// <param name="DescriptorTrackedChangeTable">The shared descriptor <c>tracked_changes_*</c> table.</param>
/// <param name="DocumentIdColumn">That table's <c>DocumentId</c> system column.</param>
/// <param name="DiscriminatorColumn">That table's <c>Discriminator</c> system column.</param>
/// <param name="OldNamespaceColumn">The old <c>Namespace</c> column.</param>
/// <param name="OldCodeValueColumn">The old <c>CodeValue</c> column.</param>
public sealed record ReadChangesCustomViewDescriptorProbeArm(
    DbTableName DescriptorTrackedChangeTable,
    DbColumnName DocumentIdColumn,
    DbColumnName DiscriminatorColumn,
    DbColumnName OldNamespaceColumn,
    DbColumnName OldCodeValueColumn
);

/// <summary>
/// How a ReadChanges custom-view check obtains the basis <c>DocumentId</c> of a tracked-change row.
/// Consumers switch on the record type.
/// </summary>
public abstract record ReadChangesCustomViewBasis
{
    private ReadChangesCustomViewBasis() { }

    /// <summary>
    /// The tombstone already stores the basis <c>DocumentId</c>: the <c>DocumentId</c> system column for a
    /// self basis, or the person <c>Old*_DocumentId</c> column for a Student/Contact/Staff basis. The
    /// predicate is <c>c.&lt;TrackedColumn&gt; IN (SELECT DocumentId FROM auth.View)</c>.
    /// </summary>
    public sealed record StoredDocumentId(DbColumnName TrackedColumn) : ReadChangesCustomViewBasis;

    /// <summary>
    /// The tombstone stores the basis's old identity values but not its <c>DocumentId</c>: the emitter seeks
    /// the live basis table (a resource root table, or an abstract resource's union view) by the paired key
    /// columns and compares the found <c>DocumentId</c> to the view. When the strategy name carries the
    /// <c>IncludingDeletes</c> suffix, <paramref name="ProbeArms"/> add one <c>UNION</c> arm per basis
    /// tracked-change table so a deleted basis is still found; otherwise the list is empty. Rendered as
    /// <c>EXISTS (SELECT 1 FROM (&lt;live arm&gt; UNION &lt;probe arms&gt;) basis WHERE basis.DocumentId IN
    /// (SELECT DocumentId FROM &lt;View&gt;))</c>.
    /// </summary>
    /// <param name="BasisTable">The basis root table or abstract union view.</param>
    /// <param name="BasisDocumentIdColumn">The <c>DocumentId</c> column of <paramref name="BasisTable"/>.</param>
    /// <param name="KeyPairs">Scalar identity parts of the basis paired with the subject tombstone columns.</param>
    /// <param name="DescriptorKeyPairs">Descriptor identity parts of the basis.</param>
    /// <param name="ProbeArms">Tombstone probe arms; empty unless the strategy opts in by suffix.</param>
    public sealed record LiveSeek(
        DbTableName BasisTable,
        DbColumnName BasisDocumentIdColumn,
        IReadOnlyList<ReadChangesCustomViewKeyPair> KeyPairs,
        IReadOnlyList<ReadChangesCustomViewDescriptorKeyPair> DescriptorKeyPairs,
        IReadOnlyList<ReadChangesCustomViewProbeArm> ProbeArms
    ) : ReadChangesCustomViewBasis;

    /// <summary>
    /// The basis is a descriptor. The tombstone stores the descriptor's old <c>Namespace</c> and
    /// <c>CodeValue</c> but not its <c>DocumentId</c>, and descriptors share one <c>dms.Descriptor</c> table,
    /// so the emitter seeks that table on the pair, discriminated by <paramref name="DescriptorResource"/>
    /// with the same two discriminator parameter values the change-query descriptor join binds, and compares
    /// the found <c>DocumentId</c> to the view. When the strategy name carries the <c>IncludingDeletes</c>
    /// suffix, <paramref name="ProbeArm"/> adds one <c>UNION</c> arm over the shared descriptor
    /// tracked-change table so a deleted descriptor is still found; otherwise it is null.
    /// </summary>
    /// <param name="DescriptorResource">The descriptor resource the view is based on.</param>
    /// <param name="TrackedOldNamespaceColumn">The subject tombstone's old descriptor <c>Namespace</c> column.</param>
    /// <param name="TrackedOldCodeValueColumn">The subject tombstone's old descriptor <c>CodeValue</c> column.</param>
    /// <param name="ProbeArm">The shared descriptor tombstone probe; null unless the strategy opts in by suffix.</param>
    public sealed record DescriptorSeek(
        QualifiedResourceName DescriptorResource,
        DbColumnName TrackedOldNamespaceColumn,
        DbColumnName TrackedOldCodeValueColumn,
        ReadChangesCustomViewDescriptorProbeArm? ProbeArm
    ) : ReadChangesCustomViewBasis;
}

public sealed record ReadChangesAuthorizationPlan(
    IReadOnlyList<ReadChangesRelationshipCheckSpec> RelationshipChecks,
    ReadChangesNamespaceCheckSpec? NamespaceCheck,
    AuthorizationClaimEducationOrganizationIdParameterization? ClaimParameterization,
    NamespacePrefixParameterization? NamespaceParameterization,
    IReadOnlyList<ReadChangesCustomViewCheckSpec> CustomViewChecks
);

public abstract record ReadChangesAuthorizationPlanOutcome
{
    private ReadChangesAuthorizationPlanOutcome() { }

    /// <summary>Proceed: emit the relationship/namespace predicates (either list may be empty → no-op).</summary>
    public sealed record Plan(ReadChangesAuthorizationPlan AuthorizationPlan)
        : ReadChangesAuthorizationPlanOutcome;

    /// <summary>500 — unsupported strategy names (no ReadChanges implementation) or a concrete security-configuration error.</summary>
    public sealed record SecurityConfiguration(
        IReadOnlyList<string> UnavailableStrategyNames,
        IReadOnlyList<string> Errors
    ) : ReadChangesAuthorizationPlanOutcome
    {
        public SecurityConfiguration(IReadOnlyList<string> UnavailableStrategyNames)
            : this(UnavailableStrategyNames, []) { }
    }

    /// <summary>
    /// 500 — at least one configured custom view could not be planned. <paramref name="PlannedChecks"/>
    /// carries the custom views that <em>did</em> plan, so the caller can still validate the ones
    /// configured ahead of the earliest failure before reporting it (mirrors
    /// <see cref="CustomViewAuthorizationPlanOutcome.SecurityConfiguration"/>).
    /// </summary>
    public sealed record CustomViewSecurityConfiguration(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> Failures,
        IReadOnlyList<ReadChangesCustomViewCheckSpec> PlannedChecks
    ) : ReadChangesAuthorizationPlanOutcome;

    /// <summary>403 — NamespaceBased configured and the client has no namespace prefixes.</summary>
    public sealed record NamespaceNoPrefixesConfigured(string StrategyName)
        : ReadChangesAuthorizationPlanOutcome;
}
