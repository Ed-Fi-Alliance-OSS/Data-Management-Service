// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Request-side state for a non-collection compiled scope. Emitted by C3
/// (request-side visibility classification) with <see cref="Creatable"/> set
/// to false; C4 (creatability analysis) enriches the flag.
/// </summary>
/// <param name="Address">Stable scope instance address derived by C1.</param>
/// <param name="Visibility">Visibility classification relative to the writable profile and request data.</param>
/// <param name="Creatable">
/// Whether a new scope instance may be created. Initially false; populated by C4.
/// </param>
public sealed record RequestScopeState(
    ScopeInstanceAddress Address,
    ProfileVisibilityKind Visibility,
    bool Creatable
);
