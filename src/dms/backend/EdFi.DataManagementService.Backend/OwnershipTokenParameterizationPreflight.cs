// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Builds the ownership-token parameterization for a request, converting the factory's defensive-limit throw
/// into the security-configuration message and diagnostics a caller wraps in its own result type.
/// </summary>
/// <remarks>
/// Defence in depth on GET-by-id, PUT and DELETE, and the gate on POST. On the first three the planner
/// returns its own token-cap terminal, which is what gives the failure correct precedence among the other
/// authorization terminals, so a request that reaches this preflight is already known to be under the limit;
/// this layer exists so that a caller which somehow skipped the planner still fails closed with a clean 500
/// instead of emitting an over-limit parameter list at the SQL boundary or letting the factory's exception
/// escape as a generic failure. POST defers the planner's cap to target resolution, so this is where its
/// over-limit list is caught, and the failure reported here is carried into the write session to be returned
/// only if the target proves to exist.
/// </remarks>
internal static class OwnershipTokenParameterizationPreflight
{
    /// <returns>
    /// <see langword="true"/> with <paramref name="parameterization"/> populated on success;
    /// <see langword="false"/> with <paramref name="securityConfigurationMessage"/> and
    /// <paramref name="securityConfigurationDiagnostics"/> set when the token list reaches the defensive
    /// limit.
    /// </returns>
    public static bool TryCreate(
        SqlDialect dialect,
        IReadOnlyList<short> ownershipTokenIds,
        out OwnershipTokenParameterization parameterization,
        out string securityConfigurationMessage,
        out SecurityConfigurationFailureDiagnostic[] securityConfigurationDiagnostics
    )
    {
        try
        {
            parameterization = OwnershipTokenParameterizationFactory.Create(
                dialect,
                ownershipTokenIds,
                OwnershipAuthorizationSqlSpecDefaults.OwnershipTokenIdsParameterName
            );
            securityConfigurationMessage = string.Empty;
            securityConfigurationDiagnostics = [];
            return true;
        }
        catch (OwnershipTokenLimitExceededException ex)
        {
            parameterization = null!;
            securityConfigurationMessage =
                OwnershipAuthorizationSecurityConfigurationMessages.TokenCapExceeded(ex.OwnershipTokenCount);
            securityConfigurationDiagnostics =
                AuthorizationSecurityConfigurationDiagnostics.ForOwnershipTokenParameterization(
                    AuthorizationSecurityConfigurationDiagnostics.OwnershipTokenCapExceeded
                );
            return false;
        }
    }
}

/// <summary>
/// Parameter names the ownership authorization SQL binds. Declared here rather than on the compiler so the
/// preflight, the compiler, and the command parameter builder cannot drift apart.
/// </summary>
internal static class OwnershipAuthorizationSqlSpecDefaults
{
    public const string DocumentIdParameterName = "documentId";
    public const string OwnershipTokenIdsParameterName = "ownershipTokenIds";
}
