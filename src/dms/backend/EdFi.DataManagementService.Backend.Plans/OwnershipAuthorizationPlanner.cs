// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Security;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// The single planned ownership authorization check for a request.
/// </summary>
/// <param name="RawConfiguredIndex">
/// Zero-based position of <c>OwnershipBased</c> in the CMS-configured strategy list. Carried into the
/// emitted <c>own1</c> AUTH1 payload, so a denial identifies the configured strategy that produced it.
/// </param>
/// <param name="StrategyName">The configured strategy name — always <c>OwnershipBased</c>.</param>
/// <remarks>
/// <para>
/// Deliberately thinner than <see cref="NamespaceAuthorizationCheckSpec"/>, which carries a value source, a
/// root table, and a column. Ownership needs none of those: it evaluates only stored values, and its subject
/// column is <c>dms.Document.CreatedByOwnershipTokenId</c> addressed by <c>DocumentId</c>, the same for every
/// resource. There is no securable element to resolve and so nothing resource-specific to record.
/// </para>
/// <para>
/// No emitted-ordinal field either. Ownership emits exactly one check, so an emitted ordinal would be a
/// constant zero; the AUTH1 payload carries this configured index instead.
/// </para>
/// </remarks>
public sealed record OwnershipAuthorizationCheckSpec(
    int RawConfiguredIndex,
    string StrategyName = AuthorizationStrategyNameConstants.OwnershipBased
);

/// <summary>
/// Plans the ownership authorization check for a single-record CRUD operation.
/// </summary>
/// <remarks>
/// <para>
/// Returns the check directly rather than an outcome union. The namespace planner needs one because it can
/// fail on inputs it inspects — no root-table Namespace column resolves, or the client has no prefixes.
/// Ownership planning inspects neither the resource model nor the client's token list: the subject column is
/// resource-independent, and an empty token list is a valid configuration that still executes the check so
/// the response can distinguish a stored null (§2.14) from a non-matching stored value (§2.13). The one
/// ownership failure that precedes execution — the defensive token limit — is a terminal on
/// <see cref="RelationalAuthorizationPlanOutcome"/>, ranked against the other strategies' terminals, not an
/// outcome of this planner.
/// </para>
/// <para>
/// Callers must invoke this only for an operation and storage kind where ownership is enforced;
/// <c>RelationalAuthorizationPlanner</c> owns that gate.
/// </para>
/// </remarks>
public static class OwnershipAuthorizationPlanner
{
    /// <param name="operation">
    /// The CRUD operation. <c>ReadSingle</c>, <c>Update</c> and <c>Delete</c> each plan one stored check;
    /// <c>Update</c> covers both write verbs because a POST resolves to a create or an upsert-as-update only
    /// in-session.
    /// </param>
    /// <param name="configuredOwnershipStrategies">
    /// Every configured <c>OwnershipBased</c> strategy, which must be non-empty.
    /// </param>
    public static OwnershipAuthorizationCheckSpec Plan(
        NamespaceAuthorizationOperation operation,
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredOwnershipStrategies
    )
    {
        ArgumentNullException.ThrowIfNull(configuredOwnershipStrategies);

        if (configuredOwnershipStrategies.Count == 0)
        {
            throw new ArgumentException(
                "Ownership authorization planning requires at least one configured OwnershipBased strategy.",
                nameof(configuredOwnershipStrategies)
            );
        }

        // A strategy that is not OwnershipBased reaching here means the caller's bucket split is wrong, which
        // would silently authorize the wrong strategy's subject. Fail loudly instead.
        var foreignStrategy = configuredOwnershipStrategies.FirstOrDefault(static strategy =>
            !string.Equals(
                strategy.StrategyName,
                AuthorizationStrategyNameConstants.OwnershipBased,
                StringComparison.Ordinal
            )
        );

        if (foreignStrategy is not null)
        {
            throw new ArgumentException(
                $"Ownership authorization planning received strategy '{foreignStrategy.StrategyName}', but only '{AuthorizationStrategyNameConstants.OwnershipBased}' is an ownership strategy.",
                nameof(configuredOwnershipStrategies)
            );
        }

        if (
            operation
            is not (
                NamespaceAuthorizationOperation.ReadSingle
                or NamespaceAuthorizationOperation.Update
                or NamespaceAuthorizationOperation.Delete
            )
        )
        {
            // Unreachable while the enablement gate withholds every other operation, and kept as a guard
            // rather than a silent default: GET-many ownership filtering is a different shape of check
            // (DMS-1410) and must not be served by this single-record plan.
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Ownership authorization planning supports only single-record operations."
            );
        }

        // The earliest configured occurrence, not the first list entry, so the result does not depend on
        // caller ordering. Configuring OwnershipBased more than once cannot make one occurrence pass and
        // another fail: the check reads one column against one token list, so it evaluates once however many
        // times it is configured, and the position where it first executes is the earliest occurrence.
        // Stamping a later one would let a custom view configured between them validate ahead of a terminal
        // it actually follows.
        var earliestConfiguredIndex = configuredOwnershipStrategies.Min(static strategy =>
            strategy.RawConfiguredIndex
        );

        return new OwnershipAuthorizationCheckSpec(earliestConfiguredIndex);
    }
}
