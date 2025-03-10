// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.AuthorizationMetadata;

public record AuthorizationMetadataResponse(
    List<AuthorizationMetadataResponse.Claim> Claims,
    List<AuthorizationMetadataResponse.Authorization> Authorizations
)
{
    public record Claim(string Name, int AuthorizationId);

    public record Authorization(int Id, Action[] Actions)
    {
        private readonly Lazy<int> _hashCode = new(() => ComputeHashCode(Actions));

        public override int GetHashCode() => _hashCode.Value;

        private static int ComputeHashCode(Action[] actions)
        {
            int hash = 0;

            if (actions.Length > 0)
            {
                foreach (var action in actions.OrderBy(a => a.Name))
                {
                    hash = HashCode.Combine(hash, action.GetHashCode());
                }
            }

            return hash;
        }
    }

    public record Action(string Name, AuthorizationStrategy[] AuthorizationStrategies)
    {
        private readonly Lazy<int> _hashCode = new(() => ComputeHashCode(Name, AuthorizationStrategies));

        public override int GetHashCode() => _hashCode.Value;

        private static int ComputeHashCode(string name, AuthorizationStrategy[] strategies)
        {
            int hash = name.GetHashCode();

            foreach (var strategy in strategies.OrderBy(a => a.Name))
            {
                hash = HashCode.Combine(hash, strategy.GetHashCode());
            }

            return hash;
        }
    }

    public record AuthorizationStrategy(string Name);
}
