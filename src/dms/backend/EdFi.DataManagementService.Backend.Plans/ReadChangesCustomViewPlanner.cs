// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Outcome of planning the custom view-based strategies of one ReadChanges request. Checks and failures
/// are both carried so the caller can validate the views configured ahead of the earliest failure before
/// reporting it.
/// </summary>
internal sealed record ReadChangesCustomViewPlanResult(
    IReadOnlyList<ReadChangesCustomViewCheckSpec> Checks,
    IReadOnlyList<RelationshipAuthorizationFailureMetadata> Failures
);

/// <summary>
/// Plans the custom view-based (<c>{Basis}With{Description}</c>) strategies for the /deletes and
/// /keyChanges endpoints. Every check reads tombstone values only, in one of two shapes:
/// <list type="bullet">
/// <item>
/// <b>Stored DocumentId.</b> A <b>self basis</b> (the subject resource or its abstract supertype) reads the
/// <c>DocumentId</c> system column, and a <b>person basis</b> (Student, Contact, or Staff) reads the stored
/// person <c>Old*_DocumentId</c> column whose person join chain matches the resolved subject-to-basis path,
/// exactly as the people relationship strategies pick their column.
/// </item>
/// <item>
/// <b>Live seek.</b> Every other resource basis. The tombstone holds the basis's old identity values but
/// not its DocumentId, so the planner pairs each basis identity column with the canonical tombstone column
/// holding its old value by walking the resolved path from the basis back to the subject root through each
/// hop's <see cref="DocumentReferenceBinding.IdentityBindings"/>. Descriptor identity parts pair the basis
/// FK column with the tombstone's old Namespace/CodeValue; an abstract basis seeks its union view by the
/// view's identity output columns. When the strategy name carries the <c>IncludingDeletes</c> suffix, one
/// probe arm per basis tracked-change table (per concrete member, for an abstract basis) lets a deleted
/// basis still be found by its <c>Old*</c> identity columns.
/// </item>
/// </list>
/// The first hop may be an identity reference or any securable element the tombstone stores; later hops are
/// identity-only, which is what <see cref="SecurableElementColumnPathResolver"/> already returns. A first
/// hop whose values are not on the tombstone fails planning with
/// <see cref="RelationshipAuthorizationFailureKind.CustomViewBasisNotIdentifyingOrSecurable"/>. The path
/// is the one the live page planner prefers, so a view authorizes the same rows on both surfaces.
/// </summary>
/// <remarks>
/// A descriptor basis (the path terminates on <c>dms.Descriptor</c>) is the third shape: the tombstone's old
/// Namespace/CodeValue seek the shared descriptor table under the basis descriptor's discriminator, with an
/// optional probe of the shared descriptor tombstone table. It obeys the same first-hop rule.
/// </remarks>
internal static class ReadChangesCustomViewPlanner
{
    private const string IncludingDeletesSuffix = "IncludingDeletes";
    private const string JsonPathRootPrefix = "$.";
    private static readonly DbSchemaName _authSchema = new("auth");
    private static readonly DbTableName _descriptorTable = new(new DbSchemaName("dms"), "Descriptor");

    /// <summary>The model-set inputs shared by every strategy of one request.</summary>
    private sealed record PlanningContext(
        DerivedRelationalModelSet ModelSet,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> ResourceLookup,
        ConcreteResourceModel Subject,
        TrackedChangeTableInfo TrackedChangeTable
    );

    /// <summary>
    /// One identity part of the basis: where the basis stores it, and the terminal reference binding that
    /// carries it toward the subject.
    /// </summary>
    /// <param name="BasisColumn">
    /// The canonical column on the basis root table (a descriptor FK for a descriptor part), or the union
    /// view output column for an abstract basis.
    /// </param>
    /// <param name="BasisIdentityPath">The canonical identity JSON path of the part on the basis resource.</param>
    /// <param name="DescriptorResource">The descriptor resource of a descriptor part; null for a scalar.</param>
    /// <param name="UnionOutputIndex">
    /// For an abstract basis, the index of <paramref name="BasisColumn"/> in the view's select list, used to
    /// read each member arm's projected column; null for a concrete basis.
    /// </param>
    /// <param name="TerminalBinding">The terminal hop's identity binding that stores this part.</param>
    private sealed record BasisIdentityPart(
        DbColumnName BasisColumn,
        string BasisIdentityPath,
        QualifiedResourceName? DescriptorResource,
        int? UnionOutputIndex,
        ReferenceIdentityBinding TerminalBinding
    );

    /// <summary>Where the live seek reads the basis, with its identity parts.</summary>
    private sealed record BasisSeekTarget(
        DbTableName Table,
        DbColumnName DocumentIdColumn,
        IReadOnlyList<BasisIdentityPart> Parts,
        AbstractUnionViewInfo? UnionView
    );

    private sealed record PairedScalarPart(BasisIdentityPart Part, DbColumnName TrackedOldColumn);

    private sealed record PairedDescriptorPart(
        BasisIdentityPart Part,
        DbColumnName TrackedOldNamespaceColumn,
        DbColumnName TrackedOldCodeValueColumn
    );

