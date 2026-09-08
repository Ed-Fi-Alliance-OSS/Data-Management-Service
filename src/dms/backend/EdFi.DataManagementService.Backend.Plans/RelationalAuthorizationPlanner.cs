// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Security;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Outcome of the relational authorization orchestrator. Conveys the namespace check plan and the
/// non-namespace configured strategies the caller must still feed through the relationship planner,
/// or a terminal failure outcome that short-circuits the request.
/// </summary>
public abstract record RelationalAuthorizationPlanOutcome
{
    private RelationalAuthorizationPlanOutcome() { }

    /// <summary>
    /// Proceed: execute the namespace checks (if any) and route any non-namespace configured
    /// strategies through the relationship planner. Both lists may be empty. Custom view
    /// supported strategies (for ReadMany) are returned separately in the third member.
    /// </summary>
    public sealed record Plan(
        IReadOnlyList<NamespaceAuthorizationCheckSpec> NamespaceChecks,
        IReadOnlyList<ConfiguredAuthorizationStrategy> NonNamespaceConfiguredStrategies,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> CustomViewStrategies
    ) : RelationalAuthorizationPlanOutcome
    {
        /// <summary>
        /// The planned ownership check, or <see langword="null"/> when <c>OwnershipBased</c> is not
        /// configured or is not enforced for this operation and storage kind.
        /// </summary>
        /// <remarks>
        /// An <c>init</c> property rather than a positional member so no existing construction site changes.
        /// Singular, not a list: ownership plans exactly one check per operation, and repeated configuration
        /// collapses to the earliest occurrence.
        /// <para>
        /// Null here does not mean "ownership is satisfied" — while the enablement gate withholds an
        /// operation, a configured <c>OwnershipBased</c> stays in the non-namespace bucket and the request
        /// never reaches a <c>Plan</c> at all, because the classifier reports it known-but-not-enabled. A
        /// caller therefore cannot silently drop the check by ignoring this property.
        /// </para>
        /// </remarks>
        public OwnershipAuthorizationCheckSpec? OwnershipCheck { get; init; }

        /// <summary>
        /// The planned <c>OwnershipBased</c> page filter, or <see langword="null"/> when <c>OwnershipBased</c>
        /// is not configured or the operation and storage kind do not enforce the page filter.
        /// </summary>
        /// <remarks>
        /// The GET-many counterpart of <see cref="OwnershipCheck"/>, never set together with it: the page filter
        /// is planned only where <see cref="RelationalAuthorizationPlanner.EnforcesOwnershipPageFilter"/> admits
        /// the request and the single-record check only where <c>EnforcesOwnershipChecks</c> does, and the two
        /// gates admit disjoint operations. Singular for the same reason the check is, and null here never means
        /// "ownership is satisfied": where the page filter is not enforced, a configured <c>OwnershipBased</c>
        /// stays in the classifier bucket and the request never reaches a <c>Plan</c>. The repository
        /// additionally refuses to compose a page query when this is set and no token parameterization was
        /// built, so a caller cannot drop the filter by ignoring this property.
        /// </remarks>
        public PageOwnershipFilterSpec? OwnershipPageFilter { get; init; }
    }

    /// <summary>
    /// <c>OwnershipBased</c> is enforced and the client's ownership-token list reaches the defensive limit.
    /// Maps to a 500 Security Configuration Error at planner/preflight time — no DB roundtrip is issued.
    /// </summary>
    /// <param name="OwnershipTokenCount">
    /// The configured token count, as supplied and before deduplication. Reported so an operator can see
    /// what to reduce; no token value is ever disclosed.
    /// </param>
    /// <param name="StrategyName">Always <c>OwnershipBased</c>.</param>
    /// <remarks>
    /// Ranked after both namespace terminals and ahead of every relationship terminal, because ownership is
    /// an AND strategy that executes last among the AND strategies and before the relationship OR group.
    /// <para>
    /// <see cref="CustomViewStrategies"/> carries every resolved custom view rather than only those
    /// configured before some index: ownership executes last among the AND strategies whatever position CMS
    /// gave it, so every view runs ahead of this terminal and must be validated before it is reported.
    /// </para>
    /// </remarks>
    public sealed record OwnershipTokenCapExceeded(int OwnershipTokenCount, string StrategyName)
        : RelationalAuthorizationPlanOutcome
    {
        public IReadOnlyList<SupportedCustomViewAuthorizationStrategy> CustomViewStrategies { get; init; } =
        [];
    }

    /// <summary>
    /// <c>NamespaceBased</c> is configured but no securable element resolves to the resource's
    /// concrete root-table column. Maps to a 500 Security Configuration Error.
    /// </summary>
    public sealed record NoUsableRootColumn(QualifiedResourceName Resource)
        : RelationalAuthorizationPlanOutcome
    {
        public int RawConfiguredIndex { get; init; } = -1;

        public IReadOnlyList<SupportedCustomViewAuthorizationStrategy> CustomViewStrategies { get; init; } =
        [];
    }

