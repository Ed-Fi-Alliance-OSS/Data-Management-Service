// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>The server facts readiness judges (7.3 item 4), read in one statement on a maintenance connection.</summary>
/// <param name="ProductMajorVersion"><c>SERVERPROPERTY('ProductMajorVersion')</c>.</param>
/// <param name="DefaultDataPath"><c>SERVERPROPERTY('InstanceDefaultDataPath')</c>, or null.</param>
/// <param name="DefaultLogPath"><c>SERVERPROPERTY('InstanceDefaultLogPath')</c>, or null.</param>
/// <param name="IsSysadmin"><c>IS_SRVROLEMEMBER('sysadmin')</c>.</param>
/// <param name="IsDbcreator"><c>IS_SRVROLEMEMBER('dbcreator')</c>.</param>
/// <param name="HasAlterAnyDatabase"><c>ALTER ANY DATABASE</c>, implied permissions counted.</param>
/// <param name="HasAlterAnyConnection"><c>ALTER ANY CONNECTION</c> (<c>KILL</c>), implied permissions counted.</param>
/// <param name="SeesAllSessions"><c>VIEW SERVER PERFORMANCE STATE</c> where the server knows it, else <c>VIEW SERVER STATE</c>.</param>
/// <param name="SeesFileCatalog">Whether <c>sys.master_files</c> shows <c>master</c>'s primary file to this login.</param>
internal sealed record MssqlEnvironmentFacts(
    int ProductMajorVersion,
    string? DefaultDataPath,
    string? DefaultLogPath,
    bool IsSysadmin,
    bool IsDbcreator,
    bool HasAlterAnyDatabase,
    bool HasAlterAnyConnection,
    bool SeesAllSessions,
    bool SeesFileCatalog
);

/// <summary>
/// The SQL Server environmental validation of 7.3 items 4 and 6 (D-18, D-21, D-22): the read and the rules that
/// turn its facts, the staging directory, and the visibility probe into readiness reasons. Every reason carries a
/// fixed probe name and a diagnostic with a fixed operation; nothing read from the server or the file system other
/// than an error number and an exception type chain reaches a reason.
/// </summary>
internal static class MssqlEnvironment
{
    /// <summary>Connecting to <c>master</c>, or reading the facts there.</summary>
    public const string TargetServerProbe = "TargetServer";

    /// <summary><c>ProductMajorVersion</c> at least <see cref="MinimumProductMajorVersion"/>.</summary>
    public const string ServerVersionProbe = "ServerVersion";

    /// <summary>6.4 step 2: the restore's <c>MOVE</c> destinations are the instance default data and log directories.</summary>
    public const string DefaultDataPathProbe = "DefaultDataPath";

    public const string DefaultLogPathProbe = "DefaultLogPath";

    /// <summary><c>RESTORE</c> into a new database: <c>dbcreator</c> or <c>sysadmin</c>.</summary>
    public const string CreateDatabasePrivilegeProbe = "CreateDatabasePrivilege";

    /// <summary>Rename, fence (<c>SINGLE_USER</c>), marker, and drop of databases the admin does not own: <c>ALTER ANY DATABASE</c>.</summary>
    public const string AlterAnyDatabasePrivilegeProbe = "AlterAnyDatabasePrivilege";

    /// <summary>D-22 termination: <c>ALTER ANY CONNECTION</c> (<c>KILL</c>).</summary>
    public const string AlterAnyConnectionPrivilegeProbe = "AlterAnyConnectionPrivilege";

    /// <summary>
    /// D-22 identity: without <c>VIEW SERVER STATE</c> (<c>VIEW SERVER PERFORMANCE STATE</c> from SQL Server 2022)
    /// <c>sys.dm_exec_sessions</c> shows only the caller's own session, so no session can be identified or proven
    /// gone (spec row 5am).
    /// </summary>
    public const string ViewServerStatePrivilegeProbe = "ViewServerStatePrivilege";

    /// <summary>
    /// The instance identity's primary file comes from <c>sys.master_files</c>, which a login without
    /// <c>CREATE DATABASE</c>, <c>ALTER ANY DATABASE</c>, or <c>VIEW ANY DEFINITION</c> cannot see; an opened session
    /// with no file evidence is refused as a changed instance (spec row 5ah), so readiness requires it. Observed,
    /// not inferred from the permissions above.
    /// </summary>
    public const string FileCatalogVisibilityProbe = "FileCatalogVisibility";

    /// <summary>7.3 item 6: the CMS-side staging directory exists, is owned by the CMS user, and grants no group or other write (D-18).</summary>
    public const string StagingDirectoryProbe = "StagingDirectory";