    public static ReadChangesCustomViewPlanResult Plan(
        MappingSet mappingSet,
        ConcreteResourceModel resource,
        TrackedChangeTableInfo trackedChangeTable,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> customViewStrategies
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(trackedChangeTable);
        ArgumentNullException.ThrowIfNull(customViewStrategies);

        List<ReadChangesCustomViewCheckSpec> checks = [];
        List<RelationshipAuthorizationFailureMetadata> failures = [];
        QualifiedResourceName subjectResource = resource.RelationalModel.Resource;
        var context = new PlanningContext(
            mappingSet.Model,
            mappingSet.Model.GetConcreteResourceModelsByResource(),
            resource,
            trackedChangeTable
        );

        foreach (var strategy in customViewStrategies)
        {
            string strategyName = strategy.ConfiguredStrategy.StrategyName;
            var view = new DbTableName(_authSchema, strategyName);
            bool probeBasisTombstones = strategyName.EndsWith(
                IncludingDeletesSuffix,
                StringComparison.Ordinal
            );

            ReadChangesCustomViewBasis? basis;
            if (strategy.BasisResource == subjectResource)
            {
                // Self basis: the tombstone's own DocumentId is the basis DocumentId; no path to resolve.
                basis = new ReadChangesCustomViewBasis.StoredDocumentId(
                    RequireSystemColumn(trackedChangeTable, TrackedChangeSystemColumnRole.DocumentId)
                );
            }
            else if (
                SecurableElementColumnPathResolver.ResolveBasisResourcePathWithMetadata(
                    subjectResource,
                    strategy.BasisResource,
                    mappingSet.Model
                )
                is not { Steps.Count: > 0 } path
            )
            {
                failures.Add(BuildNoJoinPathFailure(subjectResource, strategy));
                continue;
            }
            else if (
                path.Steps is [var selfStep]
                && selfStep.SourceTable == resource.RelationalModel.Root.Table
                && selfStep.SourceColumnName
                    == PersonJoinPathResolver.ResolveToCanonicalColumn(
                        resource.RelationalModel.Root,
                        RelationalNameConventions.DocumentIdColumnName
                    )
            )
            {
                // An abstract basis also resolves to the subject's own DocumentId when the subject is
                // one of its concrete members. This is a self path, not a document reference to seek.
                basis = new ReadChangesCustomViewBasis.StoredDocumentId(
                    RequireSystemColumn(trackedChangeTable, TrackedChangeSystemColumnRole.DocumentId)
                );
            }
            else if (
                PersonKindOf(strategy.BasisResource) is { } personKind
                && ResolveStoredPersonBasis(trackedChangeTable, personKind, path.Steps) is { } storedPerson
            )
            {
                // A person securable path stores the person DocumentId on the tombstone. A person reference
                // that is not a securable element of the subject has no such column and is treated like any
                // other basis: sought live by the unique id when the reference is identifying, and otherwise
                // reported under the first-hop rule (ODS applies its identifying-only rule to StaffUSI alike).
                basis = storedPerson;
            }
            else if (IsDescriptorBasisPath(path.Steps))
            {
                basis = ResolveDescriptorSeek(context, strategy, path.Steps, probeBasisTombstones, failures);
                if (basis is null)
                {
                    continue;
                }
            }
            else
            {
                basis = ResolveLiveSeek(context, strategy, path.Steps, probeBasisTombstones, failures);
                if (basis is null)
                {
                    continue;
                }
            }

            checks.Add(
                new ReadChangesCustomViewCheckSpec(
                    strategy.ConfiguredStrategy,
                    strategy.AuthorizationLocalOrder,
                    strategy.BasisResource,
                    view,
                    probeBasisTombstones,
                    basis
                )
            );
        }

        return new ReadChangesCustomViewPlanResult(checks, failures);
    }

    private static SecurableElementKind? PersonKindOf(QualifiedResourceName basisResource)
    {
        if (PersonJoinPathResolver.IsPersonResource(basisResource, "Student"))
        {
            return SecurableElementKind.Student;
        }
        if (PersonJoinPathResolver.IsPersonResource(basisResource, "Contact"))
        {
            return SecurableElementKind.Contact;
        }
        if (PersonJoinPathResolver.IsPersonResource(basisResource, "Staff"))
        {
            return SecurableElementKind.Staff;
        }
        return null;
    }

    /// <summary>
    /// Picks the stored person <c>Old*_DocumentId</c> column whose person join chain walks the same
    /// root-table columns as the resolved basis path. Chains are compared hop by hop on the source table
    /// and column: the two resolvers agree on those, while the person chain also names the reached table
    /// and the basis path leaves its terminal hop's target unset.
    /// </summary>
    private static ReadChangesCustomViewBasis.StoredDocumentId? ResolveStoredPersonBasis(
        TrackedChangeTableInfo trackedChangeTable,
        SecurableElementKind personKind,
        IReadOnlyList<ColumnPathStep> basisPath
    )
    {
        TrackedChangePersonJoinInfo[] matchingJoins =
        [
            .. trackedChangeTable.PersonJoins.Where(join =>
                join.PersonKind == personKind && SameSourceColumns(join.JoinPath, basisPath)
            ),
        ];
        if (matchingJoins.Length != 1)
        {
            return null;
        }

        TrackedChangeColumnInfo[] personColumns =
        [
            .. trackedChangeTable.ValueColumnsInTableOrder.Where(column =>
                column.Role is TrackedChangeColumnRole.PersonDocumentId
                && string.Equals(
                    column.PersonJoinName,
                    matchingJoins[0].PersonJoinName,
                    StringComparison.Ordinal
                )
            ),
        ];

        return personColumns.Length == 1
            ? new ReadChangesCustomViewBasis.StoredDocumentId(personColumns[0].OldColumnName)
            : null;
    }

    private static bool SameSourceColumns(
        IReadOnlyList<ColumnPathStep> joinPath,
        IReadOnlyList<ColumnPathStep> basisPath
    ) =>
        joinPath.Count == basisPath.Count
        && joinPath
            .Zip(basisPath)
            .All(pair =>
                pair.First.SourceTable == pair.Second.SourceTable
                && pair.First.SourceColumnName == pair.Second.SourceColumnName
            );

    /// <summary>
    /// The resolver ends a descriptor-basis path on <c>dms.Descriptor</c> (a reference-basis path leaves
    /// its terminal target unset).
    /// </summary>
    private static bool IsDescriptorBasisPath(IReadOnlyList<ColumnPathStep> path) =>
        path[^1].TargetTable is { } terminalTarget && terminalTarget.Equals(_descriptorTable);