    /// <summary>
    /// <c>NamespaceBased</c> is configured and the client has no namespace prefixes assigned.
    /// Maps to the no-prefixes-configured 403 ProblemDetails at planner/preflight time — no DB
    /// roundtrip is issued.
    /// </summary>
    public sealed record NoPrefixesConfigured(string StrategyName, int RawConfiguredIndex)
        : RelationalAuthorizationPlanOutcome
    {
        public IReadOnlyList<SupportedCustomViewAuthorizationStrategy> CustomViewStrategies { get; init; } =
        [];
    }

    /// <summary>
    /// At least one configured strategy is known but not yet supported (e.g. <c>OwnershipBased</c> or
    /// a custom view-based strategy). The request fails closed. The non-namespace strategies are carried
    /// so the caller can re-run the relationship planner and surface the exact fail-closed result.
    /// </summary>
    public sealed record StillUnsupported(
        IReadOnlyList<ConfiguredAuthorizationStrategy> NonNamespaceConfiguredStrategies,
        RelationshipAuthorizationClassification RelationshipClassification
    ) : RelationalAuthorizationPlanOutcome;

    /// <summary>
    /// At least one configured strategy is unrecognized or otherwise invalid. The relationship
    /// classifier reports a security configuration error and the request fails with 500. The
    /// non-namespace strategies and classification are carried so the caller can surface the exact
    /// security-configuration response and diagnostics without reclassifying the strategy set.
    /// </summary>
    public sealed record SecurityConfigurationError(
        IReadOnlyList<ConfiguredAuthorizationStrategy> NonNamespaceConfiguredStrategies,
        RelationshipAuthorizationClassification RelationshipClassification
    ) : RelationalAuthorizationPlanOutcome;
}

/// <summary>
/// How <see cref="RelationalAuthorizationPlanner.Plan"/> treats an enforced <c>OwnershipBased</c> token list
/// that reaches the defensive limit.
/// </summary>
public enum OwnershipTokenCapHandling
{
    /// <summary>
    /// Report <see cref="RelationalAuthorizationPlanOutcome.OwnershipTokenCapExceeded"/> as a planner terminal.
    /// The default, for every operation whose target is known to exist before planning — GET-by-id, PUT and
    /// DELETE — so the failure is reported before any session opens and is ranked against the other terminals.
    /// </summary>
    FailAtPlanning,

    /// <summary>
    /// Plan as though the list were under the limit and leave the failure to the caller, who owes it only once
    /// the target proves to exist. For POST: the check authorizes the stored token, so it is vacuous for a
    /// create and the over-limit list is never parameterized for one, while a POST that resolves to an
    /// upsert-as-update fails closed in the ownership slot — after the custom-view and namespace checks,
    /// before the relationship check and before any DML. With the cap deferred it is not a planning fact, so
    /// a relationship configuration failure keeps its own precedence rather than yielding to the cap.
    /// </summary>
    DeferToTargetResolution,
}