    /// <summary>7.3 item 6 and D-21: the real staging routine writes, re-reads, and deletes a probe the server can open.</summary>
    public const string StagingVisibilityProbe = "StagingVisibility";

    /// <summary>7.3 item 7: the probe lock is acquired, holds through one heartbeat, and is released.</summary>
    public const string TargetLockProbe = "TargetLock";

    /// <summary>
    /// The lowest <c>ProductMajorVersion</c> accepted: SQL Server 2022. Every operation still requires the live
    /// major to reach the manifest's (6.4 step 2); this floor only refuses a server the adapter was never meant for.
    /// </summary>
    public const int MinimumProductMajorVersion = 16;

    public const string ConnectOperation = TargetServerProbe + ".Connect";
    public const string ReadFactsOperation = TargetServerProbe + ".ReadFacts";
    public const string CreateDatabaseRoleOperation = CreateDatabasePrivilegeProbe + ".DbcreatorOrSysadmin";
    public const string StagingDirectoryMissingOperation = StagingDirectoryProbe + ".Missing";
    public const string StagingDirectoryWritableOperation = StagingDirectoryProbe + ".GroupOrOtherWritable";
    public const string StagingDirectoryModeOperation = StagingDirectoryProbe + ".ReadMode";
    public const string StagingDirectoryNotOwnedOperation = StagingDirectoryProbe + ".NotOwnedByProcess";
    public const string StagingDirectoryOwnerOperation = StagingDirectoryProbe + ".ReadOwner";
    public const string StagingProbeDeleteOperation = StagingVisibilityProbe + ".Delete";
    public const string StagingProbeServerOperation = StagingVisibilityProbe + ".Server";
    public const string LockHeartbeatOperation = TargetLockProbe + ".Heartbeat";

    /// <summary>The facts in one statement; every privilege is read with implied permissions counted, so <c>sysadmin</c> holds them all.</summary>
    private const string FactsQuery =
        "SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion')), "
        + "CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultDataPath')), "
        + "CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultLogPath')), "
        + "CONVERT(int, COALESCE(IS_SRVROLEMEMBER(N'sysadmin'), 0)), "
        + "CONVERT(int, COALESCE(IS_SRVROLEMEMBER(N'dbcreator'), 0)), "
        + "CONVERT(int, COALESCE(HAS_PERMS_BY_NAME(NULL, NULL, N'ALTER ANY DATABASE'), 0)), "
        + "CONVERT(int, COALESCE(HAS_PERMS_BY_NAME(NULL, NULL, N'ALTER ANY CONNECTION'), 0)), "
        + $"COALESCE({MssqlSessionQueries.SessionsVisible}, 0), "
        + "CASE WHEN EXISTS (SELECT 1 FROM sys.master_files f WHERE f.database_id = 1 AND f.file_id = 1) THEN 1 ELSE 0 END;";

    /// <summary>The operation privileges, in the order they are reported, each read independently of the others.</summary>
    private static readonly (
        string Probe,
        string Operation,
        Func<MssqlEnvironmentFacts, bool> Holds
    )[] _privilegeRules =
    [
        (
            CreateDatabasePrivilegeProbe,
            CreateDatabaseRoleOperation,
            facts => facts.IsSysadmin || facts.IsDbcreator
        ),
        (AlterAnyDatabasePrivilegeProbe, AlterAnyDatabasePrivilegeProbe, facts => facts.HasAlterAnyDatabase),
        (
            AlterAnyConnectionPrivilegeProbe,
            AlterAnyConnectionPrivilegeProbe,
            facts => facts.HasAlterAnyConnection
        ),
        (ViewServerStatePrivilegeProbe, ViewServerStatePrivilegeProbe, facts => facts.SeesAllSessions),
        (FileCatalogVisibilityProbe, FileCatalogVisibilityProbe, facts => facts.SeesFileCatalog),
    ];

    /// <summary>Reads the facts on an open maintenance connection.</summary>
    public static async Task<MssqlEnvironmentFacts> ReadFactsAsync(
        SqlConnection connection,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = new(FactsQuery, connection);
        command.CommandTimeout = commandTimeoutSeconds;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The server environment query returned no row.");
        }

