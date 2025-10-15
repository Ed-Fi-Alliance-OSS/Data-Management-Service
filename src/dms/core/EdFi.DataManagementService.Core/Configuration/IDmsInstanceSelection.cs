// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Provides the selected DMS instance for the current request
/// This is a scoped service that is populated by middleware and consumed by repositories
/// </summary>
public interface IDmsInstanceSelection
{
    /// <summary>
    /// Sets the selected DMS instance for the current request
    /// Called by ResolveDmsInstanceMiddleware
    /// </summary>
    void SetSelectedDmsInstance(DmsInstance dmsInstance);

    /// <summary>
    /// Gets the selected DMS instance for the current request
    /// Called by repository factories
    /// </summary>
    DmsInstance GetSelectedDmsInstance();

    /// <summary>
    /// Indicates whether the DMS instance has been set for this request
    /// </summary>
    bool IsSet { get; }
}
