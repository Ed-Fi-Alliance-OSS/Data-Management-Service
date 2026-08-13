// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Outcome of planning the single-record custom view-based checks for one operation.
/// </summary>
public abstract record SingleRecordCustomViewAuthorizationPlanOutcome
{
    private SingleRecordCustomViewAuthorizationPlanOutcome() { }

    /// <summary>The checks the SQL layer must execute, in emission order.</summary>
    public sealed record Plan(IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> Checks)
        : SingleRecordCustomViewAuthorizationPlanOutcome;

    /// <summary>
    /// At least one configured custom view could not be planned for this operation. <paramref name="PlannedChecks"/>
    /// carries the checks that <em>did</em> plan, so a caller can still validate the custom views configured
    /// ahead of the earliest failure before reporting it — custom views are AND filters executing in
    /// CMS-configured order, so an earlier missing or non-conforming <c>auth.{StrategyName}</c> must surface
    /// its own error rather than being hidden by a later strategy's planning failure. Mirrors the shape
    /// <see cref="CustomViewAuthorizationPlanOutcome.SecurityConfiguration"/> uses for GET-many.
    /// </summary>
    public sealed record SecurityConfiguration(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> Failures,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> PlannedChecks
    ) : SingleRecordCustomViewAuthorizationPlanOutcome;
}

/// <summary>
/// Plans the custom view-based authorization checks for a single-record operation: resolves each configured
/// strategy's basis path, derives the wording metadata a denial reports, and expands the strategy into the
/// stored and proposed checks the operation requires.
/// </summary>
/// <remarks>
/// GET-many is planned by <see cref="CustomViewAuthorizationPlanner"/> instead. It filters a page rather than
/// deciding one record, so it needs neither a value source nor a check target, and passing
/// <see cref="NamespaceAuthorizationOperation.ReadMany"/> here is rejected rather than silently planned.
/// </remarks>
public static class SingleRecordCustomViewAuthorizationPlanner
{
    private static readonly DbSchemaName AuthSchema = new("auth");
    private static readonly DbColumnName DocumentIdColumn = new("DocumentId");
    private const string ProposedValueParameterSeedPrefix = "customViewAuthorization";