    // ---- Live seek -------------------------------------------------------------------------------

    /// <summary>
    /// Pairs every basis identity part with the subject tombstone column holding its old value, then adds
    /// the tombstone probe arms when the strategy opts in. Returns null (after recording the
    /// <see cref="RelationshipAuthorizationFailureKind.CustomViewBasisNotIdentifyingOrSecurable"/> failure)
    /// when the first hop's values are not on the tombstone.
    /// </summary>
    private static ReadChangesCustomViewBasis.LiveSeek? ResolveLiveSeek(
        PlanningContext context,
        SupportedCustomViewAuthorizationStrategy strategy,
        IReadOnlyList<ColumnPathStep> path,
        bool probeBasisTombstones,
        List<RelationshipAuthorizationFailureMetadata> failures
    )
    {
        IReadOnlyList<DocumentReferenceBinding> hops = ResolveHopBindings(context, path);
        BasisSeekTarget target = ResolveBasisSeekTarget(context, strategy.BasisResource, hops[^1]);
        DbTableModel subjectRoot = context.Subject.RelationalModel.Root;

        List<PairedScalarPart> scalarParts = [];
        List<PairedDescriptorPart> descriptorParts = [];
        foreach (BasisIdentityPart part in target.Parts)
        {
            (DbColumnName subjectColumn, string subjectJsonPath) = WalkToSubjectRoot(
                hops,
                subjectRoot,
                part.TerminalBinding
            );

            if (part.DescriptorResource is null)
            {
                DbColumnName? trackedOld = TryResolveTrackedScalar(
                    context.TrackedChangeTable,
                    subjectColumn,
                    subjectJsonPath
                );
                if (trackedOld is null)
                {
                    failures.Add(
                        BuildNotIdentifyingOrSecurableFailure(
                            context,
                            strategy,
                            hops[0].ReferenceObjectPath.Canonical
                        )
                    );
                    return null;
                }
                AddDistinct(scalarParts, new PairedScalarPart(part, trackedOld.Value));
            }
            else
            {
                (DbColumnName Namespace, DbColumnName CodeValue)? trackedOld = TryResolveTrackedDescriptor(
                    context.TrackedChangeTable,
                    subjectRoot,
                    subjectColumn
                );
                if (trackedOld is null)
                {
                    failures.Add(
                        BuildNotIdentifyingOrSecurableFailure(
                            context,
                            strategy,
                            hops[0].ReferenceObjectPath.Canonical
                        )
                    );
                    return null;
                }
                AddDistinct(
                    descriptorParts,
                    new PairedDescriptorPart(part, trackedOld.Value.Namespace, trackedOld.Value.CodeValue)
                );
            }
        }

        return new ReadChangesCustomViewBasis.LiveSeek(
            target.Table,
            target.DocumentIdColumn,
            [
                .. scalarParts.Select(static pair => new ReadChangesCustomViewKeyPair(
                    pair.Part.BasisColumn,
                    pair.TrackedOldColumn
                )),
            ],
            [
                .. descriptorParts.Select(static pair => new ReadChangesCustomViewDescriptorKeyPair(
                    pair.Part.BasisColumn,
                    pair.TrackedOldNamespaceColumn,
                    pair.TrackedOldCodeValueColumn,
                    pair.Part.DescriptorResource!.Value
                )),
            ],
            probeBasisTombstones
                ? BuildProbeArms(context, strategy.BasisResource, target, scalarParts, descriptorParts)
                : []
        );
    }

    /// <summary>
    /// Two basis identity parts can land on one unified subject column (a school id carried by two of the
    /// basis's references, say); the seek compares each (basis column, tombstone column) pair once.
    /// </summary>
    private static void AddDistinct(List<PairedScalarPart> parts, PairedScalarPart part)
    {
        if (
            !parts.Exists(existing =>
                existing.Part.BasisColumn == part.Part.BasisColumn
                && existing.TrackedOldColumn == part.TrackedOldColumn
            )
        )
        {
            parts.Add(part);
        }
    }

    private static void AddDistinct(List<PairedDescriptorPart> parts, PairedDescriptorPart part)
    {
        if (
            !parts.Exists(existing =>
                existing.Part.BasisColumn == part.Part.BasisColumn
                && existing.TrackedOldNamespaceColumn == part.TrackedOldNamespaceColumn
                && existing.TrackedOldCodeValueColumn == part.TrackedOldCodeValueColumn
            )
        )
        {
            parts.Add(part);
        }
    }

    /// <summary>
    /// The root reference each path step leaves through: step <c>i</c> is on resource <c>i</c> (the subject
    /// for <c>i = 0</c>) and its canonical FK column names the binding. The resolver built the steps from
    /// these same bindings, so a miss is a model inconsistency rather than a configuration error.
    /// </summary>
    private static IReadOnlyList<DocumentReferenceBinding> ResolveHopBindings(
        PlanningContext context,
        IReadOnlyList<ColumnPathStep> path
    )
    {
        List<DocumentReferenceBinding> hops = [];
        RelationalResourceModel current = context.Subject.RelationalModel;
        foreach (ColumnPathStep step in path)
        {
            DbTableModel root = current.Root;
            DocumentReferenceBinding binding =
                current.DocumentReferenceBindings.FirstOrDefault(candidate =>
                    candidate.Table.Equals(root.Table)
                    && PersonJoinPathResolver.ResolveToCanonicalColumn(root, candidate.FkColumn)
                        == step.SourceColumnName
                )
                ?? throw new InvalidOperationException(
                    $"Resource '{FormatResource(current.Resource)}' has no root reference stored in column '{step.SourceColumnName.Value}' for the resolved custom view basis path."
                );
            hops.Add(binding);

            if (step.TargetTable is not null)
            {
                current = context.ResourceLookup.TryGetValue(binding.TargetResource, out var next)
                    ? next.RelationalModel
                    : throw new InvalidOperationException(
                        $"Resource '{FormatResource(binding.TargetResource)}' on the resolved custom view basis path is not in the mapping set."
                    );
            }
        }

        return hops;
    }

