// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>Why the selected target is being dropped on the holder session; staging databases are dropped as cleanup instead.</summary>
public enum DmsDropReason
{
    /// <summary>The renamed target after a proven-uncommitted marker, in the same invocation.</summary>
    Compensation,

    /// <summary>An owned, fenced, exclusively held target being deleted.</summary>
    Delete,
}
