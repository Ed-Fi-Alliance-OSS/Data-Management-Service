// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// The staging visibility probe of D-21 (7.3 item 6). A zero-byte probe is written with the real staging routine
/// (<see cref="MssqlBackupStager"/>, under a fresh operation id, so the CMS's create, read, and re-hash are the ones a
/// create performs), then the server is asked for its header through the configured server path, with documented
/// commands only. The server's error numbers decide: <c>3201</c> (the device cannot be opened, also for a file the
/// server identity may not read) means not visible; <c>3241</c>, <c>3242</c>, or <c>3254</c> (the file opened and is
/// not a backup; SQL Server 2025 reports an empty file as <c>3254</c>) means visible; anything else, including a
/// header returned for a file that cannot hold one, is inconclusive. The probe file is deleted on every outcome with
/// the routine's bounded deletion, and a probe that cannot be deleted is reported. Readiness (Step 4.9) maps the
/// verdict to <c>StagingNotVisible</c> or <c>NotReady</c>.
/// </summary>
public sealed class MssqlStagingProbe
{
    /// <summary>The fixed <c>Diagnostic.Operation</c> of the server's answer.</summary>
    public const string HeaderStep = "StagingProbe.Header";

    /// <summary>SHA-256 of zero bytes.</summary>
    internal const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private readonly MssqlBackupStager _stager;
    private readonly MssqlAdminConnection _adminConnection;

    public MssqlStagingProbe(MssqlBackupStager stager, MssqlAdminConnection adminConnection)
    {
        ArgumentNullException.ThrowIfNull(stager);
        ArgumentNullException.ThrowIfNull(adminConnection);
        _stager = stager;
        _adminConnection = adminConnection;
    }

    /// <summary>
    /// Writes, probes, and deletes one probe file. A failure to reach the server propagates for readiness to
    /// classify, after the probe file is deleted; so does cancellation.
    /// </summary>
    public async Task<MssqlStagingProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        DmsOperationId operation = DmsOperationId.New();
        MssqlStagingResult staged = await _stager.StageAsync(
            Stream.Null,
            EmptySha256,
            operation,
            cancellationToken
        );
        if (staged is MssqlStagingResult.Failed failed)
        {
            return new MssqlStagingProbeResult(
                MssqlStagingVisibility.NotWritable,
                failed.Diagnostic,
                ProbeDeleted: failed.Residue is null
            );
        }

        MssqlStagedBackup probe = ((MssqlStagingResult.Staged)staged).Backup;
        MssqlStagingVisibility visibility;
        DmsProvisioningDiagnostic? diagnostic;
        try
        {
            (visibility, diagnostic) = await AskServerAsync(probe.ServerPath, cancellationToken);
        }
        catch
        {
            await _stager.DeleteAsync(probe);
            throw;
        }

        bool deleted = await _stager.DeleteAsync(probe);
        return new MssqlStagingProbeResult(visibility, diagnostic, deleted);
    }

    /// <summary>
    /// The D-21 verdict for the error numbers of a failed <c>RESTORE HEADERONLY</c>: not visible when any is
    /// <c>3201</c>; visible when any is <c>3241</c>, <c>3242</c>, or <c>3254</c>; otherwise inconclusive.
    /// </summary>
    public static MssqlStagingVisibility Classify(IReadOnlyCollection<int> errorNumbers)
    {
        ArgumentNullException.ThrowIfNull(errorNumbers);
        if (errorNumbers.Contains(MssqlErrorClassifier.BackupDeviceCannotOpen))
        {
            return MssqlStagingVisibility.NotVisible;
        }

        return errorNumbers.Any(number =>
            number
                is MssqlErrorClassifier.BackupMediaFamilyMalformed
                    or MssqlErrorClassifier.BackupNotAValidBackupSet
                    or MssqlErrorClassifier.BackupVolumeEmpty
        )
            ? MssqlStagingVisibility.Visible
            : MssqlStagingVisibility.Inconclusive;
    }

    private SqlCommand HeaderCommand(string serverPath, SqlConnection connection) =>
        new(MssqlRestorePlanner.HeaderOnlyStatement(serverPath), connection)
        {
            CommandTimeout = _adminConnection.CommandTimeoutSeconds,
        };

    private async Task<(MssqlStagingVisibility, DmsProvisioningDiagnostic?)> AskServerAsync(
        string serverPath,
        CancellationToken cancellationToken
    )
    {
        await using SqlConnection connection = await _adminConnection.OpenMaintenanceAsync(cancellationToken);
        await using SqlCommand command = HeaderCommand(serverPath, connection);
        try
        {
            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                // Drained only: any header row is the inconclusive answer below.
            }
        }
        catch (SqlException exception) when (!MssqlErrorClassifier.IsConnectionFailure(exception))
        {
            MssqlStagingVisibility visibility = Classify(MssqlErrorClassifier.ErrorNumbersOf(exception));
            return (
                visibility,
                visibility == MssqlStagingVisibility.Visible
                    ? null
                    : MssqlErrorClassifier.Diagnostic(exception, HeaderStep)
            );
        }

        // A header for a zero-byte file proves nothing about the path the server opened.
        return (MssqlStagingVisibility.Inconclusive, DmsProvisioningDiagnostic.ForOperation(HeaderStep));
    }
}

/// <summary>The probe's verdict, the diagnostic of any verdict but visible, and whether the probe file is gone.</summary>
public sealed record MssqlStagingProbeResult(
    MssqlStagingVisibility Visibility,
    DmsProvisioningDiagnostic? Diagnostic,
    bool ProbeDeleted
);

public enum MssqlStagingVisibility
{
    /// <summary>The server opened the probe through the staging server path.</summary>
    Visible,

    /// <summary>The server could not open the probe (<c>3201</c>): <c>StagingNotVisible</c>.</summary>
    NotVisible,

    /// <summary>The server answered with something D-21 does not name.</summary>
    Inconclusive,

    /// <summary>The CMS could not create, write, or re-read the probe with the staging routine.</summary>
    NotWritable,
}
