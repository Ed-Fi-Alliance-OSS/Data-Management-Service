// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Configuration for the DMS-owned Identity HTTP surface, bound from the
/// <c>DataManagement:IdentityManagement</c> section.
/// </summary>
public sealed class IdentityManagementOptions
{
    public const string SectionName = "DataManagement:IdentityManagement";

    /// <summary>
    /// If true, maps the Identity HTTP surface under /identity/v2. Defaults to false so every
    /// identity route falls through to the fallback 404 until an operator opts in.
    /// </summary>
    public bool Enabled { get; set; }
}
