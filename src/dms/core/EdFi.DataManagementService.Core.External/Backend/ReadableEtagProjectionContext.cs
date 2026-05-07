// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// Minimal readable-surface inputs needed to recompute an <c>_etag</c> from the same
/// profile-projected representation a client would receive from a readable GET.
/// </summary>
/// <param name="ContentTypeDefinition">The readable profile content-type definition.</param>
/// <param name="IdentityPropertyNames">
/// Top-level identity property names that must always survive readable projection.
/// </param>
public sealed record ReadableEtagProjectionContext(
    ContentTypeDefinition ContentTypeDefinition,
    IReadOnlySet<string> IdentityPropertyNames
);
