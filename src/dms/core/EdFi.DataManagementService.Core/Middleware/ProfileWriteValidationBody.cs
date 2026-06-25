// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Selects the request body that profile-aware request validation should inspect.
/// </summary>
/// <remarks>
/// DMS-1229: on relational profile writes, <see cref="RequestInfo.ParsedBody"/> intentionally
/// remains the raw submitted body (the backend relies on receiving it), and the profile-shaped
/// write surface is carried separately on <c>BackendProfileWriteContext.Request.WritableRequestBody</c>.
/// Request validators that would otherwise reject submitted data the profile hides must validate
/// against the shaped body so that hidden submitted data is accepted and ignored. This mirrors the
/// backend's "extract raw, restrict to the shaped surface at the point of use" pattern rather than
/// mutating <see cref="RequestInfo.ParsedBody"/> or <see cref="RequestInfo.DocumentInfo"/>.
/// </remarks>
internal static class ProfileWriteValidationBody
{
    /// <summary>
    /// Returns the profile-shaped writable body when a writable profile shaped the request,
    /// otherwise the raw parsed request body.
    /// </summary>
    public static JsonNode Effective(RequestInfo requestInfo) =>
        requestInfo.BackendProfileWriteContext?.Request.WritableRequestBody ?? requestInfo.ParsedBody;
}
