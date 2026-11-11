// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The fixed <see cref="DmsProvisioningDiagnostic.Operation"/> names an engine puts on a definite
/// command failure when the orchestrator must tell the cause apart without reading a provider code or a
/// message: what ended a replay whose transaction was found rolled back, and the D-16 role that
/// exists but is not locked down, and an owned delete that found the target name empty. An engine emits
/// exactly these spellings; the orchestrator maps them to failure codes and categories.
/// </summary>
public static class DmsEngineOperations
{
    /// <summary>
    /// The replay's transaction rolled back for a reason a retry can outlive: its session ended in doubt (a
    /// connection-class error) and it was found rolled back, or the server rejected it with a condition that
    /// clears on its own, such as a deadlock or temporary resource exhaustion: <c>RestoreFailed</c>, transient.
    /// </summary>
    public const string RestoreRolledBack = "RestoreIntoStaging.RolledBack";

    /// <summary>The restore bound ended the replay and its transaction was found rolled back: <c>TimedOut</c>.</summary>
    public const string RestoreTimedOut = "RestoreIntoStaging.TimedOut";

    /// <summary>The caller's cancellation ended the replay and its transaction was found rolled back: the cancellation propagates after cleanup.</summary>
    public const string RestoreCancelled = "RestoreIntoStaging.Cancelled";

    /// <summary>
    /// A request the restore needs before it, such as the SQL Server backup header read, failed without a server
    /// verdict (connection loss, client timeout), so nothing was restored: <c>RestoreFailed</c>, transient.
    /// </summary>
    public const string RestoreNotStarted = "RestoreIntoStaging.NotStarted";

    /// <summary>
    /// The staged SQL Server backup no longer hashes to <c>artifactSha256</c> after the restore, or can no longer
    /// be read to prove it does (D-18): <c>StagingIntegrityViolation</c>.
    /// </summary>
    public const string StagedBackupChanged = "RestoreIntoStaging.StagedBackupChanged";

    /// <summary>
    /// The prefix of every SQL Server staging-routine failure (D-18), including a staged file the server
    /// cannot open: <c>StagingFailed</c>, transient. The step after the prefix names where it failed.
    /// </summary>
    public const string StageBackupPrefix = "StageBackup.";

    /// <summary>
    /// The prefix of every refusal of the SQL Server backup itself (D-11), including a backup the server
    /// refuses to read: <c>BackupInvalid</c>, inspection. The defect after the prefix names the rule.
    /// </summary>
    public const string PlanRestorePrefix = "PlanRestore.";

    /// <summary>The D-16 role exists but is not in the locked-down state the template expects: <c>RoleStateUnsafe</c>.</summary>
    public const string RoleStateUnsafe = "EnsureEnqueueOwner.RoleStateUnsafe";

    /// <summary>
    /// An owned delete found no database under the target name, before or by the drop, with
    /// <see cref="DmsCommandFailureClass.DoesNotExist"/>: the requested state already holds, <c>AlreadyAbsent</c>.
    /// </summary>
    public const string DropTargetAbsent = "DropTarget.Absent";
}