/// <summary>
/// Higher-level relational authorization planner. Splits the configured strategy list into a
/// namespace bucket and a non-namespace bucket, delegates namespace planning to
/// <see cref="NamespaceAuthorizationPlanner"/>, and uses
/// <see cref="RelationshipAuthorizationStrategyClassifier"/> to detect still-unsupported and
/// security-configuration outcomes on the non-namespace bucket. Composes the result so callers can
/// dispatch on a single outcome value.
/// </summary>
/// <remarks>
/// Outcome precedence:
/// <list type="number">
/// <item><see cref="RelationalAuthorizationPlanOutcome.SecurityConfigurationError"/> — the relationship classifier
/// reports an unrecognized or invalid strategy in the non-namespace bucket (500). For <c>ReadMany</c> this yields
/// to an earlier namespace terminal — either <see cref="RelationalAuthorizationPlanOutcome.NoPrefixesConfigured"/> or
/// <see cref="RelationalAuthorizationPlanOutcome.NoUsableRootColumn"/>: Namespace-based and custom view-based are AND
/// strategies that execute in CMS-configured order, so the failing strategy only wins when it is configured at or
/// before the <c>NamespaceBased</c> index.</item>
/// <item><see cref="RelationalAuthorizationPlanOutcome.NoUsableRootColumn"/> — <c>NamespaceBased</c> is configured
/// but no root-table column resolves (500).</item>
/// <item><see cref="RelationalAuthorizationPlanOutcome.StillUnsupported"/> — descriptor storage configured with
/// <c>OwnershipBased</c> on a single-record operation (501 NotImplemented, fail closed). The one place an
/// unsupported strategy outranks a namespace terminal: descriptor ownership enforcement is out of this story's
/// scope, so reporting the namespace 403 first would answer as though the caller's prefixes refused a check
/// that was never enforced. Scoped to the operations the ownership gate would otherwise enforce, so descriptor
/// <c>ReadMany</c> keeps its namespace terminal.</item>
/// <item><see cref="RelationalAuthorizationPlanOutcome.NoPrefixesConfigured"/> — <c>NamespaceBased</c> is configured
/// and the client has no namespace prefixes (403, preflight). Namespace-based is AND-combined and executes
/// ahead of relationship OR-combined strategies, so its 403 wins over a sibling
/// known-but-not-enabled relationship strategy.</item>
/// <item><see cref="RelationalAuthorizationPlanOutcome.OwnershipTokenCapExceeded"/> — <c>OwnershipBased</c> is
/// enforced and the client's ownership-token list reaches the defensive limit (500, preflight). Ranked after both
/// namespace terminals, because Namespace-based and custom view-based execute ahead of Ownership-based among the
/// AND strategies; and ahead of every relationship terminal, because the relationship OR group executes after all
/// AND strategies. That last part needs no index comparison, unlike the namespace-versus-relationship case: an
/// ownership terminal outranks a relationship one whatever position CMS gave either. It does <em>not</em> outrank a
/// custom-view configuration failure, which the classifier reports in the same bucket as relationship failures —
/// see <c>OwnershipCapOutranksClassifierFailure</c>. A caller that resolves its target in-session — POST — asks
/// for <see cref="OwnershipTokenCapHandling.DeferToTargetResolution"/>, and this terminal is then never
/// returned: the plan is handed back and the caller fails closed only once the target proves to exist.</item>
/// <item><see cref="RelationalAuthorizationPlanOutcome.StillUnsupported"/> — the relationship classifier reports a
/// known-but-not-enabled strategy in the non-namespace bucket (501 NotImplemented, fail closed).</item>
/// <item><see cref="RelationalAuthorizationPlanOutcome.Plan"/> — everything else.</item>
/// </list>
/// <para>
/// <c>OwnershipBased</c> is split into a bucket of its own, but only where one of two gates says the caller
/// executes what this planner hands back: <c>EnforcesOwnershipChecks</c> admits the single-record operations,
/// whose callers execute the <see cref="RelationalAuthorizationPlanOutcome.Plan.OwnershipCheck"/>, and
/// <see cref="EnforcesOwnershipPageFilter"/> admits <c>ReadMany</c>, whose GET-many and partition callers apply
/// the <see cref="RelationalAuthorizationPlanOutcome.Plan.OwnershipPageFilter"/> to the candidate relation.
/// Everywhere else it stays in the non-namespace bucket, so the classifier keeps reporting it
/// known-but-not-enabled and the request keeps its fail-closed 501 — which is what stops an unenforced
/// ownership strategy from being silently dropped. Each gate was flipped on in the same commit that wired its
/// executor. Descriptor storage is withheld by both gates because descriptor ownership enforcement is out of
/// scope. A custom view configured ahead of any of these terminals is still validated first, so an earlier
/// custom-view configuration failure keeps its own response.
/// </para>
/// </remarks>
public static class RelationalAuthorizationPlanner
{
    public static RelationalAuthorizationPlanOutcome Plan(
        MappingSet mappingSet,
        ConcreteResourceModel resource,
        NamespaceAuthorizationOperation operation,
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies,
        RelationalAuthorizationContext context,
        OwnershipTokenCapHandling ownershipTokenCapHandling = OwnershipTokenCapHandling.FailAtPlanning
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(configuredAuthorizationStrategies);
        ArgumentNullException.ThrowIfNull(context);

        var (namespaceStrategies, nonNamespaceStrategies) = SplitByNamespaceBased(
            configuredAuthorizationStrategies
        );

        // Split OwnershipBased into its own bucket only where it is enforced — as the single-record check
        // or as the GET-many page filter. Where it is neither, it stays in the non-namespace bucket so the
        // classifier keeps reporting it known-but-not-enabled and the request keeps its existing 501. That
        // is what makes an unenforced ownership strategy fail closed rather than be silently dropped, and
        // it is why this split is conditional rather than unconditional like the namespace one.
        var enforcesOwnershipChecks = EnforcesOwnershipChecks(operation, resource.StorageKind);
        var enforcesOwnershipPageFilter = EnforcesOwnershipPageFilter(operation, resource.StorageKind);

        IReadOnlyList<ConfiguredAuthorizationStrategy> ownershipStrategies = [];

        if (enforcesOwnershipChecks || enforcesOwnershipPageFilter)
        {
            (ownershipStrategies, nonNamespaceStrategies) = SplitByOwnershipBased(nonNamespaceStrategies);
        }

        // SecurityConfigurationError (500) and StillUnsupported (501) are detected by the existing
        // relationship classifier; invoke it only when the non-namespace bucket is non-empty.
        RelationshipAuthorizationClassification? relationshipClassification =
            nonNamespaceStrategies.Count == 0
                ? null
                : RelationshipAuthorizationStrategyClassifier.Classify(
                    mappingSet,
                    resource.RelationalModel.Resource,
                    nonNamespaceStrategies
                );

        var hasSecurityConfigurationError =
            relationshipClassification is
            { Outcome: RelationshipAuthorizationClassificationOutcome.SecurityConfigurationError };

        var enforcesCustomViewChecks = EnforcesCustomViewChecks(operation);

        // Deferred, the cap is not a planning fact: a POST that creates never parameterizes the list, so the
        // caller owes the failure only once the target proves to exist, and no terminal below may consult it.
        var ownershipCapExceeded =
            ownershipTokenCapHandling is OwnershipTokenCapHandling.FailAtPlanning
            && ownershipStrategies.Count > 0
            && context.OwnershipTokenIds.Count >= OwnershipTokenLimitExceededException.OwnershipTokenLimit;

        // Whether the ownership terminal may displace the classifier's security-configuration failure.
        // Evaluated once here so both relationship security-configuration arms below consult one answer.
        var ownershipCapOutranksClassifierFailure = OwnershipCapOutranksClassifierFailure(
            ownershipCapExceeded,
            relationshipClassification?.SecurityConfigurationFailures ?? []
        );

        if (
            hasSecurityConfigurationError
            && !enforcesCustomViewChecks
            && !ownershipCapOutranksClassifierFailure
        )
        {
            return new RelationalAuthorizationPlanOutcome.SecurityConfigurationError(
                nonNamespaceStrategies,
                relationshipClassification!
            );
        }

        NamespaceAuthorizationPlanOutcome? namespaceOutcome =
            namespaceStrategies.Count == 0
                ? null
                : NamespaceAuthorizationPlanner.Plan(resource, operation, context);

        if (hasSecurityConfigurationError)
        {
            // Namespace-based and custom view-based are AND strategies that execute in CMS-configured
            // order, so a classifier failure must not leap ahead of a Namespace terminal configured
            // before it. Only reached by callers that enforce custom views; the rest returned above. Both namespace terminals participate — no configured
            // prefixes and no usable root column. Every other combination — no Namespace terminal,
            // or a Namespace terminal configured after the failing strategy — keeps the classifier's
            // security-configuration error.
            var namespaceTerminalPrecedesFailure =
                namespaceOutcome
                    is NamespaceAuthorizationPlanOutcome.NoPrefixesConfigured
                        or NamespaceAuthorizationPlanOutcome.NoUsableRootColumn
                && namespaceStrategies[0].RawConfiguredIndex
                    < EarliestSecurityConfigurationFailureIndex(
                        relationshipClassification!.SecurityConfigurationFailures
                    );

            // The ownership terminal may also outrank this failure — but only when the failure is a
            // relationship one. A custom-view configuration failure keeps its own response, because every
            // custom view executes ahead of ownership among the AND strategies.
            if (!namespaceTerminalPrecedesFailure && !ownershipCapOutranksClassifierFailure)
            {
                return new RelationalAuthorizationPlanOutcome.SecurityConfigurationError(
                    nonNamespaceStrategies,
                    relationshipClassification!
                );
            }
        }

        var classifiedCustomViewStrategies = relationshipClassification?.SupportedCustomViewStrategies ?? [];
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> supportedCustomViewStrategies =
            enforcesCustomViewChecks ? classifiedCustomViewStrategies : [];

        if (namespaceOutcome is NamespaceAuthorizationPlanOutcome.NoUsableRootColumn noUsableRoot)
        {
            // namespaceOutcome is non-null only when the namespace bucket is non-empty.
            return new RelationalAuthorizationPlanOutcome.NoUsableRootColumn(noUsableRoot.Resource)
            {
                RawConfiguredIndex = namespaceStrategies[0].RawConfiguredIndex,
                CustomViewStrategies = supportedCustomViewStrategies,
            };
        }

        // Descriptor ownership enforcement is out of this story's scope, so a descriptor configured with
        // OwnershipBased must fail closed as known-but-not-enabled (501) on every single-record operation —
        // GET-by-id, the write verbs, and DELETE. Ranked ahead of the namespace no-prefixes terminal on
        // purpose, and it is the one place where an unimplemented strategy outranks a namespace terminal:
        // that 403 is a runtime authorization answer for a strategy the caller does execute, so letting it
        // win reports "your namespace prefixes refused this" for a descriptor whose ownership strategy was
        // never enforced at all. The unimplemented strategy has to be the terminal, or the response says the
        // request was authorized-and-refused rather than not-implemented.
        //
        // Ranked after NoUsableRootColumn, which stays ahead: that terminal means the descriptor's own
        // namespace column is missing from the model, which is a genuine security-configuration fault (500)
        // rather than a masked scope boundary.
        //
        // Scoped to the operations the ownership gate would otherwise enforce, so descriptor GET-many keeps
        // its existing namespace terminal — GET-many ownership filtering is DMS-1410's.
        if (DescriptorOwnershipUnsupported(resource.StorageKind, operation, nonNamespaceStrategies))
        {
            // relationshipClassification is non-null because the predicate requires OwnershipBased in the
            // non-namespace bucket, so that bucket is non-empty. The classification is carried unchanged, so
            // the resolved custom views ride along and a view configured ahead of this terminal keeps its own
            // configuration failure exactly as it does on the other unsupported terminals below.
            return new RelationalAuthorizationPlanOutcome.StillUnsupported(
                nonNamespaceStrategies,
                relationshipClassification!
            );
        }

        if (namespaceOutcome is NamespaceAuthorizationPlanOutcome.NoPrefixesConfigured noPrefixes)
        {
            ConfiguredAuthorizationStrategy namespaceStrategy = namespaceStrategies[0];

            return new RelationalAuthorizationPlanOutcome.NoPrefixesConfigured(
                noPrefixes.StrategyName,
                namespaceStrategy.RawConfiguredIndex
            )
            {
                CustomViewStrategies = supportedCustomViewStrategies,
            };
        }

        // Ranked here on purpose: after both namespace terminals, because Namespace-based and custom
        // view-based execute ahead of Ownership-based among the AND strategies; and before every
        // relationship terminal below, because the relationship OR group executes after all of them. The
        // two relationship security-configuration arms above already yield to it.
        //
        // Every resolved custom view is carried for validation, not just those configured before some
        // index, because ownership executes last among the AND strategies whatever position CMS gave it —
        // so every view genuinely runs ahead of this terminal.
        if (ownershipCapExceeded)
        {
            return new RelationalAuthorizationPlanOutcome.OwnershipTokenCapExceeded(
                context.OwnershipTokenIds.Count,
                AuthorizationStrategyNameConstants.OwnershipBased
            )
            {
                CustomViewStrategies = supportedCustomViewStrategies,
            };
        }

        // A caller that does not yet execute custom-view checks fails the request closed with 501 rather
        // than dropping the checks and serving unauthorized data. Ranked after both namespace terminals
        // above: like OwnershipBased — the other unimplemented AND strategy — an unimplemented custom
        // view does not displace an earlier Namespace terminal.
        if (classifiedCustomViewStrategies.Count > 0 && !enforcesCustomViewChecks)
        {
            return new RelationalAuthorizationPlanOutcome.StillUnsupported(
                nonNamespaceStrategies,
                relationshipClassification!
            );
        }

        if (
            relationshipClassification is
            { Outcome: RelationshipAuthorizationClassificationOutcome.KnownButNotEnabled }
        )
        {
            return new RelationalAuthorizationPlanOutcome.StillUnsupported(
                nonNamespaceStrategies,
                relationshipClassification
            );
        }

        // Prepare namespace checks.
        //
        // Every namespace check is stamped with the same configured position — the earliest index at
        // which NamespaceBased appears. This reads like a simplification but is not:
        //
        // - The namespace planner never receives the configured strategy list. It derives its checks
        //   from the operation alone (one for reads and delete, two for Update), so the check count
        //   carries no information about how many times NamespaceBased was configured.
        // - Both namespace failure outcomes are global: NoUsableRootColumn is a property of the
        //   resource model, NoPrefixesConfigured of the client's prefix list. Neither can make one
        //   configured occurrence fail while another succeeds.
        //
        // So the namespace filter is evaluated once no matter how many times it is configured, and the
        // position where it first executes — and therefore first fails — is the earliest occurrence.
        // namespaceStrategies is in configured order, so that is [0]. Stamping a later occurrence's
        // index would wrongly let a custom view configured between them validate ahead of this terminal.
        // Update's Stored/Proposed pair shares the index for the same reason: two value-sources of one
        // occurrence, not two occurrences.
        var namespaceChecks = namespaceOutcome is NamespaceAuthorizationPlanOutcome.Plan namespacePlan
            ? namespacePlan
                .Checks.Select(check =>
                    check with
                    {
                        RawConfiguredIndex = namespaceStrategies[0].RawConfiguredIndex,
                    }
                )
                .ToArray()
            : (IReadOnlyList<NamespaceAuthorizationCheckSpec>)[];

        // Exclude custom-view configured strategies from the non-namespace configured strategies
        // returned to the relationship planner for ReadMany; return them separately in the plan.
        var customViewStrategyRawIndexes = supportedCustomViewStrategies
            .Select(static s => s.ConfiguredStrategy.RawConfiguredIndex)
            .ToHashSet();

        IReadOnlyList<ConfiguredAuthorizationStrategy> relationshipConfiguredStrategies =
            nonNamespaceStrategies
                .Where(strategy => !customViewStrategyRawIndexes.Contains(strategy.RawConfiguredIndex))
                .ToArray();

        // Planned only where enforced, so the ownership bucket is empty for every operation and storage kind
        // both gates withhold — and in those cases the strategy is still in the relationship bucket earning
        // its known-but-not-enabled 501 above, so neither null can mean "dropped". Each gate plans only its
        // own shape: the single-record planner is never asked to plan ReadMany, which it rejects, and the
        // page filter is never planned for a single-record operation. An empty token list still plans the
        // page filter: emptiness is the repository's empty-page result, not a planning fact.
        var ownershipCheck =
            enforcesOwnershipChecks && ownershipStrategies.Count > 0
                ? OwnershipAuthorizationPlanner.Plan(operation, ownershipStrategies)
                : null;
        var ownershipPageFilter =
            enforcesOwnershipPageFilter && ownershipStrategies.Count > 0
                ? PlanPageOwnershipFilter(ownershipStrategies)
                : null;

        return new RelationalAuthorizationPlanOutcome.Plan(
            namespaceChecks,
            relationshipConfiguredStrategies,
            supportedCustomViewStrategies
        )
        {
            OwnershipCheck = ownershipCheck,
            OwnershipPageFilter = ownershipPageFilter,
        };
    }

