// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// The SQL Server staging routine of D-18 (6.4 step 4): copies the extracted backup into
/// <c>&lt;StagingPath&gt;/dms-provision-&lt;op&gt;.bak</c>, a name generated from the operation id alone, created with
/// <see cref="FileMode.CreateNew"/> so an existing entry is refused and never touched, with the configured Unix
/// mode applied on the handle (the create mode is subject to the umask), hashing while it copies; flushes it to
/// the device; then re-opens the file by path and hashes it again, independently of the copy. The staged file is
/// handed out only when both hashes equal the manifest's <c>artifactSha256</c>. Immutability is not claimed from a
/// share mode (advisory on Unix): it rests on the directory denying the server identity any create, unlink, or
/// rename (mode <c>2750</c>), on the file mode denying it any write, and on unique per-operation names;
/// <see cref="VerifyAsync"/> repeats the independent hash after <c>RESTORE</c> as defense in depth. A file this
/// routine created is deleted on every failure with the bounded D-19 deletion; one it did not create is left alone.
/// </summary>
public sealed class MssqlBackupStager
{
    public const string FileExtension = ".bak";

    /// <summary>The fixed <c>Diagnostic.Operation</c> of each staging failure; never a path or a value.</summary>
    public const string OpenSourceStep = "StageBackup.OpenSource";

    public const string CreateStep = "StageBackup.Create";

    public const string ApplyModeStep = "StageBackup.ApplyMode";

    public const string CopyStep = "StageBackup.Copy";

    public const string CopyHashMismatchStep = "StageBackup.CopyHashMismatch";

    public const string RehashStep = "StageBackup.Rehash";

    public const string RehashMismatchStep = "StageBackup.RehashMismatch";

    internal static readonly EventId StagedFileResidue = new(1438_40, nameof(StagedFileResidue));

    private const int BufferSize = 81920;

