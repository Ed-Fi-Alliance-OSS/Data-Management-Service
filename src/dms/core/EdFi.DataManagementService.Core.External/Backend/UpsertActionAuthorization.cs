// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// The resource action a POST performs once its target is known: <see cref="Create"/> when the natural key
/// identifies no existing document, <see cref="Update"/> when it does.
/// </summary>
public enum UpsertTargetAction
{
    Create,
    Update,
}

/// <summary>
/// What the API client's claim set permits for one resource action. Deliberately message-free: why an
/// action is not permitted, and how that is rendered, stays with Core, which holds the claim-set evidence.
/// </summary>
public abstract record UpsertActionPolicy
{
    private UpsertActionPolicy() { }

    /// <summary>
    /// The action is granted with at least one authorization strategy.
    /// </summary>
    public sealed record Permitted : UpsertActionPolicy
    {
        public Permitted(AuthorizationStrategyEvaluator[] evaluators)
        {
            ArgumentNullException.ThrowIfNull(evaluators);

            if (evaluators.Length == 0)
            {
                throw new ArgumentException(
                    "A permitted action requires at least one authorization strategy.",
                    nameof(evaluators)
                );
            }

            if (Array.Exists(evaluators, static evaluator => evaluator is null))
            {
                throw new ArgumentException(
                    "Authorization strategy evaluators must not contain null entries.",
                    nameof(evaluators)
                );
            }

            Evaluators = [.. evaluators];
        }

        /// <summary>
        /// The action's authorization strategy evaluators, in configured order.
        /// </summary>
        public AuthorizationStrategyEvaluator[] Evaluators { get; }
    }

    /// <summary>
    /// The action is not granted, or is granted with no authorization strategies.
    /// </summary>
    public sealed record NotPermitted : UpsertActionPolicy
    {
        public static NotPermitted Instance { get; } = new();

        private NotPermitted() { }
    }
}

/// <summary>
/// The Create and Update policies a POST chooses between once the write observes its target.
/// </summary>
public sealed record UpsertActionAuthorization
{
    public UpsertActionAuthorization(UpsertActionPolicy create, UpsertActionPolicy update)
    {
        Create = create ?? throw new ArgumentNullException(nameof(create));
        Update = update ?? throw new ArgumentNullException(nameof(update));
    }

    /// <summary>
    /// The policy applied when the POST creates a new document.
    /// </summary>
    public UpsertActionPolicy Create { get; }

    /// <summary>
    /// The policy applied when the POST updates the document its natural key identifies.
    /// </summary>
    public UpsertActionPolicy Update { get; }

    /// <summary>
    /// The policy for the given target action.
    /// </summary>
    public UpsertActionPolicy For(UpsertTargetAction action) =>
        action switch
        {
            UpsertTargetAction.Create => Create,
            UpsertTargetAction.Update => Update,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };

    /// <summary>
    /// Permits both actions with the same evaluators. An explicit opt-in for callers whose Create and Update
    /// strategies are intentionally identical.
    /// </summary>
    public static UpsertActionAuthorization SamePolicyForCreateAndUpdate(
        AuthorizationStrategyEvaluator[] evaluators
    )
    {
        var policy = new UpsertActionPolicy.Permitted(evaluators);
        return new UpsertActionAuthorization(policy, policy);
    }

    /// <summary>
    /// Returns true, with the shared evaluators, when both actions are permitted with semantically identical
    /// evaluator sequences: the same strategy names compared ordinally, the same filters and the same
    /// operator, position by position. Order and duplicates are significant because configured position
    /// drives strategy precedence and failure attribution.
    /// </summary>
    public bool TryGetSharedPolicy(out AuthorizationStrategyEvaluator[] evaluators)
    {
        if (
            Create is UpsertActionPolicy.Permitted create
            && Update is UpsertActionPolicy.Permitted update
            && create.Evaluators.Length == update.Evaluators.Length
            && create
                .Evaluators.Zip(update.Evaluators)
                .All(static pair => AreEquivalent(pair.First, pair.Second))
        )
        {
            evaluators = create.Evaluators;
            return true;
        }

        evaluators = [];
        return false;
    }

    private static bool AreEquivalent(
        AuthorizationStrategyEvaluator left,
        AuthorizationStrategyEvaluator right
    ) =>
        string.Equals(
            left.AuthorizationStrategyName,
            right.AuthorizationStrategyName,
            StringComparison.Ordinal
        )
        && left.Operator == right.Operator
        && left.Filters.SequenceEqual(right.Filters);
}
