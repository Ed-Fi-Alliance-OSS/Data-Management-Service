// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Classifies a compiled scope by its backend physical storage topology.
/// Separate from <see cref="RequiredSliceFamily"/> to keep "what this scope is
/// physically" distinct from "what slice is required."
/// </summary>
internal enum ScopeTopologyKind
{
    RootInlined,
    SeparateTableNonCollection,
    TopLevelBaseCollection,
    NestedOrExtensionCollection,
}