    private static readonly TimeSpan[] _cleanupBackoff =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(2),
    ];

    private readonly MssqlStagingSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <param name="settings">The staging directory, its server view, the file mode, and the deletion bounds.</param>
    /// <param name="timeProvider">Drives the deletion backoff and bound.</param>
    /// <param name="logger">Receives a staged file left behind on a cancelled staging, by generated name only.</param>
    public MssqlBackupStager(MssqlStagingSettings settings, TimeProvider timeProvider, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Test seam: runs with the staged file's local path after the copy is written, flushed, and closed and
    /// before the independent re-hash, the point where a writer could alter the file.
    /// </summary>
    internal Action<string>? AfterCopy { get; init; }

    /// <summary>
    /// Test seam: replaces the mode application on the new file's handle (Unix: the configured mode set on the
    /// handle; Windows: nothing), so a failure there can be produced on either platform.
    /// </summary>
    internal Action<FileStream>? ApplyModeOverride { get; init; }

    /// <summary><c>dms-provision-&lt;op&gt;.bak</c>.</summary>
    public static string FileNameFor(DmsOperationId operation) =>
        PrivateOperationDirectory.NamePrefix + operation.Value + FileExtension;

    /// <summary>The path the database server opens for <paramref name="fileName"/>, in its own convention.</summary>
    public string ServerPathOf(string fileName) =>
        MssqlServerPaths.Join(_settings.StagingServerPath, fileName);

    /// <summary>
    /// Stages <paramref name="artifact"/> for <paramref name="operation"/>. A failure is reported, not thrown, so the
    /// caller receives the residue of a file it could not delete; cancellation propagates after the deletion.
    /// </summary>
    public async Task<MssqlStagingResult> StageAsync(
        StagedArtifact artifact,
        DmsOperationId operation,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Kind != DmsArtifactKind.MssqlBackup)
        {
            throw new ArgumentException("Only a SQL Server backup artifact is staged.", nameof(artifact));
        }

        FileStream source;
        try
        {
            source = OpenForReading(artifact.FilePath);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            return new MssqlStagingResult.Failed(
                DmsProvisioningDiagnostic.From(exception, OpenSourceStep),
                null
            );
        }

        await using (source)
        {
            return await StageAsync(source, artifact.Sha256, operation, cancellationToken);
        }
    }

    /// <summary>
    /// The routine itself, shared with the visibility probe (D-21), which stages zero bytes: create, copy while
    /// hashing, flush, close, re-open, re-hash, compare both hashes with <paramref name="expectedSha256"/>.
    /// </summary>
    internal async Task<MssqlStagingResult> StageAsync(
        Stream source,
        string expectedSha256,
        DmsOperationId operation,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        string fileName = FileNameFor(operation);
        string localPath = Path.Combine(_settings.StagingPath, fileName);

        FileStream target;
        try
        {
            target = OpenNew(localPath);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            // Nothing of this operation exists: CreateNew refused the name, so the entry is not ours to touch.
            return new MssqlStagingResult.Failed(DmsProvisioningDiagnostic.From(exception, CreateStep), null);
        }

        // From here the file is this operation's own: every failure deletes it with the bounded deletion and
        // reports it as residue when that fails.
        try
        {
            (ApplyModeOverride ?? ApplyConfiguredMode)(target);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            await target.DisposeAsync();
            return await FailAsync(
                localPath,
                fileName,
                DmsProvisioningDiagnostic.From(exception, ApplyModeStep)
            );
        }

        string copiedSha256;
        try
        {
            await using (target)
            {
                copiedSha256 = await CopyHashingAsync(source, target, cancellationToken);
                target.Flush(flushToDisk: true);
            }
        }
        catch (OperationCanceledException)
        {
            await DeleteAfterCancellationAsync(localPath, fileName);
            throw;
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            return await FailAsync(localPath, fileName, DmsProvisioningDiagnostic.From(exception, CopyStep));
        }

        if (!string.Equals(copiedSha256, expectedSha256, StringComparison.Ordinal))
        {
            return await FailAsync(
                localPath,
                fileName,
                DmsProvisioningDiagnostic.ForOperation(CopyHashMismatchStep)
            );
        }

        AfterCopy?.Invoke(localPath);
        MssqlStagedBackup backup = new(fileName, localPath, ServerPathOf(fileName), expectedSha256);
        MssqlStagedFileCheck check;
        try
        {
            check = await VerifyAsync(backup, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await DeleteAfterCancellationAsync(localPath, fileName);
            throw;
        }

        return check switch
        {
            MssqlStagedFileCheck.Intact => new MssqlStagingResult.Staged(backup),
            MssqlStagedFileCheck.Unreadable unreadable => await FailAsync(
                localPath,
                fileName,
                unreadable.Diagnostic
            ),
            _ => await FailAsync(
                localPath,
                fileName,
                DmsProvisioningDiagnostic.ForOperation(RehashMismatchStep)
            ),
        };
    }

    /// <summary>
    /// Re-opens the staged file by path and hashes it independently of any earlier handle: intact when the bytes
    /// still hash to the manifest's <c>artifactSha256</c>, a mismatch when they do not (including a file replaced
    /// under the same name), unreadable when it cannot be opened or read. 6.4 step 7 calls this after
    /// <c>RESTORE</c>.
    /// </summary>
    public static async Task<MssqlStagedFileCheck> VerifyAsync(
        MssqlStagedBackup backup,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(backup);
        string actualSha256;
        try
        {
            await using FileStream stream = OpenForReading(backup.LocalPath);
            actualSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            return new MssqlStagedFileCheck.Unreadable(DmsProvisioningDiagnostic.From(exception, RehashStep));
        }

        return string.Equals(actualSha256, backup.Sha256, StringComparison.Ordinal)
            ? new MssqlStagedFileCheck.Intact()
            : new MssqlStagedFileCheck.Mismatch();
    }

    /// <summary>
    /// Deletes the staged file with <c>CleanupAttempts</c> tries and the D-19 backoff, bounded by
    /// <c>CommandTimeout</c> on its own token rather than the operation's: true when the file is gone afterwards,
    /// including when it was already absent.
    /// </summary>
    public Task<bool> DeleteAsync(MssqlStagedBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return DeleteBoundedAsync(backup.LocalPath);
    }

    private async Task<bool> DeleteBoundedAsync(string localPath)
    {
        using CancellationTokenSource bound = new(_settings.CleanupTimeout, _timeProvider);
        for (int attempt = 0; attempt < _settings.CleanupAttempts; attempt++)
        {
            if (attempt > 0)
            {
                try
                {
                    await Task.Delay(
                        _cleanupBackoff[Math.Min(attempt - 1, _cleanupBackoff.Length - 1)],
                        _timeProvider,
                        bound.Token
                    );
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (TryDelete(localPath))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<MssqlStagingResult> FailAsync(
        string localPath,
        string fileName,
        DmsProvisioningDiagnostic diagnostic
    ) =>
        new MssqlStagingResult.Failed(
            diagnostic,
            await DeleteBoundedAsync(localPath)
                ? null
                : new DmsCleanupResidue(DmsResidueKind.StagedBackup, fileName)
        );

    private async Task DeleteAfterCancellationAsync(string localPath, string fileName)
    {
        if (!await DeleteBoundedAsync(localPath))
        {
            _logger.LogError(
                StagedFileResidue,
                "DMSPROV-StagedBackupResidue: the staged backup {ResourceId} of a cancelled staging could not be deleted",
                fileName
            );
        }
    }

    /// <summary>Creates the file, refusing any existing entry; the create mode is subject to the umask.</summary>
    private FileStream OpenNew(string localPath)
    {
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 0,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = _settings.FileMode;
        }

        return new FileStream(localPath, options);
    }

    /// <summary>Sets the configured mode on the handle, so the umask cannot narrow it (Unix only).</summary>
    private void ApplyConfiguredMode(FileStream stream)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(stream.SafeFileHandle, _settings.FileMode);
        }
    }

    private static FileStream OpenForReading(string path) =>
        new(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 0,
            }
        );

    private static async Task<string> CopyHashingAsync(
        Stream source,
        Stream target,
        CancellationToken cancellationToken
    )
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize];
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool TryDelete(string localPath)
    {
        try
        {
            File.Delete(localPath);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            return false;
        }

        return !File.Exists(localPath);
    }

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;
}