    public static SingleRecordCustomViewAuthorizationPlanOutcome Plan(
        MappingSet mappingSet,
        ConcreteResourceModel resource,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> customViewStrategies,
        NamespaceAuthorizationOperation operation
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(customViewStrategies);

        if (operation is NamespaceAuthorizationOperation.ReadMany)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "GET-many custom view authorization is planned by CustomViewAuthorizationPlanner, not the single-record planner."
            );
        }

        var subjectResource = resource.RelationalModel.Resource;
        var rootTable = resource.RelationalModel.Root.Table;
        var rootDocumentIdColumn = PersonJoinPathResolver.ResolveToCanonicalColumn(
            resource.RelationalModel.Root,
            RelationalNameConventions.DocumentIdColumnName
        );
        var plansProposedChecks = operation is NamespaceAuthorizationOperation.Update;

        List<PlannedStrategy> plannedStrategies = [];
        List<RelationshipAuthorizationFailureMetadata> failures = [];

        foreach (var strategy in customViewStrategies)
        {
            var resolvedPath = SecurableElementColumnPathResolver.ResolveBasisResourcePathWithMetadata(
                subjectResource,
                strategy.BasisResource,
                mappingSet.Model
            );

            if (resolvedPath.Steps.Count == 0)
            {
                failures.Add(BuildNoJoinPathFailure(subjectResource, strategy));
                continue;
            }

            // A path whose first hop leaves the root through the root's own DocumentId reaches the basis
            // through a child collection table. Stored checks can still walk it — the root DocumentId is
            // known — but a proposed check cannot: it would have to bind a value from child rows that the
            // request has not written yet. auth.md is explicit that authorization checks apply to the
            // resource or descriptor root table and not to collection items, so an operation that needs a
            // proposed check fails closed rather than silently skipping this strategy.
            var startsFromRootDocumentId = IsRootDocumentIdSourced(
                resolvedPath.Steps[0],
                rootTable,
                rootDocumentIdColumn
            );
            var isSelfBasis = resolvedPath.Steps.Count == 1 && startsFromRootDocumentId;
            var reachesBasisThroughChildTable = resolvedPath.Steps.Count > 1 && startsFromRootDocumentId;

            if (plansProposedChecks && reachesBasisThroughChildTable)
            {
                failures.Add(BuildChildTablePathFailure(subjectResource, strategy, resolvedPath));
                continue;
            }

            plannedStrategies.Add(
                new PlannedStrategy(
                    strategy,
                    resolvedPath,
                    isSelfBasis,
                    BuildReadableSecurableElements(resolvedPath, strategy.BasisResource, mappingSet.Model),
                    CustomViewAuthorizationHintFormatter.Format(strategy.ConfiguredStrategy.StrategyName)
                )
            );
        }

        var checks = BuildChecks(plannedStrategies, rootTable, rootDocumentIdColumn, plansProposedChecks);

        return failures.Count > 0
            ? new SingleRecordCustomViewAuthorizationPlanOutcome.SecurityConfiguration(failures, checks)
            : new SingleRecordCustomViewAuthorizationPlanOutcome.Plan(checks);
    }

    /// <summary>
    /// Expands the planned strategies into checks. All stored checks are emitted before any proposed check,
    /// so the indexes read <c>0..n-1</c> stored then <c>n..2n-1</c> proposed. That ordering is the design's
    /// requirement that stored values are authorized before proposed values, and it matches how
    /// <see cref="NamespaceAuthorizationPlanner"/> numbers its own stored/proposed pair.
    /// </summary>
    private static IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> BuildChecks(
        IReadOnlyList<PlannedStrategy> plannedStrategies,
        DbTableName rootTable,
        DbColumnName rootDocumentIdColumn,
        bool plansProposedChecks
    )
    {
        List<SingleRecordCustomViewAuthorizationCheckSpec> checks = [];

        foreach (var planned in plannedStrategies)
        {
            checks.Add(
                CreateCheck(
                    planned,
                    checks.Count,
                    CustomViewAuthorizationCheckValueSource.Stored,
                    new CustomViewAuthorizationCheckTarget.Stored(rootTable, rootDocumentIdColumn)
                )
            );
        }

        if (!plansProposedChecks)
        {
            return checks;
        }

        foreach (var planned in plannedStrategies)
        {
            checks.Add(
                CreateCheck(
                    planned,
                    checks.Count,
                    CustomViewAuthorizationCheckValueSource.Proposed,
                    CreateProposedTarget(planned, rootTable, checks.Count)
                )
            );
        }

        return checks;
    }

    private static SingleRecordCustomViewAuthorizationCheckSpec CreateCheck(
        PlannedStrategy planned,
        int index,
        CustomViewAuthorizationCheckValueSource valueSource,
        CustomViewAuthorizationCheckTarget checkTarget
    ) =>
        new(
            planned.Strategy.ConfiguredStrategy,
            index,
            valueSource,
            new DbTableName(AuthSchema, planned.Strategy.ConfiguredStrategy.StrategyName),
            DocumentIdColumn,
            planned.ResolvedPath.Steps,
            checkTarget,
            planned.Strategy.BasisResource,
            planned.ReadableSecurableElements,
            planned.FailureHint
        );

    private static CustomViewAuthorizationCheckTarget CreateProposedTarget(
        PlannedStrategy planned,
        DbTableName rootTable,
        int index
    )
    {
        if (planned.IsSelfBasis)
        {
            return new CustomViewAuthorizationCheckTarget.ProposedSelfBasisUnavailable(rootTable);
        }

        var firstStep = planned.ResolvedPath.Steps[0];

        return new CustomViewAuthorizationCheckTarget.Proposed(
            rootTable,
            new CustomViewAuthorizationProposedValueBinding(
                firstStep.SourceTable,
                firstStep.SourceColumnName,
                $"{planned.Strategy.ConfiguredStrategy.StrategyName}:{firstStep.SourceColumnName.Value}",
                $"{ProposedValueParameterSeedPrefix}{index}"
            )
        );
    }

    /// <summary>
    /// Whether <paramref name="step"/> leaves the subject root through the root's own <c>DocumentId</c>
    /// rather than through a basis-bearing foreign key. The resolver builds exactly that step both for a
    /// self-basis path (as the only step) and as the prefix hop of a path that reaches the basis through a
    /// child collection table.
    /// </summary>
    private static bool IsRootDocumentIdSourced(
        ColumnPathStep step,
        DbTableName rootTable,
        DbColumnName rootDocumentIdColumn
    ) => step.SourceTable.Equals(rootTable) && step.SourceColumnName.Equals(rootDocumentIdColumn);

    /// <summary>
    /// Readable names for the securable element the check decides on.
    /// </summary>
    /// <remarks>
    /// Normally these come from the terminal reference's identity JSON paths. A self-basis path has none — it
    /// terminates on the subject's own <c>DocumentId</c> rather than on a reference — so the basis resource's
    /// own authoritative identity is resolved instead. The names must be produced here: the cross-boundary
    /// failure carries only this list, so a later layer has nothing left to reconstruct them from.
    /// </remarks>
    private static IReadOnlyList<string> BuildReadableSecurableElements(
        ResolvedBasisResourcePath resolvedPath,
        QualifiedResourceName basisResource,
        DerivedRelationalModelSet modelSet
    )
    {
        var readableNames = ToReadableNames(resolvedPath.TerminalReferenceJsonPaths);

        if (readableNames.Count > 0)
        {
            return readableNames;
        }

        var basisIdentityNames = ToReadableNames(ResolveBasisIdentityJsonPaths(basisResource, modelSet));

        // Nothing in the model references the basis, so no authoritative identity path exists to name — a
        // descriptor basis reached by its own view is the realistic case, since descriptor edges carry only
        // the referencing value path. The resource name is the closest truthful label left.
        return basisIdentityNames.Count > 0 ? basisIdentityNames : [basisResource.ResourceName];
    }

    /// <summary>
    /// The basis resource's authoritative identity JSON paths, read from any document reference that targets
    /// it. <see cref="ReferenceIdentityBinding.IdentityJsonPath"/> is defined as the identity path <em>on the
    /// target resource</em>, so a reference to the basis names the basis's own identity — which is what a
    /// self-basis check decides on. All parts of a composite identity are kept, in binding order.
    /// </summary>
    /// <remarks>
    /// Resources are scanned in name order and a non-role-named reference wins, so the result does not depend
    /// on which resource happens to reference the basis first. An abstract basis resolves through references
    /// to the abstract resource itself, giving <c>EducationOrganizationId</c> rather than a concrete arm's
    /// own key.
    /// </remarks>
    private static IReadOnlyList<string> ResolveBasisIdentityJsonPaths(
        QualifiedResourceName basisResource,
        DerivedRelationalModelSet modelSet
    )
    {
        IReadOnlyList<string>? roleNamedFallback = null;

        foreach (var resource in modelSet.ConcreteResourcesInNameOrder)
        {
            foreach (var binding in resource.RelationalModel.DocumentReferenceBindings)
            {
                if (binding.TargetResource != basisResource || binding.IdentityBindings.Count == 0)
                {
                    continue;
                }

                IReadOnlyList<string> identityJsonPaths =
                [
                    .. binding.IdentityBindings.Select(static identityBinding =>
                        identityBinding.IdentityJsonPath.Canonical
                    ),
                ];

                if (!binding.IsRoleNamed)
                {
                    return identityJsonPaths;
                }

                roleNamedFallback ??= identityJsonPaths;
            }
        }

        return roleNamedFallback ?? [];
    }

    private static IReadOnlyList<string> ToReadableNames(IReadOnlyList<string> jsonPaths) =>
        [
            .. jsonPaths
                .Select(ToReadableName)
                .Where(static readableName => !string.IsNullOrWhiteSpace(readableName))
                .Distinct(StringComparer.Ordinal),
        ];

    // Matches RelationalPeopleAuthorizationSubjectSelector.ToReadableName. Both feed the same
    // ProblemDetails vocabulary, so the two must agree on how a JSON path becomes a user-facing name.
    private static string ToReadableName(string jsonPath)
    {
        var leaf = jsonPath.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? jsonPath;
        leaf = leaf.Replace("[*]", string.Empty, StringComparison.Ordinal);

        return string.IsNullOrEmpty(leaf) ? jsonPath : leaf[..1].ToUpperInvariant() + leaf[1..];
    }

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
            Hint: $"No DocumentId join path could be resolved from subject resource '{subjectResource.ProjectName}.{subjectResource.ResourceName}' to custom view basis resource '{strategy.BasisResource.ProjectName}.{strategy.BasisResource.ResourceName}'."
        );

    private static RelationshipAuthorizationFailureMetadata BuildChildTablePathFailure(
        QualifiedResourceName subjectResource,
        SupportedCustomViewAuthorizationStrategy strategy,
        ResolvedBasisResourcePath resolvedPath
    ) =>
        new(
            RelationshipAuthorizationFailureKind.MissingProposedCustomViewRootBinding,
            subjectResource,
            strategy.ConfiguredStrategy,
            strategy.AuthorizationLocalOrder,
            // Both taken from the terminal step so the pair names a column on the table it lives on; mixing
            // steps would point diagnostics at a table.column combination that does not exist for 3+ hop paths.
            Location: new RelationshipAuthorizationFailureLocation(
                Table: resolvedPath.Steps[^1].SourceTable,
                Column: resolvedPath.Steps[^1].SourceColumnName,
                AuthorizationObjectName: $"auth.{strategy.ConfiguredStrategy.StrategyName}"
            ),
            Hint: $"Custom view basis resource '{strategy.BasisResource.ProjectName}.{strategy.BasisResource.ResourceName}' is reached from subject resource '{subjectResource.ProjectName}.{subjectResource.ResourceName}' through a child collection table, so no root-table value can authorize proposed data for a write."
        );

    private sealed record PlannedStrategy(
        SupportedCustomViewAuthorizationStrategy Strategy,
        ResolvedBasisResourcePath ResolvedPath,
        bool IsSelfBasis,
        IReadOnlyList<string> ReadableSecurableElements,
        string FailureHint
    );
}
