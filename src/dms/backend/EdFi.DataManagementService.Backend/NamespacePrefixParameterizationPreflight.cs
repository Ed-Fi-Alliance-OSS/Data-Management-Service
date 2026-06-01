// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Shared preflight for building the dialect-specific namespace prefix parameterization. Centralizes the
/// <see cref="NamespacePrefixParameterizationFactory.Create"/> call (with the shared prefix parameter
/// name) and the SQL Server prefix-cap failure so every read, write, delete, and descriptor authorization
/// path constructs the parameterization and its <see cref="NamespacePrefixLimitExceededException"/>
/// security-configuration message identically.
/// </summary>
internal static class NamespacePrefixParameterizationPreflight
{
    /// <summary>
    /// Builds the namespace prefix parameterization for <paramref name="dialect"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> with <paramref name="parameterization"/> populated on success;
    /// <see langword="false"/> with <paramref name="prefixCapExceededMessage"/> set to the
    /// security-configuration diagnostic when the SQL Server prefix cap is exceeded. Callers wrap that
    /// message in their operation-specific security-configuration result.
    /// </returns>
    public static bool TryCreate(
        SqlDialect dialect,
        IReadOnlyList<string> namespacePrefixes,
        out NamespacePrefixParameterization parameterization,
        out string prefixCapExceededMessage
    )
    {
        try
        {
            parameterization = NamespacePrefixParameterizationFactory.Create(
                dialect,
                namespacePrefixes,
                NamespaceAuthorizationSqlSpecDefaults.NamespacePrefixesParameterName
            );
            prefixCapExceededMessage = string.Empty;
            return true;
        }
        catch (NamespacePrefixLimitExceededException ex)
        {
            parameterization = null!;
            prefixCapExceededMessage = NamespaceAuthorizationSecurityConfigurationMessages.PrefixCapExceeded(
                ex.PrefixCount
            );
            return false;
        }
    }
}
