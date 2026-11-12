// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// The settings the SQL Server staging routine reads (D-18): the CMS-side staging directory, the same
/// directory as the database server sees it, the Unix mode of every staged file and probe, and the D-19 bounds
/// of a staged file's deletion. Every value is checked so the routine never holds one outside the documented
/// contract; the options validator reports the same rules with setting names at startup.
/// </summary>
public sealed record MssqlStagingSettings
{
    /// <summary>Group write and other write: a staged file the server identity could rewrite is refused.</summary>
    public const UnixFileMode ForbiddenFileModeBits = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    public const int MaximumCleanupAttempts = 10;

    public MssqlStagingSettings(
        string stagingPath,
        string stagingServerPath,
        UnixFileMode fileMode,
        int cleanupAttempts,
        TimeSpan cleanupTimeout
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingServerPath);
        if (!Path.IsPathFullyQualified(stagingPath))
        {
            throw new ArgumentException(
                "The staging path must be fully qualified on the CMS host.",
                nameof(stagingPath)
            );
        }

        if (!MssqlServerPaths.IsAbsolute(stagingServerPath))
        {
            throw new ArgumentException(
                "The staging server path must be absolute under the Unix or the Windows convention.",
                nameof(stagingServerPath)
            );
        }

        if ((fileMode & ForbiddenFileModeBits) != 0)
        {
            throw new ArgumentException(
                "The staged file mode must not grant group or other write.",
                nameof(fileMode)
            );
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(cleanupAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cleanupAttempts, MaximumCleanupAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cleanupTimeout, TimeSpan.Zero);
        StagingPath = stagingPath;
        StagingServerPath = stagingServerPath;
        FileMode = fileMode;
        CleanupAttempts = cleanupAttempts;
        CleanupTimeout = cleanupTimeout;
    }

    /// <summary><c>Mssql:StagingPath</c>: where the CMS writes staged files.</summary>
    public string StagingPath { get; }

    /// <summary><c>Mssql:StagingServerPath</c>: the same directory in the database server's path convention.</summary>
    public string StagingServerPath { get; }

    /// <summary><c>Mssql:StagingFileMode</c>: applied to every staged file and probe on Unix; ignored on Windows.</summary>
    public UnixFileMode FileMode { get; }

    /// <summary><c>CleanupAttempts</c>: tries to delete a staged file (D-19).</summary>
    public int CleanupAttempts { get; }

    /// <summary><c>CommandTimeout</c>: the bound of the whole deletion, on its own token (D-19).</summary>
    public TimeSpan CleanupTimeout { get; }

    /// <summary>
    /// Parses the three or four octal digits of <c>Mssql:StagingFileMode</c> (such as <c>0640</c>); anything else
    /// is a <see cref="FormatException"/> naming no value.
    /// </summary>
    public static UnixFileMode ParseFileMode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length is < 3 or > 4 || !text.All(character => character is >= '0' and <= '7'))
        {
            throw new FormatException("The staged file mode must be three or four octal digits.");
        }

        return (UnixFileMode)Convert.ToInt32(text, 8);
    }
}
