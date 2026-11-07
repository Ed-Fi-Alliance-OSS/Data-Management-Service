// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The readiness gate (D-2, AC 18). Startup sets it once from static and environmental validation; it
/// never moves from <c>NotReady</c> back to <c>Ready</c> without a restart.
/// </summary>
public interface IDmsDataStoreProvisionerReadiness
{
    DmsProvisionerReadiness Current { get; }
}
