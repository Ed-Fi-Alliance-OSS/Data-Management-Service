// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// Backup validation and destination control of D-11 (6.4 step 5). <see cref="ReadAsync"/> reads the staged
/// backup's <c>RESTORE HEADERONLY</c> and <c>RESTORE FILELISTONLY</c> results on a work session;
/// <see cref="Plan"/> judges them and places every file. The header must hold exactly one backup set, a full
/// database backup (<c>BackupType = 1</c>) at <c>Position = 1</c>, whose recorded database name differs, ignoring
/// case, from both the staging and the target names (hygiene only; no safety claim rests on it). The file list may
/// hold only data (<c>D</c>) and log (<c>L</c>) files, at least one of each and at most
/// <see cref="MaximumFileCount"/> in all, with logical names matching <c>^[A-Za-z0-9_]+$</c>, and a data file with
/// <c>FileId = 1</c>, the primary file. Each file is moved into the server's default data or log directory under a
/// name that carries the operation tag (D-13 instance evidence; no collision with a crash leftover):
/// <c>&lt;stage&gt;__&lt;op&gt;.mdf</c> for the primary file, <c>&lt;stage&gt;__&lt;op&gt;_&lt;logical&gt;.ndf</c> for other data
/// files, <c>&lt;stage&gt;__&lt;op&gt;_log.ldf</c> for the first log file, and
/// <c>&lt;stage&gt;__&lt;op&gt;_&lt;logical&gt;.ldf</c> for other log files. Two files that would land on the same
/// name, compared ignoring case, are refused. The restore statement never carries <c>REPLACE</c>.
/// </summary>
public static partial class MssqlRestorePlanner
{
    public const int MaximumFileCount = 16;

    public const short FullDatabaseBackupType = 1;

    public const string DataFileType = "D";

    public const string LogFileType = "L";

    /// <summary>The <c>FileId</c> of a database's primary data file.</summary>
    public const long PrimaryFileId = 1;

    /// <summary>The prefix of every refusal's <c>Diagnostic.Operation</c>; the defect name follows it.</summary>
    public const string PlanStepPrefix = DmsEngineOperations.PlanRestorePrefix;

    /// <summary><c>&lt;stage&gt;__&lt;op&gt;.mdf</c>: the file name of the primary data file of <paramref name="operation"/>'s staging database (D-11, D-13).</summary>
    public static string PrimaryFileNameFor(DmsOperationId operation) =>
        operation.StagingDatabaseName.Value + operation.PhysicalFileTag + ".mdf";

    /// <summary><c>RESTORE HEADERONLY</c> of the file at <paramref name="serverPath"/>, the server's view of the staged file.</summary>
    public static string HeaderOnlyStatement(string serverPath) =>
        $"RESTORE HEADERONLY FROM DISK = {MssqlIdentifiers.Literal(serverPath)};";

    /// <summary><c>RESTORE FILELISTONLY</c> of the file at <paramref name="serverPath"/>.</summary>
    public static string FileListOnlyStatement(string serverPath) =>
        $"RESTORE FILELISTONLY FROM DISK = {MssqlIdentifiers.Literal(serverPath)};";

