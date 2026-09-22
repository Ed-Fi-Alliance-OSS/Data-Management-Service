// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Identity;

/// <summary>
/// The three outcomes <see cref="IdentityTenantSnapshot.CheckAsync" /> can answer for a requested
/// tenant name (design.md:565-583). Only <see cref="Absent" /> is a confirmed negative; an
/// <see cref="OperationCanceledException" /> for the caller's own request token is never converted
/// into one of these three values, it propagates instead.
/// </summary>
internal enum TenantExistenceOutcome
{
    /// <summary>The tenant is confirmed present in a successfully fetched snapshot.</summary>
    Exists,

    /// <summary>A successfully fetched snapshot does not contain the tenant.</summary>
    Absent,

    /// <summary>The existence question could not be answered (refresh failure or timeout).</summary>
    Unavailable,
}
