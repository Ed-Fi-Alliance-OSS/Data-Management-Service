// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Claims.Models;
using Microsoft.Extensions.Logging;
using SharedClaimsFragmentComposer = EdFi.DmsConfigurationService.Claims.Composition.ClaimsFragmentComposer;

namespace EdFi.DmsConfigurationService.Backend.Claims;

/// <summary>
/// CMS adapter for shared fragment composition. Forward the CMS logger to preserve its
/// category and return the existing loading contract so provider fallback remains unchanged.
/// </summary>
public class ClaimsFragmentComposer(ILogger<ClaimsFragmentComposer> logger) : IClaimsFragmentComposer
{
    private readonly SharedClaimsFragmentComposer _composer = new(logger);

    public ClaimsLoadResult ComposeClaimsFromFragments(
        ClaimsDocument baseClaimsNodes,
        string fragmentsPath
    ) => _composer.ComposeClaimsFromFragments(baseClaimsNodes, fragmentsPath);

    public List<string> DiscoverFragmentFiles(string fragmentsPath) =>
        _composer.DiscoverFragmentFiles(fragmentsPath);
}