    /// <summary>
    /// Whether the caller for this operation and storage kind executes the ownership check this planner
    /// would hand back. When it does not, <c>OwnershipBased</c> is left in the relationship bucket so the
    /// classifier reports it known-but-not-enabled and the request fails closed with 501, exactly as it did
    /// before ownership planning existed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns <see langword="true"/> for every single-record operation, and never for
    /// <see cref="NamespaceAuthorizationOperation.ReadMany"/> or descriptor storage. Each enforcement step
    /// flipped exactly one operation on in the same commit that added its execution, so no commit existed in
    /// which a planned ownership check had no executor.
    /// </para>
    /// <para>
    /// <see cref="ResourceStorageKind.SharedDescriptorTable"/> is withheld permanently for this story.
    /// Descriptor ownership enforcement is out of scope, and this named arm is what keeps that boundary
    /// deliberate: before ownership had its own bucket, descriptors were protected only incidentally, by
    /// <c>RelationalReadGuardrails.HasDescriptorUnsupportedNonNamespaceStrategies</c> catching every
    /// non-namespace strategy. Splitting ownership out would have removed that protection silently.
    /// Descriptor <em>stamping</em> is unaffected — it never consults configured strategies.
    /// </para>
    /// <para>
    /// <see cref="NamespaceAuthorizationOperation.ReadMany"/> is withheld here permanently: GET-many ownership
    /// is a page filter rather than a single-record check, and it has its own gate,
    /// <see cref="EnforcesOwnershipPageFilter"/>.
    /// </para>
    /// <para>
    /// Internal rather than private so its matrix can be pinned directly. A withheld operation returns the
    /// same known-but-not-enabled 501 whether this gate withheld it or the classifier never recognized the
    /// strategy, so a plan outcome alone cannot say which happened; only the predicate separates them, and
    /// the alternative was a test-only switch, which would be a worse thing to add.
    /// </para>
    /// </remarks>
    internal static bool EnforcesOwnershipChecks(
        NamespaceAuthorizationOperation operation,
        ResourceStorageKind storageKind
    ) =>
        storageKind is not ResourceStorageKind.SharedDescriptorTable
        && _ownershipEnforcedOperations.Contains(operation);

