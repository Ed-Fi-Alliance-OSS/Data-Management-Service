// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.EducationOrganizationProjection;

/// <summary>
/// The education-organization projection contract versions this deployment serves.
/// </summary>
/// <remarks>
/// Matching is exact and case-sensitive: a client sends a version it read from Discovery, so a
/// differently spelled value is not one this deployment advertised. Public so the frontend's
/// Discovery document advertises exactly the list the request parser accepts.
/// </remarks>
public static class ProjectionContractVersions
{
    public const string V1 = "educationOrganizationProjection.v1";

    /// <summary>
    /// The version a request that omits <c>contractVersion</c> is answered in.
    /// </summary>
    public const string Default = V1;

    /// <summary>
    /// Every served version, in the order Discovery advertises them.
    /// </summary>
    public static IReadOnlyList<string> Supported { get; } = [V1];

    public static bool IsSupported(string contractVersion) =>
        Supported.Contains(contractVersion, StringComparer.Ordinal);
}
