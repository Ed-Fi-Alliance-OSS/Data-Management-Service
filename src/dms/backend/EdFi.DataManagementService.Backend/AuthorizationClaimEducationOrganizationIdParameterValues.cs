// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

internal static class AuthorizationClaimEducationOrganizationIdParameterValues
{
    public static void AddTo(
        IDictionary<string, object?> parameterValues,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization
    )
    {
        ArgumentNullException.ThrowIfNull(parameterValues);
        ArgumentNullException.ThrowIfNull(authorizationClaimParameterization);

        switch (authorizationClaimParameterization.Kind)
        {
            case AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray:
            case AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured:
                parameterValues[authorizationClaimParameterization.BaseParameterName] =
                    authorizationClaimParameterization.ClaimEducationOrganizationIds;
                return;

            case AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar:
                for (
                    var parameterIndex = 0;
                    parameterIndex < authorizationClaimParameterization.ParameterNamesInOrder.Count;
                    parameterIndex++
                )
                {
                    parameterValues[
                        authorizationClaimParameterization.ParameterNamesInOrder[parameterIndex]
                    ] = authorizationClaimParameterization.ClaimEducationOrganizationIds[parameterIndex];
                }

                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(authorizationClaimParameterization),
                    authorizationClaimParameterization.Kind,
                    "Unsupported authorization claim EdOrg parameterization kind."
                );
        }
    }
}