    /// <summary>
    /// Whether the caller for this operation and storage kind applies the <c>OwnershipBased</c> page filter
    /// this planner would hand back. True only for <see cref="NamespaceAuthorizationOperation.ReadMany"/> over
    /// relationally stored resources: the GET-many page and the partition candidate relation share that path,
    /// so both are filtered. When it is false and <see cref="EnforcesOwnershipChecks"/> is too,
    /// <c>OwnershipBased</c> is left in the relationship bucket so the classifier reports it
    /// known-but-not-enabled and the request fails closed with 501.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Descriptor storage is withheld: descriptor ownership enforcement is out of scope, and without this arm
    /// splitting ownership out of the bucket would silently remove the protection
    /// <c>RelationalReadGuardrails.HasDescriptorUnsupportedNonNamespaceStrategies</c> gives descriptor GET-many.
    /// Descriptor GET-many therefore keeps its namespace-terminal precedence unchanged, because the descriptor
    /// single-record terminal is gated on the single-record operation set rather than on this predicate.
    /// </para>
    /// <para>
    /// Internal so its matrix can be pinned directly, for the same reason the single-record gate is.
    /// </para>
    /// </remarks>
    internal static bool EnforcesOwnershipPageFilter(
        NamespaceAuthorizationOperation operation,
        ResourceStorageKind storageKind
    ) =>
        operation is NamespaceAuthorizationOperation.ReadMany
        && storageKind is not ResourceStorageKind.SharedDescriptorTable;