    /// <summary>
    /// Reads the header and file list of the backup at <paramref name="serverPath"/>, each column by its documented
    /// name, every value as the server returns it; judging is <see cref="Plan"/>'s. Server errors propagate for the
    /// caller to classify.
    /// </summary>
    public static async Task<MssqlBackupDescription> ReadAsync(
        SqlConnection connection,
        string serverPath,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverPath);
        List<MssqlBackupHeader> headers = [];
        await using (
            SqlCommand command = CommandFor(
                HeaderOnlyStatement(serverPath),
                connection,
                commandTimeoutSeconds
            )
        )
        await using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            int backupType = reader.GetOrdinal("BackupType");
            int position = reader.GetOrdinal("Position");
            int databaseName = reader.GetOrdinal("DatabaseName");
            while (await reader.ReadAsync(cancellationToken))
            {
                headers.Add(
                    new MssqlBackupHeader(
                        await Int64OrNullAsync(reader, backupType, cancellationToken),
                        await Int64OrNullAsync(reader, position, cancellationToken),
                        await StringOrNullAsync(reader, databaseName, cancellationToken)
                    )
                );
            }
        }

        List<MssqlBackupFile> files = [];
        await using (
            SqlCommand command = CommandFor(
                FileListOnlyStatement(serverPath),
                connection,
                commandTimeoutSeconds
            )
        )
        await using (SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            int logicalName = reader.GetOrdinal("LogicalName");
            int type = reader.GetOrdinal("Type");
            int fileId = reader.GetOrdinal("FileId");
            while (await reader.ReadAsync(cancellationToken))
            {
                files.Add(
                    new MssqlBackupFile(
                        await StringOrNullAsync(reader, logicalName, cancellationToken),
                        await StringOrNullAsync(reader, type, cancellationToken),
                        await Int64OrNullAsync(reader, fileId, cancellationToken)
                    )
                );
            }
        }

        return new MssqlBackupDescription(headers, files);
    }

    /// <summary>
    /// Judges <paramref name="backup"/> for <paramref name="operation"/>'s staging database and places its files
    /// under <paramref name="defaultDataPath"/> and <paramref name="defaultLogPath"/> (the server's
    /// <c>InstanceDefaultDataPath</c> and <c>InstanceDefaultLogPath</c>). The first failing rule, in the order the
    /// class summary gives them, is the refusal.
    /// </summary>
    public static MssqlRestorePlanResult Plan(
        MssqlBackupDescription backup,
        DmsOperationId operation,
        DmsDatabaseName target,
        string defaultDataPath,
        string defaultLogPath
    )
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultDataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultLogPath);
        DmsDatabaseName staging = operation.StagingDatabaseName;

        MssqlBackupDefect? headerDefect = JudgeHeader(backup.Headers, staging, target);
        if (headerDefect is not null)
        {
            return new MssqlRestorePlanResult.Refused(headerDefect.Value);
        }

        MssqlBackupDefect? fileDefect = JudgeFiles(backup.Files);
        if (fileDefect is not null)
        {
            return new MssqlRestorePlanResult.Refused(fileDefect.Value);
        }

        string stem = staging.Value + operation.PhysicalFileTag;
        string primaryFileName = PrimaryFileNameFor(operation);
        List<MssqlFileMove> moves = [];
        bool firstLogPlaced = false;
        foreach (MssqlBackupFile file in backup.Files)
        {
            string logicalName = file.LogicalName!;
            string physicalFileName;
            string directory;
            if (file.Type == DataFileType)
            {
                directory = defaultDataPath;
                physicalFileName =
                    file.FileId == PrimaryFileId ? primaryFileName : $"{stem}_{logicalName}.ndf";
            }
            else
            {
                directory = defaultLogPath;
                physicalFileName = firstLogPlaced ? $"{stem}_{logicalName}.ldf" : $"{stem}_log.ldf";
                firstLogPlaced = true;
            }

            moves.Add(new MssqlFileMove(logicalName, MssqlServerPaths.Join(directory, physicalFileName)));
        }

        HashSet<string> destinations = new(StringComparer.OrdinalIgnoreCase);
        if (!moves.TrueForAll(move => destinations.Add(move.PhysicalPath)))
        {
            return new MssqlRestorePlanResult.Refused(MssqlBackupDefect.PhysicalNameCollision);
        }

        return new MssqlRestorePlanResult.Planned(new MssqlRestorePlan(staging, moves));
    }

    private static MssqlBackupDefect? JudgeHeader(
        IReadOnlyList<MssqlBackupHeader> headers,
        DmsDatabaseName staging,
        DmsDatabaseName target
    )
    {
        if (headers.Count != 1)
        {
            return MssqlBackupDefect.BackupSetCount;
        }

        MssqlBackupHeader header = headers[0];
        if (header.BackupType != FullDatabaseBackupType)
        {
            return MssqlBackupDefect.NotFullDatabaseBackup;
        }

        if (header.Position != 1)
        {
            return MssqlBackupDefect.PositionNotOne;
        }

        if (string.IsNullOrEmpty(header.DatabaseName))
        {
            return MssqlBackupDefect.RecordedNameMissing;
        }

        if (string.Equals(header.DatabaseName, staging.Value, StringComparison.OrdinalIgnoreCase))
        {
            return MssqlBackupDefect.RecordedNameIsStaging;
        }

        return string.Equals(header.DatabaseName, target.Value, StringComparison.OrdinalIgnoreCase)
            ? MssqlBackupDefect.RecordedNameIsTarget
            : null;
    }

    private static MssqlBackupDefect? JudgeFiles(IReadOnlyList<MssqlBackupFile> files)
    {
        if (!files.All(file => file.Type is DataFileType or LogFileType))
        {
            return MssqlBackupDefect.UnsupportedFileType;
        }

        if (files.All(file => file.Type != DataFileType))
        {
            return MssqlBackupDefect.NoDataFile;
        }

        if (files.All(file => file.Type != LogFileType))
        {
            return MssqlBackupDefect.NoLogFile;
        }

        if (files.Count > MaximumFileCount)
        {
            return MssqlBackupDefect.TooManyFiles;
        }

        if (
            !files.All(file => file.LogicalName is not null && LogicalNamePattern().IsMatch(file.LogicalName))
        )
        {
            return MssqlBackupDefect.LogicalNameInvalid;
        }

        return files.Any(file => file is { Type: DataFileType, FileId: PrimaryFileId })
            ? null
            : MssqlBackupDefect.PrimaryFileMissing;
    }

    private static SqlCommand CommandFor(string sql, SqlConnection connection, int commandTimeoutSeconds) =>
        new(sql, connection) { CommandTimeout = commandTimeoutSeconds };

    private static async Task<long?> Int64OrNullAsync(
        SqlDataReader reader,
        int ordinal,
        CancellationToken cancellationToken
    ) =>
        await reader.IsDBNullAsync(ordinal, cancellationToken)
            ? null
            : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static async Task<string?> StringOrNullAsync(
        SqlDataReader reader,
        int ordinal,
        CancellationToken cancellationToken
    ) => await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetString(ordinal);

    [GeneratedRegex(@"\A[A-Za-z0-9_]+\z")]
    private static partial Regex LogicalNamePattern();
}

