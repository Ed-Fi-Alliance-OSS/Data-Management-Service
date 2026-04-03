// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Request-side state for a visible collection item. Emitted by C3 with
/// <see cref="Creatable"/> set to false; C4 enriches the flag.
/// </summary>
/// <param name="Address">Stable collection row address derived by C1.</param>
/// <param name="Creatable">
/// Whether a new collection item may be created. Initially false; populated by C4.
/// </param>
/// <param name="RequestJsonPath">
/// Concrete JSON path of this item in the request document (e.g. <c>$.classPeriods[0]</c>).
/// Used by C8 duplicate-collision diagnostics to report the actual request locations.
/// </param>
public sealed record VisibleRequestCollectionItem(
    CollectionRowAddress Address,
    bool Creatable,
    string RequestJsonPath
);