    /// <summary>
    /// Plans the single page filter for the configured <c>OwnershipBased</c> occurrences. The earliest
    /// configured occurrence, not the first list entry, so the result does not depend on caller ordering:
    /// configuring <c>OwnershipBased</c> more than once cannot make one occurrence pass and another fail,
    /// because the filter reads one column against one token list.
    /// </summary>
    private static PageOwnershipFilterSpec PlanPageOwnershipFilter(
        IReadOnlyList<ConfiguredAuthorizationStrategy> ownershipStrategies
    ) => new(ownershipStrategies.Min(static strategy => strategy.RawConfiguredIndex));

    /// <summary>
    /// Whether this request is a descriptor operation configured with <c>OwnershipBased</c> that the story
    /// leaves unimplemented, and so must report the known-but-not-enabled 501 rather than any namespace
    /// terminal that would otherwise be reported first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The complement of <see cref="EnforcesOwnershipChecks"/> for descriptor storage: that gate withholds
    /// enforcement, and this predicate is what makes the withheld case visible early enough to keep its own
    /// terminal. Without it the strategy still reaches the classifier and still earns its 501, but only when
    /// no namespace terminal is reported first — and a client with no namespace prefixes reports one every
    /// time, turning the descriptor scope boundary into a namespace 403.
    /// </para>
    /// <para>
    /// Gated on the same operation set as the enforcement gate rather than on all operations, so
    /// <see cref="NamespaceAuthorizationOperation.ReadMany"/> keeps its existing behavior: descriptor
    /// GET-many ownership filtering is DMS-1410's and is staged separately.
    /// </para>
    /// <para>
    /// Internal so the boundary can be pinned directly, for the same reason the enforcement gate is.
    /// </para>
    /// </remarks>
    internal static bool DescriptorOwnershipUnsupported(
        ResourceStorageKind storageKind,
        NamespaceAuthorizationOperation operation,
        IReadOnlyList<ConfiguredAuthorizationStrategy> nonNamespaceConfiguredStrategies
    )
    {
        ArgumentNullException.ThrowIfNull(nonNamespaceConfiguredStrategies);

        return storageKind is ResourceStorageKind.SharedDescriptorTable
            && _ownershipEnforcedOperations.Contains(operation)
            && nonNamespaceConfiguredStrategies.Any(IsOwnershipBased);
    }

