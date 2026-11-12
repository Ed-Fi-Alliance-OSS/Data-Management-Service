// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;

public record AuthorizationMetadataResponse(IList<ClaimSetMetadata> ClaimSets);

public record ClaimSetMetadata(
    string ClaimSetName,
    List<ClaimSetMetadata.Claim> Claims,
    List<ClaimSetMetadata.Authorization> Authorizations
)
{
    public record Claim(string Name, int AuthorizationId);

    public record Authorization(int Id, Action[] Actions)
    {
        public override int GetHashCode() => ComputeHashCode(Actions);

        public virtual bool Equals(Authorization? other)
        {
            if (other is null)
            {
                return false;
            }

            if (ReferenceEquals(this, other))
            {
                return true;
            }

            if (Actions.Length != other.Actions.Length)
            {
                return false;
            }

            // Match each occurrence once: order is irrelevant, but multiplicity is not.
            var unmatched = other.Actions.ToList();
            foreach (var action in Actions)
            {
                int index = unmatched.FindIndex(candidate => ActionsEqual(action, candidate));
                if (index < 0)
                {
                    return false;
                }

                unmatched.RemoveAt(index);
            }

            return true;
        }

        private static int ComputeHashCode(Action[] actions)
        {
            // Commutative accumulation also handles reordered actions with the same name.
            int hash = actions.Length;
            foreach (var action in actions)
            {
                hash = unchecked(hash + action.GetHashCode());
            }

            return hash;
        }
    }

    public record Action(string Name, AuthorizationStrategy[] AuthorizationStrategies)
    {
        public override int GetHashCode() => ComputeHashCode(Name, AuthorizationStrategies);

        public virtual bool Equals(Action? other) =>
            other is not null && EqualityContract == other.EqualityContract && ActionsEqual(this, other);

        private static int ComputeHashCode(string name, AuthorizationStrategy[] strategies)
        {
            int hash = StringComparer.Ordinal.GetHashCode(name);

            foreach (var strategy in strategies.OrderBy(a => a.Name, StringComparer.Ordinal))
            {
                hash = HashCode.Combine(hash, strategy.GetHashCode());
            }

            return hash;
        }
    }

    private static bool ActionsEqual(Action left, Action right) =>
        StringComparer.Ordinal.Equals(left.Name, right.Name)
        && left.AuthorizationStrategies.Length == right.AuthorizationStrategies.Length
        && left.AuthorizationStrategies.Select(s => s.Name)
            .Order(StringComparer.Ordinal)
            .SequenceEqual(
                right.AuthorizationStrategies.Select(s => s.Name).Order(StringComparer.Ordinal),
                StringComparer.Ordinal
            );

    public record AuthorizationStrategy(string Name);
}
