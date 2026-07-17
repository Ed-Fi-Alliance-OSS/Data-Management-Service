// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Backend.RelationalModel.Constraints.ConstraintDerivationHelpers;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Assigns final ON UPDATE actions to SQL Server document-reference foreign keys by pruning
/// duplicate cascade paths from the same mutable origin at their convergence, per
/// design-docs/sql-server-pruning.md. Runs immediately before
/// <see cref="ApplyConstraintDialectHashingPass"/> because the constraint hash includes the
/// ON UPDATE action. PostgreSQL models bypass this pass unchanged.
/// </summary>
public sealed class MssqlForeignKeyPruningPass : IRelationalModelSetPass
{
    /// <summary>
    /// For SQL Server only: collects document-reference cascade candidates, rejects candidate
    /// cycles, discovers per-mutable-origin convergences, and changes safe duplicate incoming
    /// edges to full-composite ON UPDATE NO ACTION. The final ForeignKey.OnUpdate action is the
    /// only output; no pruning metadata is retained.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Dialect != SqlDialect.Mssql)
        {
            return;
        }

        var candidates = CollectCandidatesInStableOrder(context);

        RejectCandidateCycles(candidates);

        var cutEdges = SelectCutEdges(
            CollectMutableOriginsInStableOrder(context),
            candidates,
            CollectImmutableAbstractOrigins(context)
        );

