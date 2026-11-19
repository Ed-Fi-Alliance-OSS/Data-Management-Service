// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.EducationOrganizationProjection;

/// <summary>
/// The decoded continuation state of one projection walk. The server keeps no state for a walk;
/// everything a later page needs travels in this value.
/// </summary>
/// <param name="DataStoreId">The data store the walk reads; must equal the request's.</param>
/// <param name="LastEducationOrganizationId">
/// The id of the last item already returned. The next page starts strictly after it, so any signed
/// int64 value is a valid position, including zero and negative values.
/// </param>
/// <param name="Digest">
/// The first page's <see cref="ProjectionDigest"/> as unpadded base64url
/// (<see cref="ProjectionDigest.ToCursorField"/>).
/// </param>
/// <param name="WalkIssuedAtUnixSeconds">
/// When the walk's first page was answered. Copied unchanged into every later cursor, so a walk
/// expires a fixed time after it began however many pages follow.
/// </param>
/// <param name="BindingHash">
/// The 32 lower-case hexadecimal characters of <see cref="ProjectionCursorCodec.ComputeBindingHash"/>
/// for the tenant, contract version and route qualifiers of the walk.
/// </param>
internal sealed record ProjectionCursor(
    int DataStoreId,
    long LastEducationOrganizationId,
    string Digest,
    long WalkIssuedAtUnixSeconds,
    string BindingHash
);

/// <summary>
/// Why a supplied cursor was refused. Every reason is answered with the same
/// <c>invalid-cursor</c> problem; the reason exists for logging only.
/// </summary>
internal enum ProjectionCursorRejection
{
    Malformed,
    DataStoreMismatch,
    BindingMismatch,
    Expired,
    FutureDated,
}