    /// <summary>
    /// Follows one basis identity part from the terminal hop back to the subject root: at each hop the
    /// carried value is an identity path of the next resource, and the previous hop's binding for that path
    /// names the column and reference path one resource closer to the subject. Returns the canonical subject
    /// root column and the subject-relative JSON path of the value.
    /// </summary>
    private static (DbColumnName Column, string JsonPath) WalkToSubjectRoot(
        IReadOnlyList<DocumentReferenceBinding> hops,
        DbTableModel subjectRoot,
        ReferenceIdentityBinding terminalBinding
    )
    {
        ReferenceIdentityBinding current = terminalBinding;
        for (int hop = hops.Count - 2; hop >= 0; hop--)
        {
            current = RequireCarriedIdentity(hops[hop], current.ReferenceJsonPath.Canonical);
        }

        return (
            PersonJoinPathResolver.ResolveToCanonicalColumn(subjectRoot, current.Column),
            current.ReferenceJsonPath.Canonical
        );
    }

    /// <summary>
    /// Where the seek reads the basis and which identity parts it needs. Four cases, by whether the basis and
    /// the terminal reference's target are abstract: a concrete basis referenced directly reads its root
    /// table by its own identity paths; a concrete basis reached through a reference to its abstract
    /// supertype maps the union view's identity outputs through the basis's arm to its own columns; an
    /// abstract basis reads the union view, mapping the outputs to the terminal binding either directly or,
    /// when the reference targets a concrete member, through that member's arm.
    /// </summary>
    private static BasisSeekTarget ResolveBasisSeekTarget(
        PlanningContext context,
        QualifiedResourceName basisResource,
        DocumentReferenceBinding terminalHop
    )
    {
        AbstractUnionViewInfo? basisView = FindUnionView(context.ModelSet, basisResource);
        if (basisView is null)
        {
            return ResolveConcreteBasisSeekTarget(context, basisResource, terminalHop);
        }

        DbColumnName viewDocumentId =
            basisView
                .OutputColumnsInSelectOrder.FirstOrDefault(static output =>
                    output.ColumnName == RelationalNameConventions.DocumentIdColumnName
                )
                ?.ColumnName
            ?? throw new InvalidOperationException(
                $"Abstract union view '{basisView.ViewName}' has no '{RelationalNameConventions.DocumentIdColumnName.Value}' output column."
            );
        AbstractUnionViewArm? memberArm =
            terminalHop.TargetResource == basisResource
                ? null
                : RequireArm(basisView, terminalHop.TargetResource);
        DbTableModel? memberRoot = memberArm is null
            ? null
            : RequireRoot(context, terminalHop.TargetResource);

        List<BasisIdentityPart> parts = [];
        foreach ((AbstractUnionViewOutputColumn output, int index) in IdentityOutputs(basisView))
        {
            string identityPath = output.SourceJsonPath!.Value.Canonical;
            string identityPathOnTerminalTarget =
                memberArm is null || memberRoot is null
                    ? identityPath
                    : RequireSourceJsonPath(memberRoot, RequireProjectedColumn(memberArm, index));
            parts.Add(
                new BasisIdentityPart(
                    output.ColumnName,
                    identityPath,
                    output.IsDescriptorReference
                        ? output.TargetResource
                            ?? throw new InvalidOperationException(
                                $"Descriptor output column '{output.ColumnName.Value}' of '{basisView.ViewName}' names no descriptor resource."
                            )
                        : null,
                    index,
                    RequireCarriedIdentity(terminalHop, identityPathOnTerminalTarget)
                )
            );
        }

        return new BasisSeekTarget(basisView.ViewName, viewDocumentId, parts, basisView);
    }

    private static BasisSeekTarget ResolveConcreteBasisSeekTarget(
        PlanningContext context,
        QualifiedResourceName basisResource,
        DocumentReferenceBinding terminalHop
    )
    {
        RelationalResourceModel basisModel = RequireResource(context, basisResource).RelationalModel;
        DbTableModel basisRoot = basisModel.Root;
        DbColumnName documentId = PersonJoinPathResolver.ResolveToCanonicalColumn(
            basisRoot,
            RelationalNameConventions.DocumentIdColumnName
        );

        List<BasisIdentityPart> parts = [];
        AbstractUnionViewInfo? terminalView =
            terminalHop.TargetResource == basisResource
                ? null
                : FindUnionView(context.ModelSet, terminalHop.TargetResource);
        if (terminalView is null)
        {
            foreach (ReferenceIdentityBinding binding in terminalHop.IdentityBindings)
            {
                parts.Add(DescribeConcretePart(basisModel, binding.IdentityJsonPath.Canonical, binding));
            }
        }
        else
        {
            // The reference targets the abstract supertype; the basis's own arm says which of its columns
            // each identity output projects.
            AbstractUnionViewArm basisArm = RequireArm(terminalView, basisResource);
            foreach ((AbstractUnionViewOutputColumn output, int index) in IdentityOutputs(terminalView))
            {
                parts.Add(
                    DescribeConcretePart(
                        basisModel,
                        RequireSourceJsonPath(basisRoot, RequireProjectedColumn(basisArm, index)),
                        RequireCarriedIdentity(terminalHop, output.SourceJsonPath!.Value.Canonical)
                    )
                );
            }
        }

        return new BasisSeekTarget(basisRoot.Table, documentId, parts, null);
    }

