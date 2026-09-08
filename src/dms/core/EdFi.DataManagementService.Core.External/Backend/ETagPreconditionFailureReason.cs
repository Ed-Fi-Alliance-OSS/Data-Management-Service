// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>Why an ETag precondition failed, so the API can return actionable 412 Problem Details.</summary>
public enum ETagPreconditionFailureReason
{
    /// <summary>The target exists but its current etag does not match the client's specific tag.</summary>
    Concurrency,

    /// <summary>No current representation exists to satisfy the precondition (bare If-Match: * on a
    /// missing target, or If-Match on a resource that resolves to an insert).</summary>
    TargetDoesNotExist,

    /// <summary>A current representation matches the client's If-None-Match precondition.</summary>
    CurrentRepresentationMatchesIfNoneMatch,
}