    /// <summary>
    /// The operations whose callers execute the ownership check. Each enforcement step adds its own
    /// operation in the same commit that wires that operation's executor, so no operation can be planned a
    /// check nothing executes.
    /// </summary>
    /// <remarks>
    /// A membership set rather than a per-operation switch, so an operation absent from it is withheld by
    /// default: an operation added to the enum without an ownership executor keeps its 501 rather than
    /// silently inheriting enforcement it does not implement.
    /// <para>
    /// <see cref="NamespaceAuthorizationOperation.ReadMany"/> must never be added here. GET-many ownership is
    /// a page filter, not a single-record check — <see cref="OwnershipAuthorizationPlanner"/> throws if it is
    /// ever asked to plan one — and it is gated by <see cref="EnforcesOwnershipPageFilter"/> instead.
    /// </para>
    /// </remarks>
    private static readonly HashSet<NamespaceAuthorizationOperation> _ownershipEnforcedOperations =
    [
        // Every single-record operation. ReadMany is absent and must stay absent: GET-many ownership is a
        // page filter rather than a single-record check, and has its own gate.
        NamespaceAuthorizationOperation.ReadSingle,
        NamespaceAuthorizationOperation.Update,
        NamespaceAuthorizationOperation.Delete,
    ];

    /// <summary>
    /// Whether the ownership token-cap terminal may displace the relationship classifier's
    /// security-configuration failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The classifier's <c>SecurityConfigurationError</c> bucket is not purely relationship failures: it also
    /// carries custom view-based strategy-resolution failures, such as a
    /// <c>{BasisResource}With…</c> strategy whose basis resource does not exist
    /// (<see cref="RelationshipAuthorizationFailureKind.UnknownCustomViewBasisResource"/>). Those are
    /// AND-strategy failures. Every custom view executes ahead of Ownership-based among the AND strategies,
    /// whatever position CMS gave either, so a custom-view configuration failure keeps its own response and
    /// the ownership cap must yield to it — with no index comparison needed.
    /// </para>
    /// <para>
    /// A purely relationship or otherwise generic failure does yield to the cap, because the relationship OR
    /// group executes after every AND strategy. Both outcomes are a 500 with the same status and title, so
    /// this choice decides which diagnostic an operator sees rather than whether the request is refused.
    /// </para>
    /// <para>
    /// These are the classifier-level failures, which never become
    /// <c>SupportedCustomViewAuthorizationStrategy</c> entries and so are never validated through the
    /// resolved-custom-view path. That path is unaffected: a resolved view that turns out to be missing or
    /// non-conforming is still probed and still keeps its own 500, because the terminals carry their
    /// resolved views for validation.
    /// </para>
    /// <para>
    /// Internal so the rule can be pinned directly, which is how it was covered before any operation was
    /// enforced and no plan outcome could reach it. It is now also asserted behaviorally through
    /// <see cref="NamespaceAuthorizationOperation.ReadSingle"/>: an unresolved custom-view basis paired with
    /// an over-cap ownership token list must report the custom-view failure, not the cap terminal.
    /// </para>
    /// </remarks>
    internal static bool OwnershipCapOutranksClassifierFailure(
        bool ownershipCapExceeded,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> securityConfigurationFailures
    )
    {
        ArgumentNullException.ThrowIfNull(securityConfigurationFailures);

        return ownershipCapExceeded && !securityConfigurationFailures.Any(IsCustomViewConfigurationFailure);
    }

