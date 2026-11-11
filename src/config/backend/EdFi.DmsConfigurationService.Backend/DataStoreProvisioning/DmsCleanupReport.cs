// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// What bounded in-operation cleanup (D-19) could not remove. Every result carries one; an empty
/// report means every resource the operation created is gone. Residue is removed by the documented
/// operator procedure, never by automatic sweeping.
/// </summary>
public sealed record DmsCleanupReport(IReadOnlyList<DmsCleanupResidue> Residue)
{
    public static DmsCleanupReport Clean { get; } = new([]);

    public bool HasResidue => Residue.Count > 0;
}
