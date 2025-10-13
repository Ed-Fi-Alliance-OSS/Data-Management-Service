// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;

namespace EdFi.DmsConfigurationService.Backend
{
    public static class ClientClaimHelper
    {
        public static Dictionary<string, string> CreateNamespacePrefixClaim(string namespacePrefixes)
        {
            return new Dictionary<string, string>
            {
                { "claim.name", "namespacePrefixes" },
                { "claim.value", namespacePrefixes },
                { "jsonType.label", "String" },
            };
        }

        public static Dictionary<string, string> CreateEducationOrganizationClaim(
            string educationOrganizationIds
        )
        {
            return new Dictionary<string, string>
            {
                { "claim.name", "educationOrganizationIds" },
                { "claim.value", educationOrganizationIds },
                { "jsonType.label", "String" },
            };
        }

        public static Dictionary<string, string> CreateDmsInstanceIdsClaim(string dmsInstanceIds)
        {
            return new Dictionary<string, string>
            {
                { "claim.name", "dmsInstanceIds" },
                { "claim.value", dmsInstanceIds },
                { "jsonType.label", "String" },
            };
        }

        public static Dictionary<string, string> CreateRoleClaim(
            string roleClaimType,
            string userAttribute = "roles"
        )
        {
            return new Dictionary<string, string>
            {
                { "claim.name", roleClaimType },
                { "user.attribute", userAttribute },
                { "jsonType.label", "String" },
                { "multivalued", "true" },
            };
        }
    }
}
