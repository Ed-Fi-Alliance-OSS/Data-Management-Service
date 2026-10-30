// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Claims.Models;

namespace EdFi.DmsConfigurationService.Claims.Composition;

/// <summary>
/// Composition completeness is independent of CMS loading failures. CMS may continue with
/// partial policy or its base fallback; offline callers must also inspect IsComplete.
/// </summary>
public sealed record ClaimsCompositionResult(
    ClaimsLoadResult LoadResult,
    IReadOnlyList<ClaimsCompositionDiagnostic> Diagnostics
)
{
    public bool IsComplete => LoadResult.Failures.Count == 0 && !Diagnostics.Any(d => d.IsIncomplete);
}