    /// <summary>
    /// Locates one identity part on a concrete basis root: a descriptor edge at the identity path makes it
    /// a descriptor part (basis column = the descriptor FK); otherwise the root column sourced from that
    /// path, canonicalized under key unification.
    /// </summary>
    private static BasisIdentityPart DescribeConcretePart(
        RelationalResourceModel basisModel,
        string identityPath,
        ReferenceIdentityBinding terminalBinding
    )
    {
        DbTableModel root = basisModel.Root;
        DescriptorEdgeSource? descriptorEdge = basisModel.DescriptorEdgeSources.FirstOrDefault(edge =>
            edge.Table.Equals(root.Table)
            && string.Equals(edge.DescriptorValuePath.Canonical, identityPath, StringComparison.Ordinal)
        );
        if (descriptorEdge is not null)
        {
            return new BasisIdentityPart(
                PersonJoinPathResolver.ResolveToCanonicalColumn(root, descriptorEdge.FkColumn),
                identityPath,
                descriptorEdge.DescriptorResource,
                null,
                terminalBinding
            );
        }

        DbColumnModel column =
            root.Columns.FirstOrDefault(candidate =>
                candidate.SourceJsonPath is { } sourcePath
                && string.Equals(sourcePath.Canonical, identityPath, StringComparison.Ordinal)
            )
            ?? throw new InvalidOperationException(
                $"Basis resource '{FormatResource(basisModel.Resource)}' stores no root column for identity path '{identityPath}'."
            );
        return new BasisIdentityPart(
            PersonJoinPathResolver.ResolveToCanonicalColumn(root, column.ColumnName),
            identityPath,
            null,
            null,
            terminalBinding
        );
    }

    // ---- Descriptor seek -------------------------------------------------------------------------

    /// <summary>
    /// A descriptor basis. The path's reference hops (none, when the subject root carries the descriptor)
    /// lead to the resource whose root stores the descriptor FK; its descriptor value path is then walked
    /// back to the subject root exactly like a reference identity part, and the subject tombstone's old
    /// Namespace/CodeValue are read through the descriptor join on that column. Nothing on the tombstone
    /// means the descriptor is neither identifying nor securable at the first hop.
    /// </summary>
    private static ReadChangesCustomViewBasis.DescriptorSeek? ResolveDescriptorSeek(
        PlanningContext context,
        SupportedCustomViewAuthorizationStrategy strategy,
        IReadOnlyList<ColumnPathStep> path,
        bool probeBasisTombstones,
        List<RelationshipAuthorizationFailureMetadata> failures
    )
    {
        IReadOnlyList<DocumentReferenceBinding> hops = ResolveHopBindings(
            context,
            [.. path.Take(path.Count - 1)]
        );
        RelationalResourceModel edgeOwner =
            hops.Count == 0
                ? context.Subject.RelationalModel
                : RequireResource(context, hops[^1].TargetResource).RelationalModel;
        DbColumnName terminalColumn = path[^1].SourceColumnName;
        DescriptorEdgeSource edge =
            edgeOwner.DescriptorEdgeSources.FirstOrDefault(candidate =>
                candidate.Table.Equals(edgeOwner.Root.Table)
                && PersonJoinPathResolver.ResolveToCanonicalColumn(edgeOwner.Root, candidate.FkColumn)
                    == terminalColumn
            )
            ?? throw new InvalidOperationException(
                $"Resource '{FormatResource(edgeOwner.Resource)}' has no root descriptor edge stored in column '{terminalColumn.Value}' for the resolved custom view basis path."
            );

        string carriedPath = edge.DescriptorValuePath.Canonical;
        DbColumnName column = edge.FkColumn;
        for (int hop = hops.Count - 1; hop >= 0; hop--)
        {
            // The shared resolver admits a descriptor edge as the terminal step without requiring it to be
            // identifying on its owner (the live paths follow the FK). A tombstone only carries the descriptor
            // when every hop stores it as an identity value, so a miss here is the ODS "Non-identifying
            // properties" rule, not a model defect.
            ReferenceIdentityBinding? binding = TryFindCarriedIdentity(hops[hop], carriedPath);
            if (binding is null)
            {
                failures.Add(
                    BuildDescriptorBasisNotIdentifyingOnIntermediateFailure(
                        context,
                        strategy,
                        hops,
                        hops[hop].TargetResource,
                        carriedPath
                    )
                );
                return null;
            }
            carriedPath = binding.ReferenceJsonPath.Canonical;
            column = binding.Column;
        }
        DbColumnName subjectColumn = PersonJoinPathResolver.ResolveToCanonicalColumn(
            context.Subject.RelationalModel.Root,
            column
        );

        (DbColumnName Namespace, DbColumnName CodeValue)? trackedOld = TryResolveTrackedDescriptor(
            context.TrackedChangeTable,
            context.Subject.RelationalModel.Root,
            subjectColumn
        );
        if (trackedOld is null)
        {
            string firstHopJsonPath =
                hops.Count == 0 ? edge.DescriptorValuePath.Canonical : hops[0].ReferenceObjectPath.Canonical;
            failures.Add(BuildNotIdentifyingOrSecurableFailure(context, strategy, firstHopJsonPath));
            return null;
        }

        return new ReadChangesCustomViewBasis.DescriptorSeek(
            edge.DescriptorResource,
            trackedOld.Value.Namespace,
            trackedOld.Value.CodeValue,
            probeBasisTombstones ? BuildDescriptorProbeArm(context) : null
        );
    }