        ApplyCutActions(context, cutEdges);
    }

    /// <summary>
    /// Selects the convergence cuts: repeatedly finds the earliest remaining convergence across
    /// origins in stable order, retains the first survivor whose competing incoming edges all pass
    /// the structural safe-cut test, and assigns full-composite ON UPDATE NO ACTION to the cuts.
    /// When a convergence has no locally safe survivor, the nearest earlier decision that assigned
    /// an action to one of this convergence's own incoming edges is retried with its next stable
    /// survivor; a merely reachable, carrier-related, or disjoint earlier decision is never
    /// retried. When neither a safe survivor nor a shared-FK retry exists, a convergence discovered
    /// from an immutable abstract-identity origin (one with no transitively-mutable concrete
    /// subclass) is rescued by retaining the next still-available candidate and cutting the rest:
    /// that origin never renames the shared columns, so the cut guards a rename that cannot happen.
    /// Once backtracking has exhausted that origin's candidates, or for a genuinely mutable origin,
    /// derivation fails as NoSafeSqlServerForeignKeyPruning.
    /// </summary>
    internal static HashSet<PruningEdgeKey> SelectCutEdges(
        IReadOnlyList<string> origins,
        IReadOnlyList<PruningCandidate> candidates,
        IReadOnlySet<string> immutableAbstractOrigins
    )
    {
        List<PruningDecision> decisions = [];
        HashSet<PruningEdgeKey> cutEdges = [];
        Dictionary<PruningEdgeKey, int> retainedEdgeCounts = [];
        Dictionary<(string Origin, string Receiver), int> retryStartIndexByConvergence = new();

        while (true)
        {
            string? origin = null;
            ConvergenceDiscovery? convergence = null;

            foreach (var candidateOrigin in origins)
            {
                convergence = FindEarliestConvergence(candidateOrigin, candidates, cutEdges);

                if (convergence is not null)
                {
                    origin = candidateOrigin;
                    break;
                }
            }

            if (convergence is null || origin is null)
            {
                return cutEdges;
            }

            var startIndex = retryStartIndexByConvergence.GetValueOrDefault((origin, convergence.Receiver));
            var (survivorIndex, firstFailedCondition) = TrySelectSurvivor(
                convergence,
                startIndex,
                origin,
                retainedEdgeCounts
            );

            if (survivorIndex >= 0)
            {
                CommitSurvivor(origin, convergence, survivorIndex, cutEdges, retainedEdgeCounts, decisions);

                continue;
            }

            // Retry is permitted only to reverse an already-assigned action on the exact same
            // physical FK: the nearest earlier decision that assigned an action to one of this
            // convergence's own incoming edges. A disjoint earlier decision at the same physical
            // receiver shares no FK with this convergence and is not a retry dependency.
            var convergenceEdges = convergence.IncomingChoices.Select(choice => choice.EdgeKey).ToHashSet();
            var retryIndex = decisions.FindLastIndex(decision =>
                decision.AssignedEdges.Any(convergenceEdges.Contains)
            );

            if (retryIndex < 0)
            {
                // An immutable abstract-identity origin never renames the shared columns, so a
                // convergence discovered from it guards a rename that cannot happen. With no
                // locally safe survivor and no earlier shared-FK decision to retry, retain the
                // next still-available candidate and cut the rest instead of failing: fewer
                // cascades never violate SQL Server's single-cascade-path rule, and no real update
                // is left uncovered. Once backtracking has advanced startIndex past the last
                // candidate, fall through and fail deterministically instead of wrapping to a
                // candidate already tried. Genuinely mutable origins still fail here.
                if (
                    immutableAbstractOrigins.Contains(origin)
                    && startIndex < convergence.IncomingChoices.Count
                )
                {
                    CommitSurvivor(origin, convergence, startIndex, cutEdges, retainedEdgeCounts, decisions);

                    continue;
                }

                throw BuildNoSafePruningException(origin, convergence, firstFailedCondition);
            }

            // Restore the nearest shared-FK decision and everything after it; later decisions
            // re-derive fresh, and the restored decision resumes at its next stable survivor.
            for (var index = decisions.Count - 1; index >= retryIndex; index--)
            {
                var decision = decisions[index];

                cutEdges.ExceptWith(decision.CutEdges);

                var retainedCount = retainedEdgeCounts[decision.SurvivorEdge] - 1;

                if (retainedCount == 0)
                {
                    retainedEdgeCounts.Remove(decision.SurvivorEdge);
                }
                else
                {
                    retainedEdgeCounts[decision.SurvivorEdge] = retainedCount;
                }

                if (index > retryIndex)
                {
                    retryStartIndexByConvergence.Remove((decision.Origin, decision.Receiver));
                }
                else
                {
                    retryStartIndexByConvergence[(decision.Origin, decision.Receiver)] =
                        decision.SurvivorIndex + 1;
                }

                decisions.RemoveAt(index);
            }
        }
    }

    /// <summary>
    /// Commits a convergence decision: retains the chosen survivor edge, cuts the competing
    /// incoming edges to full-composite ON UPDATE NO ACTION, records the retained-edge count, and
    /// appends the decision so a later convergence can retry it if it shares the same physical FK.
    /// </summary>
    private static void CommitSurvivor(
        string origin,
        ConvergenceDiscovery convergence,
        int survivorIndex,
        HashSet<PruningEdgeKey> cutEdges,
        Dictionary<PruningEdgeKey, int> retainedEdgeCounts,
        List<PruningDecision> decisions
    )
    {
        var survivorEdge = convergence.IncomingChoices[survivorIndex].EdgeKey;
        var cuts = convergence
            .IncomingChoices.Where((_, index) => index != survivorIndex)
            .Select(choice => choice.EdgeKey)
            .ToArray();

        cutEdges.UnionWith(cuts);
        retainedEdgeCounts[survivorEdge] = retainedEdgeCounts.GetValueOrDefault(survivorEdge) + 1;
        decisions.Add(
            new PruningDecision(
                origin,
                convergence.Receiver,
                convergence.IncomingChoices.Select(choice => choice.EdgeKey).ToArray(),
                survivorIndex,
                survivorEdge,
                cuts
            )
        );
    }

    /// <summary>
    /// Tries survivor choices in stable order starting at the given index. Returns the index of
    /// the first survivor for which every competing incoming edge passes the structural safe-cut
    /// test and cuts no edge retained by an earlier decision (one physical constraint has one
    /// action; reversing an earlier retained cascade is a shared-FK action conflict resolved only
    /// through retry), or (-1, first failed condition) when none qualifies.
    /// </summary>
    private static (int SurvivorIndex, string? FirstFailedCondition) TrySelectSurvivor(
        ConvergenceDiscovery convergence,
        int startIndex,
        string origin,
        IReadOnlyDictionary<PruningEdgeKey, int> retainedEdgeCounts
    )
    {
        string? firstFailedCondition = null;

        for (var index = startIndex; index < convergence.IncomingChoices.Count; index++)
        {
            var survivor = convergence.IncomingChoices[index];
            string? failure = null;

            foreach (var cut in convergence.IncomingChoices)
            {
                if (ReferenceEquals(cut, survivor))
                {
                    continue;
                }

                if (retainedEdgeCounts.ContainsKey(cut.EdgeKey))
                {
                    failure =
                        $"shared-FK action conflict: cutting '{cut.ConstraintName}' would reverse "
                        + "the cascade retained by an earlier convergence decision";
                    break;
                }

                failure = EvaluateSafeCut(cut, survivor, convergence.PredecessorEdgeByVertex, origin);

                if (failure is not null)
                {
                    break;
                }
            }

            if (failure is null)
            {
                return (index, null);
            }

            firstFailedCondition ??= failure;
        }

        return (-1, firstFailedCondition);
    }

    /// <summary>
    /// The structural, fail-closed safe-cut test. The carrier obligation applies to the cut's
    /// canonical local identity columns that the retained survivor's native cascade also updates
    /// (shared canonical storage on the same physical receiver table) and that this origin's
    /// rename actually affects: tracing both pairings backward along first-arrival predecessor
    /// edges, a pairing is affected only when it terminates at the origin, and an affected pairing
    /// must terminate identically on both sides with a required survivor binding. A pairing that
    /// steps through a DocumentId anchor depends on a reference replacement no native cascade can
    /// carry. A cut column the survivor does not update has no carrier obligation: the cut keeps
    /// enforcing it as an ordinary NO ACTION reference (SQL Server rejects a rename pinned by such
    /// a reference at runtime, exactly like any non-cascade reference). Returns null when safe,
    /// otherwise the first failed condition. The trace is compared edge by edge and discarded; no
    /// composed mapping or route artifact is created.
    /// </summary>
    private static string? EvaluateSafeCut(
        PruningCandidate cut,
        PruningCandidate survivor,
        IReadOnlyDictionary<string, PruningCandidate> predecessorEdgeByVertex,
        string origin
    )
    {
        if (!cut.ReceiverTable.Equals(survivor.ReceiverTable))
        {
            return $"receiver table: cut '{cut.ConstraintName}' and retained '{survivor.ConstraintName}' "
                + "are not on the same physical receiver table";
        }

        var requirednessChecked = false;

        for (var index = 0; index < cut.LocalColumns.Count - 1; index++)
        {
            var column = cut.LocalColumns[index];
            var survivorIndex = FindColumnIndex(survivor.LocalColumns, column);

            if (survivorIndex < 0)
            {
                // Not updated by the retained cascade, so no carrier obligation: safe-cut rule 1
                // applies only to a column the survivor also updates, so the cut FK keeps enforcing
                // this pairing as an ordinary full-composite NO ACTION reference. Passing it is a
                // safe, supported result of the test, not an unsupported cut — it does not fail
                // derivation. On the standard Ed-Fi surface every disjoint role reference resolves to
                // an immutable abstract identity (EducationOrganization), so no rename ever reaches
                // this pairing and the skip is fully safe. The one case it does not specially guard —
                // a genuinely mutable origin referenced under two disjoint, non-unified roles — does
                // not occur in standard Ed-Fi; its surviving NO ACTION reference is a complete,
                // enforced full-composite reference, and a rename is rejected at runtime by it (a
                // clean SQL Server error, never a corrupt tuple). This is settled, intended behavior,
                // deliberately left to fail closed rather than to fail derivation. See
                // design-docs/sql-server-pruning.md, "Safe cut".
                continue;
            }

            if (survivorIndex == survivor.LocalColumns.Count - 1)
            {
                return $"DocumentId: cut '{cut.ConstraintName}' column '{column.Value}' collides with "
                    + $"the local reference DocumentId anchor of retained '{survivor.ConstraintName}'";
            }

            var survivorSource = TraceToSource(
                FormatTable(survivor.TargetTable),
                survivor.TargetColumns[survivorIndex],
                predecessorEdgeByVertex,
                out var survivorDocumentIdPairing
            );
            var cutSource = TraceToSource(
                FormatTable(cut.TargetTable),
                cut.TargetColumns[index],
                predecessorEdgeByVertex,
                out var cutDocumentIdPairing
            );

            if (survivorDocumentIdPairing || cutDocumentIdPairing)
            {
                return $"DocumentId: cut '{cut.ConstraintName}' column '{column.Value}' pairing depends "
                    + "on a local reference DocumentId move that native cascades cannot carry";
            }

            // A pairing is affected by this origin's rename only when its trace terminates at the
            // origin. A pair static on both sides carries no obligation for this convergence.
            var survivorAffected = survivorSource.Table == origin;
            var cutAffected = cutSource.Table == origin;

            if (!survivorAffected && !cutAffected)
            {
                continue;
            }

            if (survivorAffected != cutAffected)
            {
                return $"canonical columns: cut '{cut.ConstraintName}' column '{column.Value}' pairs to "
                    + $"'{cutSource.Table}.{cutSource.Column}' but retained '{survivor.ConstraintName}' "
                    + $"carries '{survivorSource.Table}.{survivorSource.Column}', which the origin "
                    + "rename does not update consistently";
            }

            if (!requirednessChecked)
            {
                // The carrier must be a required binding: an optional survivor leaves rows whose
                // survivor reference is absent without native coverage for the shared columns,
                // and DMS does not reason about presence implication. An optional cut is safe
                // with a required carrier: populated cut rows are covered through the always
                // present shared storage, and absent cut rows carry no FK obligation.
                if (!survivor.LocalDocumentIdIsRequired)
                {
                    return $"required binding: retained '{survivor.ConstraintName}' shares canonical "
                        + $"columns with cut '{cut.ConstraintName}', so the retained binding must have "
                        + "a non-nullable local reference DocumentId column";
                }

                requirednessChecked = true;
            }

            if (survivorSource != cutSource)
            {
                return $"canonical columns: cut '{cut.ConstraintName}' column '{column.Value}' pairs to "
                    + $"'{cutSource.Table}.{cutSource.Column}' but retained '{survivor.ConstraintName}' "
                    + $"carries '{survivorSource.Table}.{survivorSource.Column}'";
            }
        }

        return null;
    }

    /// <summary>
    /// Follows a column's existing FK pairings backward along the first-arrival predecessor edges
    /// until the column is no longer fed by an upstream cascade edge, returning the ultimate
    /// source column. Sets the DocumentId flag when the pairing steps through a local reference
    /// DocumentId anchor. Follows only the traced chain; it never searches alternate routes.
    /// </summary>
    private static (string Table, string Column) TraceToSource(
        string table,
        DbColumnName column,
        IReadOnlyDictionary<string, PruningCandidate> predecessorEdgeByVertex,
        out bool documentIdPairing
    )
    {
        documentIdPairing = false;

        while (predecessorEdgeByVertex.TryGetValue(table, out var edge))
        {
            var index = FindColumnIndex(edge.LocalColumns, column);

            if (index < 0)
            {
                break;
            }

            if (index == edge.LocalColumns.Count - 1)
            {
                documentIdPairing = true;
                break;
            }

            column = edge.TargetColumns[index];
            table = FormatTable(edge.TargetTable);
        }

        return (table, column.Value);
    }

    /// <summary>
    /// Finds a column's position in an ordered FK column list, or -1 when absent.
    /// </summary>
    private static int FindColumnIndex(IReadOnlyList<DbColumnName> columns, DbColumnName column)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (columns[index].Equals(column))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Rewrites the selected cut foreign keys to ON UPDATE NO ACTION on their receiver tables.
    /// This is the pass's only output; retained candidates keep their initial cascade action.
    /// </summary>
    private static void ApplyCutActions(
        RelationalModelSetBuilderContext context,
        HashSet<PruningEdgeKey> cutEdges
    )
    {
        if (cutEdges.Count == 0)
        {
            return;
        }

        for (var index = 0; index < context.ConcreteResourcesInNameOrder.Count; index++)
        {
            var model = context.ConcreteResourcesInNameOrder[index];
            var resourceModel = model.RelationalModel;
            var changed = false;
            var rewrittenTables = new DbTableModel[resourceModel.TablesInDependencyOrder.Count];
            var rewrittenRoot = resourceModel.Root;

            for (var tableIndex = 0; tableIndex < resourceModel.TablesInDependencyOrder.Count; tableIndex++)
            {
                var table = resourceModel.TablesInDependencyOrder[tableIndex];
                var rewritten = RewriteCutConstraints(table, cutEdges, ref changed);

                rewrittenTables[tableIndex] = rewritten;

                if (ReferenceEquals(table, resourceModel.Root))
                {
                    rewrittenRoot = rewritten;
                }
            }

            if (changed)
            {
                context.ConcreteResourcesInNameOrder[index] = model with
                {
                    RelationalModel = resourceModel with
                    {
                        Root = rewrittenRoot,
                        TablesInDependencyOrder = rewrittenTables,
                    },
                };
            }
        }
    }

    /// <summary>
    /// Returns the table with any cut cascade foreign keys rewritten to ON UPDATE NO ACTION, or
    /// the original instance when nothing matches.
    /// </summary>
    private static DbTableModel RewriteCutConstraints(
        DbTableModel table,
        HashSet<PruningEdgeKey> cutEdges,
        ref bool changed
    )
    {
        List<TableConstraint>? rewritten = null;

        for (var index = 0; index < table.Constraints.Count; index++)
        {
            var constraint = table.Constraints[index];

            if (
                constraint is TableConstraint.ForeignKey { OnUpdate: ReferentialAction.Cascade } foreignKey
                && cutEdges.Contains(PruningEdgeKey.From(table.Table, foreignKey))
            )
            {
                rewritten ??= [.. table.Constraints];
                rewritten[index] = foreignKey with { OnUpdate = ReferentialAction.NoAction };
            }
        }

        if (rewritten is null)
        {
            return table;
        }

        changed = true;
        return table with { Constraints = rewritten };
    }

    /// <summary>
    /// Builds the no-safe-pruning diagnostic: the mutable origin, convergence receiver, the
    /// conflicting constraints in stable survivor order, and the first failed safety condition.
    /// The diagnostic describes an unsupported schema; it is not a route-proof artifact.
    /// </summary>
    private static InvalidOperationException BuildNoSafePruningException(
        string origin,
        ConvergenceDiscovery convergence,
        string? firstFailedCondition
    )
    {
        var conflictingConstraints = string.Join(
            ", ",
            convergence.IncomingChoices.Select(choice =>
                $"'{choice.ConstraintName}' ({FormatTable(choice.TargetTable)} -> "
                + $"{FormatTable(choice.ReceiverTable)})"
            )
        );

        return new InvalidOperationException(
            $"NoSafeSqlServerForeignKeyPruning: no safe SQL Server foreign-key pruning exists for "
                + $"mutable origin '{origin}' at convergence receiver '{convergence.Receiver}'. "
                + $"Conflicting constraints in stable survivor order: {conflictingConstraints}. "
                + $"First failed condition: {firstFailedCondition ?? "no survivor choices remain"}."
        );
    }

    /// <summary>
    /// A committed convergence decision: the survivor retained at a receiver for an origin and the
    /// cuts applied, kept only so a later convergence can retry the nearest decision that assigned
    /// an action to the exact same physical FK. Pass-local only; discarded after selection.
    /// </summary>
    private sealed record PruningDecision(
        string Origin,
        string Receiver,
        IReadOnlyList<PruningEdgeKey> AssignedEdges,
        int SurvivorIndex,
        PruningEdgeKey SurvivorEdge,
        IReadOnlyList<PruningEdgeKey> CutEdges
    );

    /// <summary>
    /// Collects the mutable origins in stable order: abstract identity tables (their maintenance
    /// updates can change referenced identity keys) and directly mutable concrete resource roots
    /// (allowIdentityUpdates). Transitively mutable resources are not origins; updates reach them
    /// through candidate edges during the walk.
    /// </summary>
    internal static IReadOnlyList<string> CollectMutableOriginsInStableOrder(
        RelationalModelSetBuilderContext context
    )
    {
        var resourceContextsByResource = SetPassHelpers.BuildResourceContextLookup(context);
        List<string> origins = [];

        foreach (var abstractTable in context.AbstractIdentityTablesInNameOrder)
        {
            origins.Add(FormatTable(abstractTable.TableModel.Table));
        }

        foreach (var model in context.ConcreteResourcesInNameOrder)
        {
            // A resource without a schema context (never the case for pruning-relevant resources)
            // has no allowIdentityUpdates flag to honor and cannot seed an identity update.
            if (!resourceContextsByResource.TryGetValue(model.ResourceKey.Resource, out var resourceContext))
            {
                continue;
            }

            if (!context.GetOrCreateResourceBuilderContext(resourceContext).AllowIdentityUpdates)
            {
                continue;
            }

            origins.Add(FormatTable(model.RelationalModel.Root.Table));
        }

        origins.Sort(StringComparer.Ordinal);
        return origins;
    }

    /// <summary>
    /// Collects the abstract identity origins that cannot actually rename their identity key: an
    /// abstract identity table has no concrete subclass member whose identity is transitively
    /// mutable. Every abstract identity table is treated as a mutable origin by
    /// <see cref="CollectMutableOriginsInStableOrder"/> so its multi-cascade-path topology is still
    /// pruned, but a convergence discovered from an origin in this set guards a rename that never
    /// happens, so a cut there is always safe. Returned as formatted receiver-table names matching
    /// the origin strings the selector walks.
    /// </summary>
    internal static IReadOnlySet<string> CollectImmutableAbstractOrigins(
        RelationalModelSetBuilderContext context
    )
    {
        // Abstract resources with at least one transitively-mutable concrete subclass member. Only
        // these can seed an identity update that changes the abstract identity key.
        HashSet<QualifiedResourceName> mutableAbstractResources = [];

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            var superclassResourceName = TryGetOptionalString(
                resourceContext.ResourceSchema,
                "superclassResourceName"
            );

            if (string.IsNullOrWhiteSpace(superclassResourceName))
            {
                continue;
            }

            if (!context.GetOrCreateResourceBuilderContext(resourceContext).TransitivelyAllowIdentityUpdates)
            {
                continue;
            }

            var superclassProjectName = RequireString(
                resourceContext.ResourceSchema,
                "superclassProjectName"
            );

            mutableAbstractResources.Add(
                new QualifiedResourceName(superclassProjectName, superclassResourceName)
            );
        }

        HashSet<string> immutableAbstractOrigins = new(StringComparer.Ordinal);

        foreach (var abstractTable in context.AbstractIdentityTablesInNameOrder)
        {
            if (!mutableAbstractResources.Contains(abstractTable.AbstractResourceKey.Resource))
            {
                immutableAbstractOrigins.Add(FormatTable(abstractTable.TableModel.Table));
            }
        }

        return immutableAbstractOrigins;
    }

    /// <summary>
    /// Walks reachability from one mutable origin over retained candidate edges and returns the
    /// earliest receiver reached through two or more distinct incoming physical edges, together
    /// with those distinct reachable incoming edges as the pruning choices (in stable
    /// <see cref="PruningEdgeKey"/> order) and the first-arrival predecessor trace. Returns null
    /// when the origin produces no convergence. The walk visits each vertex and edge at most once;
    /// it never enumerates paths. Paths arriving through the same incoming edge converge upstream,
    /// so that receiver supplies no local choice.
    /// </summary>
    internal static ConvergenceDiscovery? FindEarliestConvergence(
        string origin,
        IReadOnlyList<PruningCandidate> candidates,
        IReadOnlySet<PruningEdgeKey> cutEdges
    )
    {
        // Retained outgoing edges by source vertex, in stable candidate order. Cut edges no longer
        // cascade, so they neither extend reachability nor count as convergence choices.
        Dictionary<string, List<PruningCandidate>> outgoingBySource = new(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (cutEdges.Contains(candidate.EdgeKey))
            {
                continue;
            }

            var source = FormatTable(candidate.TargetTable);

            if (!outgoingBySource.TryGetValue(source, out var outgoing))
            {
                outgoing = [];
                outgoingBySource[source] = outgoing;
            }

            outgoing.Add(candidate);
        }

        // Breadth-first reachability with first-arrival depth and predecessor edge per vertex.
        Dictionary<string, int> depthByVertex = new(StringComparer.Ordinal) { [origin] = 0 };
        Dictionary<string, PruningCandidate> predecessorEdgeByVertex = new(StringComparer.Ordinal);
        Queue<string> frontier = new();
        frontier.Enqueue(origin);

        while (frontier.Count > 0)
        {
            var vertex = frontier.Dequeue();

            if (!outgoingBySource.TryGetValue(vertex, out var outgoing))
            {
                continue;
            }

            foreach (var edge in outgoing)
            {
                var receiver = FormatTable(edge.ReceiverTable);

                if (depthByVertex.ContainsKey(receiver))
                {
                    continue;
                }

                depthByVertex[receiver] = depthByVertex[vertex] + 1;
                predecessorEdgeByVertex[receiver] = edge;
                frontier.Enqueue(receiver);
            }
        }

        // A receiver has a convergence when two or more distinct retained incoming physical edges
        // have reachable sources. The earliest convergence is the one at minimal walk depth, with
        // ordinal receiver-name order as the deterministic tie-break.
        Dictionary<string, List<PruningCandidate>> reachableIncomingByReceiver = new(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (cutEdges.Contains(candidate.EdgeKey))
            {
                continue;
            }

            if (!depthByVertex.ContainsKey(FormatTable(candidate.TargetTable)))
            {
                continue;
            }

            var receiver = FormatTable(candidate.ReceiverTable);

            if (!reachableIncomingByReceiver.TryGetValue(receiver, out var incoming))
            {
                incoming = [];
                reachableIncomingByReceiver[receiver] = incoming;
            }

            incoming.Add(candidate);
        }

        string? earliestReceiver = null;
        List<PruningCandidate>? earliestChoices = null;

        foreach (var (receiver, incoming) in reachableIncomingByReceiver)
        {
            if (incoming.Count < 2)
            {
                continue;
            }

            if (
                earliestReceiver is null
                || depthByVertex[receiver] < depthByVertex[earliestReceiver]
                || (
                    depthByVertex[receiver] == depthByVertex[earliestReceiver]
                    && string.CompareOrdinal(receiver, earliestReceiver) < 0
                )
            )
            {
                earliestReceiver = receiver;
                earliestChoices = incoming;
            }
        }

        return earliestReceiver is null
            ? null
            : new ConvergenceDiscovery(earliestReceiver, earliestChoices!, predecessorEdgeByVertex);
    }

    /// <summary>
    /// The result of per-origin convergence discovery: the earliest receiver with distinct
    /// reachable incoming physical edges, those edges as the only pruning choices, and the
    /// first-arrival predecessor trace the structural safe-cut test follows. Pass-local only;
    /// discarded after selection.
    /// </summary>
    internal sealed record ConvergenceDiscovery(
        string Receiver,
        IReadOnlyList<PruningCandidate> IncomingChoices,
        IReadOnlyDictionary<string, PruningCandidate> PredecessorEdgeByVertex
    );

    /// <summary>
    /// Rejects self-loops and cycles in the candidate cascade graph before pruning. A cycle means
    /// an identity update could cascade back into its own propagation; DMS fails derivation with a
    /// stable cycle witness rather than choosing an arbitrary cycle cut. Only the document-reference
    /// candidate edges participate; unrelated FK topology is never scanned.
    /// </summary>
    private static void RejectCandidateCycles(IReadOnlyList<PruningCandidate> candidates)
    {
        // Adjacency in cascade-propagation direction (referenced target -> referencing receiver).
        // Candidates arrive in stable PruningEdgeKey order, so adjacency lists are stable too.
        Dictionary<string, List<string>> receiversBySource = new(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            var source = FormatTable(candidate.TargetTable);

            if (!receiversBySource.TryGetValue(source, out var receivers))
            {
                receivers = [];
                receiversBySource[source] = receivers;
            }

            receivers.Add(FormatTable(candidate.ReceiverTable));
        }

        // Iterative depth-first search with path tracking. Every vertex on a cycle has an outgoing
        // candidate edge, so rooting the search at each source vertex (in stable order) finds every
        // cycle, and the first cycle found is deterministic.
        Dictionary<string, VisitState> visitStates = new(StringComparer.Ordinal);
        List<string> path = [];
        Stack<(string Vertex, int NextChildIndex)> pending = new();

        foreach (var root in receiversBySource.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (visitStates.GetValueOrDefault(root) != VisitState.Unvisited)
            {
                continue;
            }

            pending.Push((root, 0));
            visitStates[root] = VisitState.OnPath;
            path.Add(root);

            while (pending.Count > 0)
            {
                var (vertex, nextChildIndex) = pending.Pop();
                var children = receiversBySource.GetValueOrDefault(vertex);

                if (children is not null && nextChildIndex < children.Count)
                {
                    pending.Push((vertex, nextChildIndex + 1));

                    var child = children[nextChildIndex];
                    var childState = visitStates.GetValueOrDefault(child);

                    if (childState == VisitState.OnPath)
                    {
                        throw BuildCycleException(path, child);
                    }

                    if (childState == VisitState.Unvisited)
                    {
                        pending.Push((child, 0));
                        visitStates[child] = VisitState.OnPath;
                        path.Add(child);
                    }

                    continue;
                }

                visitStates[vertex] = VisitState.Completed;
                path.RemoveAt(path.Count - 1);
            }
        }
    }

    /// <summary>
    /// Builds the cycle diagnostic with a stable witness: the on-path tables from the first cycle
    /// vertex back to itself, in cascade direction.
    /// </summary>
    private static InvalidOperationException BuildCycleException(List<string> path, string cycleStart)
    {
        var startIndex = path.IndexOf(cycleStart);
        var witness = string.Join(" -> ", path.Skip(startIndex).Append(cycleStart));

        return new InvalidOperationException(
            $"SqlServerCascadeCycleNotSupported: SQL Server document-reference cascade candidates "
                + $"form a cycle ({witness}). DMS does not choose an arbitrary cycle cut."
        );
    }

    /// <summary>
    /// Depth-first search vertex state for candidate cycle detection.
    /// </summary>
    private enum VisitState
    {
        Unvisited,
        OnPath,
        Completed,
    }

    /// <summary>
    /// Formats a physical table name for stable ordering, adjacency, and diagnostics.
    /// </summary>
    private static string FormatTable(DbTableName table)
    {
        return $"{table.Schema.Value}.{table.Name}";
    }

    /// <summary>
    /// Collects the cascade candidate edges: document-reference foreign keys whose target identity
    /// is abstract or transitively mutable, identified structurally from the document-reference
    /// bindings rather than from the initial ON UPDATE action, so unrelated cascade FK classes can
    /// never become candidates. Bindings that resolved to the same physical constraint collapse
    /// into a single candidate because one physical constraint has one action. Candidates are
    /// returned in stable <see cref="PruningEdgeKey"/> order.
    /// </summary>
    internal static IReadOnlyList<PruningCandidate> CollectCandidatesInStableOrder(
        RelationalModelSetBuilderContext context
    )
    {
        var resourceContextsByResource = SetPassHelpers.BuildResourceContextLookup(context);
        var abstractResources = context
            .AbstractIdentityTablesInNameOrder.Select(table => table.AbstractResourceKey.Resource)
            .ToHashSet();

        Dictionary<PruningEdgeKey, PruningCandidate> candidatesByEdgeKey = new();

        foreach (
            var resourceModel in context.ConcreteResourcesInNameOrder.Select(entry => entry.RelationalModel)
        )
        {
            foreach (var binding in resourceModel.DocumentReferenceBindings)
            {
                if (
                    !IsCascadeCandidateTarget(
                        binding.TargetResource,
                        abstractResources,
                        resourceContextsByResource,
                        context
                    )
                )
                {
                    continue;
                }

                var bindingTable = ResolveReferenceBindingTable(
                    binding,
                    resourceModel,
                    resourceModel.Resource
                );
                var localDocumentIdColumn = ResolveLocalDocumentIdStorageColumn(
                    context,
                    bindingTable,
                    binding,
                    resourceModel.Resource
                );
                var foreignKey = FindDocumentReferenceForeignKey(
                    bindingTable,
                    localDocumentIdColumn,
                    binding,
                    resourceModel.Resource
                );

                var localDocumentIdIsRequired = !bindingTable
                    .Columns.Single(column => column.ColumnName.Equals(localDocumentIdColumn))
                    .IsNullable;

                var candidate = new PruningCandidate(
                    PruningEdgeKey.From(bindingTable.Table, foreignKey),
                    foreignKey.Name,
                    bindingTable.Table,
                    foreignKey.TargetTable,
                    foreignKey.Columns,
                    localDocumentIdColumn,
                    localDocumentIdIsRequired,
                    foreignKey.TargetColumns,
                    foreignKey.OnDelete
                );

                candidatesByEdgeKey.TryAdd(candidate.EdgeKey, candidate);
            }
        }

        return candidatesByEdgeKey.Values.OrderBy(candidate => candidate.EdgeKey).ToArray();
    }

    /// <summary>
    /// Determines whether a reference target participates in identity-update cascades: abstract
    /// identity targets always do; concrete targets do when they are transitively mutable.
    /// </summary>
    private static bool IsCascadeCandidateTarget(
        QualifiedResourceName targetResource,
        HashSet<QualifiedResourceName> abstractResources,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceSchemaContext> resourceContextsByResource,
        RelationalModelSetBuilderContext context
    )
    {
        if (abstractResources.Contains(targetResource))
        {
            return true;
        }

        if (!resourceContextsByResource.TryGetValue(targetResource, out var resourceContext))
        {
            throw new InvalidOperationException(
                $"Reference target resource '{FormatResource(targetResource)}' was not found "
                    + "for SQL Server foreign-key pruning."
            );
        }

        return context.GetOrCreateResourceBuilderContext(resourceContext).TransitivelyAllowIdentityUpdates;
    }

    /// <summary>
    /// Resolves the binding's <c>..._DocumentId</c> column to its canonical storage column,
    /// mirroring the resolution used when the reference foreign key was constructed.
    /// </summary>
    private static DbColumnName ResolveLocalDocumentIdStorageColumn(
        RelationalModelSetBuilderContext context,
        DbTableModel bindingTable,
        DocumentReferenceBinding binding,
        QualifiedResourceName resource
    )
    {
        var tableMetadata = UnifiedAliasStrictMetadataCache.GetOrBuild(context, bindingTable);

        return UnifiedAliasStorageResolver.ResolveStorageColumn(
            binding.FkColumn,
            tableMetadata,
            UnifiedAliasStorageResolver.PresenceGateRejectionPolicy.RejectSyntheticScalarPresence,
            $"reference '{binding.ReferenceObjectPath.Canonical}' on resource '{FormatResource(resource)}'",
            "reference fk column",
            "SQL Server foreign-key pruning"
        );
    }

    /// <summary>
    /// Finds the document-reference foreign key for a binding: the constraint on the binding table
    /// whose trailing local column is the binding's canonical <c>..._DocumentId</c> storage column.
    /// </summary>
    private static TableConstraint.ForeignKey FindDocumentReferenceForeignKey(
        DbTableModel bindingTable,
        DbColumnName localDocumentIdColumn,
        DocumentReferenceBinding binding,
        QualifiedResourceName resource
    )
    {
        var matches = bindingTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Where(constraint => constraint.Columns[^1].Equals(localDocumentIdColumn))
            .ToArray();

        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Reference '{binding.ReferenceObjectPath.Canonical}' on resource "
                    + $"'{FormatResource(resource)}' expected exactly one document-reference foreign key "
                    + $"ending in '{localDocumentIdColumn.Value}' on table '{bindingTable.Table.Name}' "
                    + $"but found {matches.Length}."
            );
        }

        return matches[0];
    }

    /// <summary>
    /// Stable identity and ordering key for a physical candidate foreign key: receiver table,
    /// ordered local columns, target table, ordered target columns, and the ON DELETE action.
    /// Deliberately excludes the selected ON UPDATE action and the rendered constraint name so
    /// candidate identity and ordering are independent of both. Pass-local only; never serialized.
    /// </summary>
    internal sealed record PruningEdgeKey(
        string ReceiverTable,
        string LocalColumns,
        string TargetTable,
        string TargetColumns,
        ReferentialAction OnDelete
    ) : IComparable<PruningEdgeKey>
    {
        /// <summary>
        /// Joins column names with a separator that cannot appear in SQL identifiers so joined
        /// column lists compare unambiguously.
        /// </summary>
        private const char ColumnSeparator = '\u001F';

        /// <summary>
        /// Builds the edge key for a foreign key on a receiver table.
        /// </summary>
        public static PruningEdgeKey From(DbTableName receiverTable, TableConstraint.ForeignKey foreignKey)
        {
            ArgumentNullException.ThrowIfNull(foreignKey);

            return new PruningEdgeKey(
                FormatTable(receiverTable),
                string.Join(ColumnSeparator, foreignKey.Columns.Select(column => column.Value)),
                FormatTable(foreignKey.TargetTable),
                string.Join(ColumnSeparator, foreignKey.TargetColumns.Select(column => column.Value)),
                foreignKey.OnDelete
            );
        }

        /// <summary>
        /// Compares component-wise with ordinal string comparison for deterministic ordering.
        /// </summary>
        public int CompareTo(PruningEdgeKey? other)
        {
            if (other is null)
            {
                return 1;
            }

            var comparison = string.CompareOrdinal(ReceiverTable, other.ReceiverTable);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = string.CompareOrdinal(LocalColumns, other.LocalColumns);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = string.CompareOrdinal(TargetTable, other.TargetTable);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = string.CompareOrdinal(TargetColumns, other.TargetColumns);
            if (comparison != 0)
            {
                return comparison;
            }

            return OnDelete.CompareTo(other.OnDelete);
        }
    }

    /// <summary>
    /// A pass-local cascade candidate edge: one physical document-reference foreign key from a
    /// referenced target table to a referencing receiver table, with the column and requiredness
    /// facts the structural safe-cut test needs. The constraint name is carried for diagnostics
    /// only and never participates in identity or ordering. Never serialized into the relational
    /// model.
    /// </summary>
    internal sealed record PruningCandidate(
        PruningEdgeKey EdgeKey,
        string ConstraintName,
        DbTableName ReceiverTable,
        DbTableName TargetTable,
        IReadOnlyList<DbColumnName> LocalColumns,
        DbColumnName LocalDocumentIdColumn,
        bool LocalDocumentIdIsRequired,
        IReadOnlyList<DbColumnName> TargetColumns,
        ReferentialAction OnDelete
    );
}
