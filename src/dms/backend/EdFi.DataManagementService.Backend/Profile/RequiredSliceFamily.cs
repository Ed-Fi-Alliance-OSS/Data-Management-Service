// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Identifies the minimum landed slice required for a profiled write to proceed.
/// Ordered by landing sequence so <c>max(requiredFamilies)</c> is unambiguous.
/// Root creatability rejection is a terminal outcome, not a family.
/// </summary>
internal enum RequiredSliceFamily
{
    RootTableOnly = 0,
    SeparateTableNonCollection = 1,
    TopLevelCollection = 2,
    NestedAndExtensionCollections = 3,
}
