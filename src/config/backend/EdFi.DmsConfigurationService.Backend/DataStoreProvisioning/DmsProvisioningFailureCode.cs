// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// Every failure code the provisioner can raise (spec section 8). The category of each code, and the
/// fixed summary text, live in <see cref="DmsProvisioningFailure"/>.
/// </summary>
public enum DmsProvisioningFailureCode
{
    // NotReady (readiness)
    FeatureDisabled,
    StartupValidationFailed,
    AdapterUnavailable,

    // InvalidRequest (boundary)
    TemplateNotAllowlisted,
    InvalidTargetName,
    EmptySourceIdentity,
    ArtifactPinMissing,

    // Permanent
    ProtectedTarget,
    AdminPrivilegeMissing,
    RoleStateUnsafe,
    EngineIncompatible,
    StagingNotVisible,
    ServerRejected,

    // Transient
    SecretResolutionFailed,
    ServerUnavailable,
    LockTimeout,
    LockUnavailable,
    TimedOut,
    StagingFailed,
    StagingNameCollision,
    Unknown,

    // Transient before any mutation; OperatorInspectionRequired after a fence or an unknown outcome
    ExclusiveAccessNotEstablished,

    // Transient when no selected-target mutation was issued on the holder, or when settlement proved the rename or
    // fence in doubt left nothing in place; OperatorInspectionRequired otherwise
    LockLost,

    // OperatorInspectionRequired (artifact)
    ArtifactMissing,
    ArtifactUntrusted,
    ArtifactTampered,
    ManifestInvalid,
    ManifestMismatch,
    BackupInvalid,
    PackageIdentityInvalid,
    ArtifactChangedSinceCreate,

    // Permanent; Transient when the outcome resolved to "rolled back" after a connection-class error
    RestoreFailed,

    // OperatorInspectionRequired (target / instance / outcome)
    ContentProfileViolation,
    TargetIncompatible,
    SecurityStateViolation,
    StagingIntegrityViolation,
    IdentityAssignmentFailed,
    TargetUnowned,
    TargetUnverifiable,
    TargetFenced,
    InstanceChanged,
    OutcomeUnknown,
    CompensationFailed,

    // Transient (lock / connection) or OperatorInspectionRequired
    DropFailed,
}
