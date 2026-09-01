// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Frontend;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Reads the request header that asks for a point-in-time snapshot rather than current data.
/// </summary>
internal static class UseSnapshotHeader
{
    /// <summary>
    /// The header name. FrontendRequest.Headers is case-insensitive, so the casing here is only
    /// what appears in documentation and logs.
    /// </summary>
    public const string Name = "Use-Snapshot";

    /// <summary>
    /// Whether this request asks for a snapshot. Only a value that parses as boolean true does;
    /// an absent header, an unparseable value, and an explicit false are all "no".
    /// </summary>
    /// <remarks>
    /// bool.TryParse is already case-insensitive and already ignores surrounding whitespace, so
    /// "TRUE" and " true " ask for a snapshot without any normalization here. Anything else is a
    /// request for current data rather than an error: the header is an opt-in, and rejecting a
    /// malformed value would make a client that sends "yes" fail where a client that sends nothing
    /// succeeds. The header is deliberately absent from the frontend's preserved-when-blank list,
    /// so a blank value never reaches Core at all.
    /// </remarks>
    public static bool TryReadRequested(FrontendRequest frontendRequest)
    {
        ArgumentNullException.ThrowIfNull(frontendRequest);

        return frontendRequest.Headers.TryGetValue(Name, out string? value)
            && bool.TryParse(value, out bool parsed)
            && parsed;
    }
}
