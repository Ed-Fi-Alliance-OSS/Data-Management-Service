// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>How DMS-1439 classifies a failure for retry (spec section 8).</summary>
public enum DmsProvisioningFailureCategory
{
    /// <summary>Boundary rejection: template, name, empty identity, missing pin. Terminal.</summary>
    InvalidRequest,

    /// <summary>The feature is disabled or readiness failed. Terminal until restart.</summary>
    NotReady,

    /// <summary>Safe to retry: no target was left behind, or the outcome was resolved.</summary>
    Transient,

    /// <summary>Deterministic with the same inputs; no target was left behind. Terminal.</summary>
    Permanent,

    /// <summary>A target, artifact, or instance state that must not be resolved automatically. Terminal.</summary>
    OperatorInspectionRequired,
}