/// <summary>A staged backup: the generated name, where the CMS wrote it, where the server opens it, and the hash it must keep.</summary>
public sealed record MssqlStagedBackup(string FileName, string LocalPath, string ServerPath, string Sha256);

/// <summary>What <see cref="MssqlBackupStager.StageAsync(StagedArtifact, DmsOperationId, CancellationToken)"/> produced.</summary>
public abstract record MssqlStagingResult
{
    private MssqlStagingResult() { }

    /// <summary>Both hashes equal the manifest's; the file is ready for <c>RESTORE</c>.</summary>
    public sealed record Staged(MssqlStagedBackup Backup) : MssqlStagingResult;

    /// <summary>
    /// <c>StagingFailed</c> (transient, 6.4 step 4) at the step <paramref name="Diagnostic"/> names; nothing of this
    /// operation remains unless <paramref name="Residue"/> names the file the bounded deletion could not remove.
    /// </summary>
    public sealed record Failed(DmsProvisioningDiagnostic Diagnostic, DmsCleanupResidue? Residue)
        : MssqlStagingResult;
}

/// <summary>What an independent re-hash of a staged file found.</summary>
public abstract record MssqlStagedFileCheck
{
    private MssqlStagedFileCheck() { }

    public sealed record Intact : MssqlStagedFileCheck;

    /// <summary>The bytes under the staged name no longer hash to <c>artifactSha256</c>.</summary>
    public sealed record Mismatch : MssqlStagedFileCheck;

    /// <summary>The file could not be opened or read; its diagnostic names the step and the exception type chain.</summary>
    public sealed record Unreadable(DmsProvisioningDiagnostic Diagnostic) : MssqlStagedFileCheck;
}