    /// <summary>
    /// The one shared descriptor tracked-change table, as the probe reads it: its Discriminator and DocumentId
    /// system columns and the old Namespace/CodeValue value columns.
    /// </summary>
    private static ReadChangesCustomViewDescriptorProbeArm BuildDescriptorProbeArm(PlanningContext context)
    {
        TrackedChangeTableInfo descriptorTracked =
            context.ModelSet.TrackedChangeTablesInNameOrder.FirstOrDefault(static table =>
                table.Kind == TrackedChangeTableKind.SharedDescriptor
            )
            ?? throw new InvalidOperationException(
                "The mapping set has no shared descriptor tracked-change table, so descriptor tombstones cannot be probed."
            );

        return new ReadChangesCustomViewDescriptorProbeArm(
            descriptorTracked.Table,
            RequireSystemColumn(descriptorTracked, TrackedChangeSystemColumnRole.DocumentId),
            RequireSystemColumn(descriptorTracked, TrackedChangeSystemColumnRole.Discriminator),
            RequireSharedDescriptorValue(descriptorTracked, "$.namespace"),
            RequireSharedDescriptorValue(descriptorTracked, "$.codeValue")
        );
    }

    private static DbColumnName RequireSharedDescriptorValue(
        TrackedChangeTableInfo table,
        string sourceJsonPath
    ) =>
        table
            .ValueColumnsInTableOrder.FirstOrDefault(column =>
                column.Role is TrackedChangeColumnRole.Scalar
                && string.Equals(column.SourceJsonPath, sourceJsonPath, StringComparison.Ordinal)
            )
            ?.OldColumnName
        ?? throw new InvalidOperationException(
            $"Shared descriptor tracked-change table '{table.Table}' has no old value column for '{sourceJsonPath}'."
        );

    // ---- Tombstone probe -------------------------------------------------------------------------

    private static IReadOnlyList<ReadChangesCustomViewProbeArm> BuildProbeArms(
        PlanningContext context,
        QualifiedResourceName basisResource,
        BasisSeekTarget target,
        IReadOnlyList<PairedScalarPart> scalarParts,
        IReadOnlyList<PairedDescriptorPart> descriptorParts
    )
    {
        if (target.UnionView is null)
        {
            TrackedChangeTableInfo basisTracked = RequireTrackedTable(context, target.Table);
            DbTableModel basisRoot = RequireRoot(context, basisResource);
            return
            [
                new ReadChangesCustomViewProbeArm(
                    basisTracked.Table,
                    RequireSystemColumn(basisTracked, TrackedChangeSystemColumnRole.DocumentId),
                    [
                        .. scalarParts.Select(pair => new ReadChangesCustomViewKeyPair(
                            RequireTrackedScalar(
                                basisTracked,
                                pair.Part.BasisColumn,
                                pair.Part.BasisIdentityPath
                            ),
                            pair.TrackedOldColumn
                        )),
                    ],
                    [
                        .. descriptorParts.Select(pair =>
                            BuildProbeDescriptorPair(
                                RequireTrackedDescriptor(basisTracked, basisRoot, pair.Part.BasisColumn),
                                pair
                            )
                        ),
                    ]
                ),
            ];
        }

        // One arm per concrete member: each output column projects a member column, whose old value the
        // member's tracked-change table stores under its own identity path.
        List<ReadChangesCustomViewProbeArm> arms = [];
        foreach (AbstractUnionViewArm arm in target.UnionView.UnionArmsInOrder)
        {
            DbTableModel memberRoot = RequireRoot(context, arm.ConcreteMemberResourceKey.Resource);
            TrackedChangeTableInfo memberTracked = RequireTrackedTable(context, arm.FromTable);
            arms.Add(
                new ReadChangesCustomViewProbeArm(
                    memberTracked.Table,
                    RequireSystemColumn(memberTracked, TrackedChangeSystemColumnRole.DocumentId),
                    [
                        .. scalarParts.Select(pair =>
                        {
                            DbColumnName memberColumn = RequireProjectedColumn(
                                arm,
                                pair.Part.UnionOutputIndex!.Value
                            );
                            return new ReadChangesCustomViewKeyPair(
                                RequireTrackedScalar(
                                    memberTracked,
                                    PersonJoinPathResolver.ResolveToCanonicalColumn(memberRoot, memberColumn),
                                    RequireSourceJsonPath(memberRoot, memberColumn)
                                ),
                                pair.TrackedOldColumn
                            );
                        }),
                    ],
                    [
                        .. descriptorParts.Select(pair =>
                            BuildProbeDescriptorPair(
                                RequireTrackedDescriptor(
                                    memberTracked,
                                    memberRoot,
                                    RequireProjectedColumn(arm, pair.Part.UnionOutputIndex!.Value)
                                ),
                                pair
                            )
                        ),
                    ]
                )
            );
        }

        return arms;
    }

    private static ReadChangesCustomViewProbeDescriptorKeyPair BuildProbeDescriptorPair(
        (DbColumnName Namespace, DbColumnName CodeValue) basisOld,
        PairedDescriptorPart pair
    ) =>
        new(
            basisOld.Namespace,
            basisOld.CodeValue,
            pair.TrackedOldNamespaceColumn,
            pair.TrackedOldCodeValueColumn
        );

    // ---- Tracked-column lookups ------------------------------------------------------------------

    /// <summary>
    /// The tracked scalar column holding a root column's old value: matched on the canonical storage column
    /// for a key-unified value, otherwise on the source JSON path (a tracked column records only one of the
    /// unified paths, so the path alone would miss the siblings).
    /// </summary>
    private static DbColumnName? TryResolveTrackedScalar(
        TrackedChangeTableInfo table,
        DbColumnName canonicalColumn,
        string jsonPath
    ) =>
        table
            .ValueColumnsInTableOrder.FirstOrDefault(column =>
                column.Role is TrackedChangeColumnRole.Scalar
                && (
                    column.CanonicalStorageColumn == canonicalColumn
                    || (
                        column.CanonicalStorageColumn is null
                        && string.Equals(column.SourceJsonPath, jsonPath, StringComparison.Ordinal)
                    )
                )
            )
            ?.OldColumnName;

