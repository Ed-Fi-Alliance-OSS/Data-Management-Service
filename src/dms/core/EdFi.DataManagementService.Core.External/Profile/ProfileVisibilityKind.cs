// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Classifies the visibility of a compiled scope relative to a writable profile
/// and the data present in a JSON document (request or stored).
/// </summary>
public enum ProfileVisibilityKind
{
    /// <summary>
    /// Scope is included in the writable profile and the document provides data for it.
    /// </summary>
    VisiblePresent,

    /// <summary>
    /// Scope is included in the writable profile but the document does not provide data
    /// for it. Backend must distinguish this from Hidden to clear stored data correctly.
    /// </summary>
    VisibleAbsent,

    /// <summary>
    /// Scope is excluded from the writable profile. Backend must preserve stored data.
    /// </summary>
    Hidden,
}
