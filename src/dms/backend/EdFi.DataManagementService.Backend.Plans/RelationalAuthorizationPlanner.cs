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
    /// strategies through the relationship planner. Both lists may be empty.
    /// </summary>
    public sealed record Plan(
        IReadOnlyList<NamespaceAuthorizationCheckSpec> NamespaceChecks,
        IReadOnlyList<ConfiguredAuthorizationStrategy> NonNamespaceConfiguredStrategies
    ) : RelationalAuthorizationPlanOutcome;

    /// <summary>
    /// <c>NamespaceBased</c> is configured but no securable element resolves to the resource's
    /// concrete root-table column. Maps to a 500 Security Configuration Error.
    /// </summary>
    public sealed record NoUsableRootColumn(QualifiedResourceName Resource)
        : RelationalAuthorizationPlanOutcome;

    /// <summary>
    /// <c>NamespaceBased</c> is configured and the client has no namespace prefixes assigned.
    /// Maps to the no-prefixes-configured 403 ProblemDetails at planner/preflight time — no DB
    /// roundtrip is issued.
    /// </summary>
    public sealed record NoPrefixesConfigured(string StrategyName) : RelationalAuthorizationPlanOutcome;

    /// <summary>
    /// At least one configured strategy is known but not yet supported (e.g. <c>OwnershipBased</c> or
    /// a custom view-based strategy). The request fails closed. The non-namespace strategies are carried
    /// so the caller can re-run the relationship planner and surface the exact fail-closed result.
    /// </summary>
    public sealed record StillUnsupported(
        IReadOnlyList<ConfiguredAuthorizationStrategy> NonNamespaceConfiguredStrategies
    ) : RelationalAuthorizationPlanOutcome;

    /// <summary>
    /// At least one configured strategy is unrecognized or otherwise invalid. The relationship
    /// classifier reports a security configuration error and the request fails with 500. The
    /// non-namespace strategies are carried so the caller can re-run the relationship planner and
    /// surface the exact security-configuration diagnostics.
    /// </summary>
    public sealed record SecurityConfigurationError(
        IReadOnlyList<ConfiguredAuthorizationStrategy> NonNamespaceConfiguredStrategies
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
/// reports an unrecognized or invalid strategy in the non-namespace bucket (500).</item>
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

        // SecurityConfigurationError (500) and StillUnsupported (403) are detected by the existing
        // relationship classifier; invoke it only when the non-namespace bucket is non-empty.
        RelationshipAuthorizationClassification? relationshipClassification =
            nonNamespaceStrategies.Count == 0
                ? null
                : RelationshipAuthorizationStrategyClassifier.Classify(
                    mappingSet,
                    resource.RelationalModel.Resource,
                    nonNamespaceStrategies
                );

        if (
            relationshipClassification is
            { Outcome: RelationshipAuthorizationClassificationOutcome.SecurityConfigurationError }
        )
        {
            return new RelationalAuthorizationPlanOutcome.SecurityConfigurationError(nonNamespaceStrategies);
        }

        NamespaceAuthorizationPlanOutcome? namespaceOutcome =
            namespaceStrategies.Count == 0
                ? null
                : NamespaceAuthorizationPlanner.Plan(resource, operation, context);

        if (namespaceOutcome is NamespaceAuthorizationPlanOutcome.NoUsableRootColumn noUsableRoot)
        {
            return new RelationalAuthorizationPlanOutcome.NoUsableRootColumn(noUsableRoot.Resource);
        }

        if (namespaceOutcome is NamespaceAuthorizationPlanOutcome.NoPrefixesConfigured noPrefixes)
        {
            return new RelationalAuthorizationPlanOutcome.NoPrefixesConfigured(noPrefixes.StrategyName);
        }

        if (
            relationshipClassification is
            { Outcome: RelationshipAuthorizationClassificationOutcome.KnownButNotEnabled }
        )
        {
            return new RelationalAuthorizationPlanOutcome.StillUnsupported(nonNamespaceStrategies);
        }

        var namespaceChecks = namespaceOutcome is NamespaceAuthorizationPlanOutcome.Plan namespacePlan
            ? namespacePlan.Checks
            : (IReadOnlyList<NamespaceAuthorizationCheckSpec>)[];

        return new RelationalAuthorizationPlanOutcome.Plan(namespaceChecks, nonNamespaceStrategies);
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