    private static DbColumnName RequireTrackedScalar(
        TrackedChangeTableInfo table,
        DbColumnName canonicalColumn,
        string jsonPath
    ) =>
        TryResolveTrackedScalar(table, canonicalColumn, jsonPath)
        ?? throw new InvalidOperationException(
            $"Tracked-change table '{table.Table}' stores no old value for identity column '{canonicalColumn.Value}' ('{jsonPath}')."
        );

    /// <summary>
    /// The tracked old Namespace/CodeValue pair of a descriptor FK column, through the table-level
    /// descriptor join declared on that column. Tracked joins retain their source alias columns, so both
    /// sides are resolved to canonical storage to match descriptor keys shared by multiple references.
    /// </summary>
    private static (DbColumnName Namespace, DbColumnName CodeValue)? TryResolveTrackedDescriptor(
        TrackedChangeTableInfo table,
        DbTableModel sourceRoot,
        DbColumnName descriptorFkColumn
    )
    {
        DbColumnName canonicalColumn = PersonJoinPathResolver.ResolveToCanonicalColumn(
            sourceRoot,
            descriptorFkColumn
        );
        TrackedChangeDescriptorJoinInfo? join = table.DescriptorJoins.FirstOrDefault(candidate =>
            PersonJoinPathResolver.ResolveToCanonicalColumn(sourceRoot, candidate.SourceColumn)
            == canonicalColumn
        );
        if (join is null)
        {
            return null;
        }

        DbColumnName? namespaceColumn = FindDescriptorPart(
            table,
            join.DescriptorJoinName,
            TrackedChangeColumnRole.DescriptorNamespace
        );
        DbColumnName? codeValueColumn = FindDescriptorPart(
            table,
            join.DescriptorJoinName,
            TrackedChangeColumnRole.DescriptorCodeValue
        );
        return namespaceColumn is null || codeValueColumn is null
            ? null
            : (namespaceColumn.Value, codeValueColumn.Value);
    }

    private static DbColumnName? FindDescriptorPart(
        TrackedChangeTableInfo table,
        string descriptorJoinName,
        TrackedChangeColumnRole role
    ) =>
        table
            .ValueColumnsInTableOrder.FirstOrDefault(column =>
                column.Role == role
                && string.Equals(column.DescriptorJoinName, descriptorJoinName, StringComparison.Ordinal)
            )
            ?.OldColumnName;

    private static (DbColumnName Namespace, DbColumnName CodeValue) RequireTrackedDescriptor(
        TrackedChangeTableInfo table,
        DbTableModel sourceRoot,
        DbColumnName descriptorFkColumn
    ) =>
        TryResolveTrackedDescriptor(table, sourceRoot, descriptorFkColumn)
        ?? throw new InvalidOperationException(
            $"Tracked-change table '{table.Table}' stores no old Namespace/CodeValue for descriptor column '{descriptorFkColumn.Value}'."
        );

    // ---- Model lookups ---------------------------------------------------------------------------

    private static AbstractUnionViewInfo? FindUnionView(
        DerivedRelationalModelSet modelSet,
        QualifiedResourceName resource
    ) =>
        modelSet.AbstractUnionViewsInNameOrder.FirstOrDefault(view =>
            view.AbstractResourceKey.Resource == resource
        );

    /// <summary>The identity output columns of a union view (those sourced from a JSON path), with their select-list index.</summary>
    private static IEnumerable<(AbstractUnionViewOutputColumn Output, int Index)> IdentityOutputs(
        AbstractUnionViewInfo view
    ) =>
        view
            .OutputColumnsInSelectOrder.Select(static (output, index) => (Output: output, Index: index))
            .Where(static pair => pair.Output.SourceJsonPath is not null);

    private static AbstractUnionViewArm RequireArm(
        AbstractUnionViewInfo view,
        QualifiedResourceName member
    ) =>
        view.UnionArmsInOrder.FirstOrDefault(arm => arm.ConcreteMemberResourceKey.Resource == member)
        ?? throw new InvalidOperationException(
            $"Abstract union view '{view.ViewName}' has no arm for member resource '{FormatResource(member)}'."
        );

    private static DbColumnName RequireProjectedColumn(AbstractUnionViewArm arm, int outputIndex) =>
        arm.ProjectionExpressionsInSelectOrder[outputIndex]
            is AbstractUnionViewProjectionExpression.SourceColumn sourceColumn
            ? sourceColumn.ColumnName
            : throw new InvalidOperationException(
                $"Union arm '{arm.FromTable}' projects no source column at identity output position {outputIndex}."
            );

    /// <summary>The binding through which <paramref name="hop"/> stores its target's identity value at <paramref name="identityPathOnTarget"/>.</summary>
    private static ReferenceIdentityBinding? TryFindCarriedIdentity(
        DocumentReferenceBinding hop,
        string identityPathOnTarget
    ) =>
        hop.IdentityBindings.FirstOrDefault(binding =>
            string.Equals(binding.IdentityJsonPath.Canonical, identityPathOnTarget, StringComparison.Ordinal)
        );

    private static ReferenceIdentityBinding RequireCarriedIdentity(
        DocumentReferenceBinding hop,
        string identityPathOnTarget
    ) =>
        TryFindCarriedIdentity(hop, identityPathOnTarget)
        ?? throw new InvalidOperationException(
            $"Reference '{hop.ReferenceObjectPath.Canonical}' to '{FormatResource(hop.TargetResource)}' does not store the identity value '{identityPathOnTarget}' of its target."
        );

    private static string RequireSourceJsonPath(DbTableModel root, DbColumnName column) =>
        root.Columns.FirstOrDefault(candidate => candidate.ColumnName == column)?.SourceJsonPath?.Canonical
        ?? throw new InvalidOperationException(
            $"Root table '{root.Table}' has no source JSON path for identity column '{column.Value}'."
        );

