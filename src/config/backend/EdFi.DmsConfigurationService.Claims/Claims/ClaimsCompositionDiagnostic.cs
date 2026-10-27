// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Claims.Composition;

/// <summary>Machine-readable outcomes of fragment discovery and application.</summary>
public enum ClaimsCompositionDiagnosticCode
{
    EmptyDirectory,
    ClaimSetRegistered,
    MissingDirectory,
    DirectoryDiscoveryFailed,
    RetiredFragment,
    FragmentReadFailed,
    MalformedFragment,
    NoResourceClaims,
    UnmatchedGrantTarget,
    InvalidClaimSetRegistration,
}

/// <summary>
/// Original input context, preserved for callers. Sanitize values only when rendering them.
/// SourcePath identifies the fragment file, or the directory for discovery diagnostics.
/// Empty context fields indicate that the corresponding input location is unavailable.
/// </summary>
public sealed record ClaimsCompositionDiagnostic(
    ClaimsCompositionDiagnosticCode Code,
    string SourcePath,
    string ClaimName = "",
    string ClaimSetName = "",
    string JsonPath = ""
)
{
    /// <summary>Informational outcomes do not indicate unapplied or unreadable input.</summary>
    public bool IsIncomplete =>
        Code
            is not (
                ClaimsCompositionDiagnosticCode.EmptyDirectory
                or ClaimsCompositionDiagnosticCode.ClaimSetRegistered
            );
}