/// <summary>A backup's <c>RESTORE HEADERONLY</c> rows and <c>RESTORE FILELISTONLY</c> rows, in the server's order.</summary>
public sealed record MssqlBackupDescription(
    IReadOnlyList<MssqlBackupHeader> Headers,
    IReadOnlyList<MssqlBackupFile> Files
);

/// <summary>The <c>RESTORE HEADERONLY</c> columns D-11 judges.</summary>
public sealed record MssqlBackupHeader(long? BackupType, long? Position, string? DatabaseName);

/// <summary>The <c>RESTORE FILELISTONLY</c> columns D-11 judges; the recorded physical name is never read or reused.</summary>
public sealed record MssqlBackupFile(string? LogicalName, string? Type, long? FileId);

/// <summary>One <c>MOVE N'&lt;logical&gt;' TO N'&lt;physical&gt;'</c> clause.</summary>
public sealed record MssqlFileMove(string LogicalName, string PhysicalPath);

/// <summary>The accepted backup's destinations, and the one restore statement that uses them.</summary>
public sealed record MssqlRestorePlan(DmsDatabaseName Staging, IReadOnlyList<MssqlFileMove> Moves)
{
    /// <summary>The primary data file's destination: the <c>physical_name</c> the restored database's file 1 carries (D-13).</summary>
    public string PrimaryDataFilePath =>
        Moves.Single(move => move.PhysicalPath.EndsWith(".mdf", StringComparison.Ordinal)).PhysicalPath;

    /// <summary>
    /// <c>RESTORE DATABASE [stage] FROM DISK = N'…' WITH MOVE N'…' TO N'…', …, STATS = 10;</c> with the staging name
    /// quoted and every path and logical name a Unicode literal with embedded quotes doubled; never <c>REPLACE</c>.
    /// </summary>
    public string RestoreStatement(string serverPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverPath);
        IEnumerable<string> moves = Moves.Select(move =>
            $"MOVE {MssqlIdentifiers.Literal(move.LogicalName)} TO {MssqlIdentifiers.Literal(move.PhysicalPath)}"
        );
        return $"RESTORE DATABASE {MssqlIdentifiers.Quote(Staging.Value)} FROM DISK = {MssqlIdentifiers.Literal(serverPath)} WITH {string.Join(", ", moves)}, STATS = 10;";
    }
}

/// <summary>What <see cref="MssqlRestorePlanner.Plan"/> decided.</summary>
public abstract record MssqlRestorePlanResult
{
    private MssqlRestorePlanResult() { }

    public sealed record Planned(MssqlRestorePlan Plan) : MssqlRestorePlanResult;

    /// <summary><c>BackupInvalid</c> (inspection, 6.4 step 5) for the first failing rule.</summary>
    public sealed record Refused(MssqlBackupDefect Defect) : MssqlRestorePlanResult
    {
        /// <summary><c>PlanRestore.&lt;Defect&gt;</c>; never a name or a path from the backup.</summary>
        public DmsProvisioningDiagnostic Diagnostic =>
            DmsProvisioningDiagnostic.ForOperation(MssqlRestorePlanner.PlanStepPrefix + Defect);
    }
}

/// <summary>The D-11 rule a backup broke.</summary>
public enum MssqlBackupDefect
{
    /// <summary><c>RESTORE HEADERONLY</c> did not return exactly one backup set.</summary>
    BackupSetCount,

    NotFullDatabaseBackup,

    PositionNotOne,

    RecordedNameMissing,

    RecordedNameIsStaging,

    RecordedNameIsTarget,

    /// <summary>A file of a type other than <c>D</c> or <c>L</c>, such as FILESTREAM (<c>S</c>) or full-text (<c>F</c>).</summary>
    UnsupportedFileType,

    NoDataFile,

    NoLogFile,

    TooManyFiles,

    LogicalNameInvalid,

    /// <summary>No data file has <c>FileId = 1</c>.</summary>
    PrimaryFileMissing,

    /// <summary>Two files would land on the same destination name, compared ignoring case.</summary>
    PhysicalNameCollision,
}
