// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Claims.Models;

namespace EdFi.DmsConfigurationService.Backend.Claims;

/// <summary>
/// Service for composing claims from base Claims.json and fragment files
/// </summary>
public interface IClaimsFragmentComposer
{
    /// <summary>
    /// Composes claims from base Claims.json and fragment files in the specified directory
    /// </summary>
    /// <param name="baseClaimsNodes">Base claims document nodes</param>
    /// <param name="fragmentsPath">Path to directory containing fragment files (*-claims.json)</param>
    /// <returns>Composed claims document nodes with all fragments applied</returns>
    ClaimsLoadResult ComposeClaimsFromFragments(ClaimsDocument baseClaimsNodes, string fragmentsPath);

    /// <summary>
    /// Discovers fragment files matching the pattern *-claims.json in the specified directory
    /// </summary>
    /// <param name="fragmentsPath">Path to directory to search for fragment files</param>
    /// <returns>List of fragment file paths found</returns>
    List<string> DiscoverFragmentFiles(string fragmentsPath);
}