    /// <summary>
    /// Whether a classifier security-configuration failure is about a custom view rather than a relationship
    /// strategy. Kept as a list of kinds rather than a name-convention test so a kind added later is a
    /// compile-time decision here instead of silently defaulting to relationship precedence.
    /// </summary>
    private static bool IsCustomViewConfigurationFailure(RelationshipAuthorizationFailureMetadata failure) =>
        failure.FailureKind
            is RelationshipAuthorizationFailureKind.UnknownCustomViewBasisResource
                or RelationshipAuthorizationFailureKind.NoCustomViewJoinPath
                or RelationshipAuthorizationFailureKind.MissingProposedCustomViewRootBinding;

    /// <summary>
    /// Whether the caller for this operation executes the custom-view checks this planner hands back. When it
    /// does not, a configured custom view fails the request closed with 501 instead of being silently dropped —
    /// dropping it would serve data the strategy was configured to restrict.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every operation's callers now execute them, for both storage kinds: the regular-resource paths carry the
    /// checks in their <c>AUTH1</c> statements, and the descriptor paths — which have no <c>AUTH1</c> statement
    /// to carry them — run them as their own membership query ordered against that path's namespace check.
    /// <c>Update</c> covers both write verbs, because a POST resolves to a create or an upsert-as-update only
    /// in-session, so both value sources are planned the same way.
    /// </para>
    /// <para>
    /// The predicate is kept rather than inlined so an operation added without such a caller defaults to
    /// failing closed instead of inheriting enforcement it does not implement.
    /// </para>
    /// </remarks>
    private static bool EnforcesCustomViewChecks(NamespaceAuthorizationOperation operation) =>
        operation switch
        {
            NamespaceAuthorizationOperation.ReadMany
            or NamespaceAuthorizationOperation.ReadSingle
            or NamespaceAuthorizationOperation.Delete
            or NamespaceAuthorizationOperation.Update => true,
            // Unreachable for today's operations, and deliberately fail-closed: an operation added without a
            // caller that executes the checks must keep its 501 rather than silently dropping them.
            _ => false,
        };

    /// <summary>
    /// Lowest <c>RawConfiguredIndex</c> among <paramref name="failures"/>. A failure without a configured
    /// strategy has no determinable configured position, so it is treated as preceding every configured
    /// strategy — which fails closed by keeping the failure as the terminal and making no earlier
    /// AND-ordered strategy (e.g. a custom view) eligible to run ahead of it.
    /// </summary>
    public static int EarliestSecurityConfigurationFailureIndex(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    )
    {
        ArgumentNullException.ThrowIfNull(failures);

        return failures
            .Select(static failure => failure.ConfiguredStrategy?.RawConfiguredIndex ?? int.MinValue)
            .DefaultIfEmpty(int.MinValue)
            .Min();
    }

    private static (
        IReadOnlyList<ConfiguredAuthorizationStrategy> Namespace,
        IReadOnlyList<ConfiguredAuthorizationStrategy> NonNamespace
    ) SplitByNamespaceBased(IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies)
    {
        List<ConfiguredAuthorizationStrategy> namespaceStrategies = [];
        List<ConfiguredAuthorizationStrategy> nonNamespaceStrategies = [];

        foreach (var configuredStrategy in configuredAuthorizationStrategies)
        {
            if (
                string.Equals(
                    configuredStrategy.StrategyName,
                    AuthorizationStrategyNameConstants.NamespaceBased,
                    StringComparison.Ordinal
                )
            )
            {
                namespaceStrategies.Add(configuredStrategy);
            }
            else
            {
                nonNamespaceStrategies.Add(configuredStrategy);
            }
        }

        return (namespaceStrategies, nonNamespaceStrategies);
    }

    /// <summary>
    /// Splits <c>OwnershipBased</c> out of the non-namespace bucket, preserving configured order in both.
    /// </summary>
    private static (
        IReadOnlyList<ConfiguredAuthorizationStrategy> Ownership,
        IReadOnlyList<ConfiguredAuthorizationStrategy> NonOwnership
    ) SplitByOwnershipBased(IReadOnlyList<ConfiguredAuthorizationStrategy> nonNamespaceConfiguredStrategies)
    {
        List<ConfiguredAuthorizationStrategy> ownershipStrategies = [];
        List<ConfiguredAuthorizationStrategy> nonOwnershipStrategies = [];

        foreach (var configuredStrategy in nonNamespaceConfiguredStrategies)
        {
            if (IsOwnershipBased(configuredStrategy))
            {
                ownershipStrategies.Add(configuredStrategy);
            }
            else
            {
                nonOwnershipStrategies.Add(configuredStrategy);
            }
        }

        return (ownershipStrategies, nonOwnershipStrategies);
    }

    /// <summary>
    /// Whether a configured strategy is <c>OwnershipBased</c>. One definition so the ownership split and the
    /// descriptor boundary can never drift apart on how the strategy is recognized.
    /// </summary>
    private static bool IsOwnershipBased(ConfiguredAuthorizationStrategy configuredStrategy) =>
        string.Equals(
            configuredStrategy.StrategyName,
            AuthorizationStrategyNameConstants.OwnershipBased,
            StringComparison.Ordinal
        );
}