        return new MssqlEnvironmentFacts(
            reader.GetInt32(0),
            await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
            await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
            reader.GetInt32(3) == 1,
            reader.GetInt32(4) == 1,
            reader.GetInt32(5) == 1,
            reader.GetInt32(6) == 1,
            reader.GetInt32(7) == 1,
            reader.GetInt32(8) == 1
        );
    }

    /// <summary>
    /// The server rules, every failing one reported: the version floor and a missing default directory as
    /// <c>EngineIncompatible</c>; each missing privilege as <c>AdminPrivilegeMissing</c>, so a deployment sees all
    /// of them at once.
    /// </summary>
    public static IReadOnlyList<DmsReadinessReason> Evaluate(
        MssqlEnvironmentFacts facts,
        int minimumProductMajorVersion = MinimumProductMajorVersion
    )
    {
        ArgumentNullException.ThrowIfNull(facts);
        List<DmsReadinessReason> reasons = [];
        if (facts.ProductMajorVersion < minimumProductMajorVersion)
        {
            reasons.Add(Reason(DmsProvisioningFailureCode.EngineIncompatible, ServerVersionProbe));
        }

        if (string.IsNullOrEmpty(facts.DefaultDataPath))
        {
            reasons.Add(Reason(DmsProvisioningFailureCode.EngineIncompatible, DefaultDataPathProbe));
        }

        if (string.IsNullOrEmpty(facts.DefaultLogPath))
        {
            reasons.Add(Reason(DmsProvisioningFailureCode.EngineIncompatible, DefaultLogPathProbe));
        }

        reasons.AddRange(
            _privilegeRules
                .Where(rule => !rule.Holds(facts))
                .Select(rule =>
                    Reason(DmsProvisioningFailureCode.AdminPrivilegeMissing, rule.Probe, rule.Operation)
                )
        );
        return reasons;
    }

    /// <summary>
    /// D-18: a missing directory, one whose mode grants group or other write, or one another user owns is not
    /// ready. The owner matters as much as the mode: the owner of a <c>2750</c> directory may unlink or replace
    /// the staged backups in it, so a directory owned by the SQL Server identity passes the mode rule and the
    /// visibility probe (a root CMS can still write there) while giving the reader the writer's power. Mode and
    /// owner are read on Unix only (<paramref name="mode"/> and <paramref name="ownedByProcess"/> null on Windows,
    /// where the share and NTFS permissions of 7.4 are the operator's and are not inspected).
    /// </summary>
    public static DmsReadinessReason? EvaluateStagingDirectory(
        bool exists,
        UnixFileMode? mode,
        bool? ownedByProcess
    )
    {
        if (!exists)
        {
            return Reason(
                DmsProvisioningFailureCode.StartupValidationFailed,
                StagingDirectoryProbe,
                StagingDirectoryMissingOperation
            );
        }

        if (mode is { } unixMode && (unixMode & MssqlStagingSettings.ForbiddenFileModeBits) != 0)
        {
            return Reason(
                DmsProvisioningFailureCode.StartupValidationFailed,
                StagingDirectoryProbe,
                StagingDirectoryWritableOperation
            );
        }

        return ownedByProcess is false
            ? Reason(
                DmsProvisioningFailureCode.StartupValidationFailed,
                StagingDirectoryProbe,
                StagingDirectoryNotOwnedOperation
            )
            : null;
    }

    /// <summary>
    /// D-21: not visible is <c>StagingNotVisible</c>; a probe the routine could not create, write, or re-read is
    /// <c>StagingFailed</c>; an inconclusive answer is <c>StartupValidationFailed</c>; and a probe left behind is
    /// <c>StagingFailed</c> too, because a create must be able to delete what it stages. Each with the probe's own
    /// diagnostic where it has one.
    /// </summary>
    public static IReadOnlyList<DmsReadinessReason> EvaluateStagingProbe(MssqlStagingProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        List<DmsReadinessReason> reasons = [];
        DmsProvisioningFailureCode? code = result.Visibility switch
        {
            MssqlStagingVisibility.Visible => null,
            MssqlStagingVisibility.NotVisible => DmsProvisioningFailureCode.StagingNotVisible,
            MssqlStagingVisibility.NotWritable => DmsProvisioningFailureCode.StagingFailed,
            MssqlStagingVisibility.Inconclusive => DmsProvisioningFailureCode.StartupValidationFailed,
            _ => throw new ArgumentOutOfRangeException(nameof(result), "Unknown staging visibility."),
        };
        if (code is { } failure)
        {
            reasons.Add(
                new DmsReadinessReason(
                    failure,
                    StagingVisibilityProbe,
                    result.Diagnostic ?? DmsProvisioningDiagnostic.ForOperation(StagingVisibilityProbe)
                )
            );
        }

        if (!result.ProbeDeleted)
        {
            reasons.Add(
                Reason(
                    DmsProvisioningFailureCode.StagingFailed,
                    StagingVisibilityProbe,
                    StagingProbeDeleteOperation
                )
            );
        }

        return reasons;
    }

    private static DmsReadinessReason Reason(
        DmsProvisioningFailureCode code,
        string probe,
        string? operation = null
    ) => new(code, probe, DmsProvisioningDiagnostic.ForOperation(operation ?? probe));
}
