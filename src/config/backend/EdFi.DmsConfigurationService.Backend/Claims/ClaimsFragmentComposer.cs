// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.ExceptionServices;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using Microsoft.Extensions.Logging;
using SharedClaimsFragmentComposer = EdFi.DmsConfigurationService.Claims.Composition.ClaimsFragmentComposer;

namespace EdFi.DmsConfigurationService.Backend.Claims;

/// <summary>
/// CMS adapter for shared fragment composition. Forward the CMS logger to preserve its
/// category and return the existing loading contract so provider fallback remains unchanged.
/// </summary>
public class ClaimsFragmentComposer : IClaimsFragmentComposer
{
    private readonly SharedClaimsFragmentComposer _composer;

    public ClaimsFragmentComposer(ILogger<ClaimsFragmentComposer> logger)
        : this(new SharedClaimsFragmentComposer(logger)) { }

    internal ClaimsFragmentComposer(SharedClaimsFragmentComposer composer) => _composer = composer;

    public ClaimsLoadResult ComposeClaimsFromFragments(ClaimsDocument baseClaimsNodes, string fragmentsPath)
    {
        ClaimsLoadResult result = _composer
            .ComposeClaimsFromFragments(baseClaimsNodes, fragmentsPath)
            .LoadResult;
        // Read access errors historically escape composition and CMS loading. The shared result
        // exposes them to offline callers without changing that CMS behavior.
        foreach (ClaimsFailure failure in result.Failures)
        {
            if (failure.Exception is UnauthorizedAccessException exception)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }
        }
        return result;
    }

    public List<string> DiscoverFragmentFiles(string fragmentsPath) =>
        _composer.DiscoverFragmentFiles(fragmentsPath);
}