    private static ConcreteResourceModel RequireResource(
        PlanningContext context,
        QualifiedResourceName resource
    ) =>
        context.ResourceLookup.TryGetValue(resource, out var model)
            ? model
            : throw new InvalidOperationException(
                $"Resource '{FormatResource(resource)}' is not in the mapping set."
            );

    private static DbTableModel RequireRoot(PlanningContext context, QualifiedResourceName resource) =>
        RequireResource(context, resource).RelationalModel.Root;

    private static TrackedChangeTableInfo RequireTrackedTable(
        PlanningContext context,
        DbTableName sourceTable
    ) =>
        context.ModelSet.TrackedChangeTablesInNameOrder.FirstOrDefault(table =>
            table.SourceTable.Equals(sourceTable)
        )
        ?? throw new InvalidOperationException(
            $"No tracked-change table tracks '{sourceTable}', so its tombstones cannot be probed."
        );

    private static DbColumnName RequireSystemColumn(
        TrackedChangeTableInfo trackedChangeTable,
        TrackedChangeSystemColumnRole role
    ) =>
        trackedChangeTable.SystemColumns.SingleOrDefault(column => column.Role == role)?.ColumnName
        ?? throw new InvalidOperationException(
            $"Tracked-change table '{trackedChangeTable.Table.Schema.Value}.{trackedChangeTable.Table.Name}' has no '{role}' system column."
        );

    private static string FormatResource(QualifiedResourceName resource) =>
        $"{resource.ProjectName}.{resource.ResourceName}";

    // ---- Failures --------------------------------------------------------------------------------

    private static RelationshipAuthorizationFailureMetadata BuildNoJoinPathFailure(
        QualifiedResourceName subjectResource,
        SupportedCustomViewAuthorizationStrategy strategy
    ) =>
        new(
            RelationshipAuthorizationFailureKind.NoCustomViewJoinPath,
            subjectResource,
            strategy.ConfiguredStrategy,
            strategy.AuthorizationLocalOrder,
            Location: new RelationshipAuthorizationFailureLocation(
                AuthorizationObjectName: $"auth.{strategy.ConfiguredStrategy.StrategyName}"
            ),
            Hint: $"No DocumentId join path could be resolved from subject resource '{FormatResource(subjectResource)}' to custom view basis resource '{FormatResource(strategy.BasisResource)}'."
        );

    /// <summary>
    /// The first hop resolved on the live paths but the tombstone stores none of its values: the reference
    /// (or descriptor property) at <paramref name="referenceJsonPath"/> is neither part of the subject's
    /// identity nor one of its securable elements (ODS's "Non-identifying properties" rule, widened by the
    /// securable allowance).
    /// </summary>
    private static RelationshipAuthorizationFailureMetadata BuildNotIdentifyingOrSecurableFailure(
        PlanningContext context,
        SupportedCustomViewAuthorizationStrategy strategy,
        string referenceJsonPath
    )
    {
        QualifiedResourceName subjectResource = context.Subject.RelationalModel.Resource;
        string referenceName = StripJsonPathRoot(referenceJsonPath);

        return new RelationshipAuthorizationFailureMetadata(
            RelationshipAuthorizationFailureKind.CustomViewBasisNotIdentifyingOrSecurable,
            subjectResource,
            strategy.ConfiguredStrategy,
            strategy.AuthorizationLocalOrder,
            Location: new RelationshipAuthorizationFailureLocation(
                JsonPath: referenceJsonPath,
                ReadableName: referenceName,
                AuthorizationObjectName: $"auth.{strategy.ConfiguredStrategy.StrategyName}"
            ),
            Hint: CustomViewAuthorizationHintFormatter.FormatBasisNotIdentifyingOrSecurable(
                referenceName,
                subjectResource,
                strategy.BasisResource
            )
        );
    }

    /// <summary>
    /// A descriptor basis reached through identity references whose descriptor property is not identifying
    /// on <paramref name="intermediateResource"/>, so no tombstone column holds its value. Same failure kind
    /// as <see cref="BuildNotIdentifyingOrSecurableFailure"/>; the location is the first hop on the subject
    /// and the hint names the intermediate resource and descriptor property.
    /// </summary>
    private static RelationshipAuthorizationFailureMetadata BuildDescriptorBasisNotIdentifyingOnIntermediateFailure(
        PlanningContext context,
        SupportedCustomViewAuthorizationStrategy strategy,
        IReadOnlyList<DocumentReferenceBinding> hops,
        QualifiedResourceName intermediateResource,
        string descriptorJsonPathOnIntermediate
    )
    {
        QualifiedResourceName subjectResource = context.Subject.RelationalModel.Resource;
        string firstHopJsonPath = hops[0].ReferenceObjectPath.Canonical;
        string firstHopName = StripJsonPathRoot(firstHopJsonPath);

        return new RelationshipAuthorizationFailureMetadata(
            RelationshipAuthorizationFailureKind.CustomViewBasisNotIdentifyingOrSecurable,
            subjectResource,
            strategy.ConfiguredStrategy,
            strategy.AuthorizationLocalOrder,
            Location: new RelationshipAuthorizationFailureLocation(
                JsonPath: firstHopJsonPath,
                ReadableName: firstHopName,
                AuthorizationObjectName: $"auth.{strategy.ConfiguredStrategy.StrategyName}"
            ),
            Hint: CustomViewAuthorizationHintFormatter.FormatDescriptorBasisNotIdentifyingOnIntermediate(
                StripJsonPathRoot(descriptorJsonPathOnIntermediate),
                intermediateResource,
                firstHopName,
                subjectResource,
                strategy.BasisResource
            )
        );
    }

    private static string StripJsonPathRoot(string jsonPath) =>
        jsonPath.StartsWith(JsonPathRootPrefix, StringComparison.Ordinal)
            ? jsonPath[JsonPathRootPrefix.Length..]
            : jsonPath;
}
