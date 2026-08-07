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
    ) : RelationalAuthorizationPlanOutcome;

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
/// <item><see cref="RelationalAuthorizationPlanOutcome.NoPrefixesConfigured"/> — <c>NamespaceBased</c> is configured
/// and the client has no namespace prefixes (403, preflight). Namespace-based is AND-combined and executes
/// ahead of relationship OR-combined strategies, so its 403 wins over a sibling
/// known-but-not-enabled relationship strategy.</item>
/// <item><see cref="RelationalAuthorizationPlanOutcome.StillUnsupported"/> — the relationship classifier reports a
/// known-but-not-enabled strategy in the non-namespace bucket (501 NotImplemented, fail closed).</item>
/// <item><see cref="RelationalAuthorizationPlanOutcome.Plan"/> — everything else.</item>
/// </list>
/// <para>
/// <c>OwnershipBased</c> is known but not enabled for every operation, <c>ReadMany</c> included: DMS-1060 owns
/// the complete strategy — the tenant-qualified CMS application-context token source and write-side
/// <c>CreatedByOwnershipTokenId</c> stamping — and is still open. DMS-1062 therefore never promotes it to a
/// supported AND filter, so it always reaches its fail-closed 501 rather than filtering a page against
/// ownership context this story cannot provision. A custom view configured ahead of that terminal is still
/// validated first, so an earlier custom-view configuration failure keeps its own response.
/// </para>
/// </remarks>
public static class RelationalAuthorizationPlanner
{
    public static RelationalAuthorizationPlanOutcome Plan(
        MappingSet mappingSet,
        ConcreteResourceModel resource,
        NamespaceAuthorizationOperation operation,
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies,
        RelationalAuthorizationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(configuredAuthorizationStrategies);
        ArgumentNullException.ThrowIfNull(context);

        var (namespaceStrategies, nonNamespaceStrategies) = SplitByNamespaceBased(
            configuredAuthorizationStrategies
        );

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

        if (hasSecurityConfigurationError && operation is not NamespaceAuthorizationOperation.ReadMany)
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
            // ReadMany: Namespace-based and custom view-based are AND strategies that execute in
            // CMS-configured order, so a classifier failure must not leap ahead of a Namespace
            // terminal configured before it. Both namespace terminals participate — no configured
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

            if (!namespaceTerminalPrecedesFailure)
            {
                return new RelationalAuthorizationPlanOutcome.SecurityConfigurationError(
                    nonNamespaceStrategies,
                    relationshipClassification!
                );
            }
        }

        var classifiedCustomViewStrategies = relationshipClassification?.SupportedCustomViewStrategies ?? [];
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> supportedCustomViewStrategies =
            operation is NamespaceAuthorizationOperation.ReadMany ? classifiedCustomViewStrategies : [];

        if (namespaceOutcome is NamespaceAuthorizationPlanOutcome.NoUsableRootColumn noUsableRoot)
        {
            // namespaceOutcome is non-null only when the namespace bucket is non-empty.
            return new RelationalAuthorizationPlanOutcome.NoUsableRootColumn(noUsableRoot.Resource)
            {
                RawConfiguredIndex = namespaceStrategies[0].RawConfiguredIndex,
                CustomViewStrategies = supportedCustomViewStrategies,
            };
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

        // Custom-view strategies are only implemented for ReadMany. For every other operation their
        // presence fails the request closed with 501, ranked after both namespace terminals above:
        // like OwnershipBased — the other unimplemented AND strategy — an unimplemented custom view
        // does not displace an earlier Namespace terminal.
        if (
            classifiedCustomViewStrategies.Count > 0
            && operation is not NamespaceAuthorizationOperation.ReadMany
        )
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

        return new RelationalAuthorizationPlanOutcome.Plan(
            namespaceChecks,
            relationshipConfiguredStrategies,
            supportedCustomViewStrategies
        );
    }

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
}
