// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Fixtures;

/// <summary>
/// SQL Server loader SQL for the fixture definition. Rows are generated set-based with
/// GENERATE_SERIES, which requires SQL Server 2022+ at database compatibility level 160+, so
/// the guard query must pass before any load statement runs. The arithmetic below must mirror
/// <see cref="PerfFixtureDefinition" /> exactly. dms.Document is inserted before edfi.Student
/// because the Student stamp trigger reads the matching dms.Document row. Production triggers
/// and constraints stay enabled throughout.
/// </summary>
public static class MssqlPerfFixtureLoaderSql
{
    public const int MinimumProductMajorVersion = 16;

    public const int MinimumCompatibilityLevel = 160;

    /// <summary>
    /// Returns 1 when GENERATE_SERIES is available, 0 otherwise. A 0 must abort the load with
    /// a message naming the version and compatibility-level requirements.
    /// </summary>
    public const string GenerateSeriesGuardSql = """
        SELECT CASE
            WHEN CAST(SERVERPROPERTY('ProductMajorVersion') AS int) >= 16
                AND (SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()) >= 160
            THEN 1
            ELSE 0
        END;
        """;

    public const string ResourceKeyLookupSql = $"""
        SELECT [ResourceKeyId]
        FROM [dms].[ResourceKey]
        WHERE [ProjectName] = '{PerfFixtureDefinition.ProjectName}'
            AND [ResourceName] = '{PerfFixtureDefinition.ResourceName}';
        """;

    public const string DocumentInsertSql = $"""
        SET IDENTITY_INSERT [dms].[Document] ON;

        INSERT INTO [dms].[Document] ([DocumentId], [DocumentUuid], [ResourceKeyId])
        SELECT
            ((s.value - 1) / 9) * 10 + ((s.value - 1) % 9) + 2,
            CONVERT(
                uniqueidentifier,
                '{PerfFixtureDefinition.DocumentUuidPrefix}'
                    + RIGHT(REPLICATE('0', 12) + LOWER(FORMAT(s.value, 'x')), 12)
            ),
            @{PerfFixtureLoaderParameters.ResourceKeyId}
        FROM GENERATE_SERIES(@{PerfFixtureLoaderParameters.FromOrdinal}, @{PerfFixtureLoaderParameters.ToOrdinal}) AS s;

        SET IDENTITY_INSERT [dms].[Document] OFF;
        """;

    public const string StudentInsertSql = $"""
        INSERT INTO [edfi].[Student] ([DocumentId], [StudentUniqueId], [FirstName], [LastSurname], [BirthDate])
        SELECT
            ((s.value - 1) / 9) * 10 + ((s.value - 1) % 9) + 2,
            'perf-' + RIGHT(REPLICATE('0', 9) + CAST(s.value AS varchar(19)), 9),
            '{PerfFixtureDefinition.FirstName}',
            '{PerfFixtureDefinition.LastSurname}',
            '{PerfFixtureDefinition.BirthDateIso}'
        FROM GENERATE_SERIES(@{PerfFixtureLoaderParameters.FromOrdinal}, @{PerfFixtureLoaderParameters.ToOrdinal}) AS s;
        """;

    /// <summary>
    /// RESEED takes the current seed, so the next identity value is MaxDocumentId + 1 —
    /// the same next value the PostgreSQL RESTART WITH form produces.
    /// </summary>
    public static string ReseedSql(PerfFixtureDefinition definition) =>
        $"DBCC CHECKIDENT ('[dms].[Document]', RESEED, {definition.MaxDocumentId});";

    public static readonly IReadOnlyList<string> StatisticsRefreshSqls =
    [
        "UPDATE STATISTICS [dms].[Document];",
        "UPDATE STATISTICS [edfi].[Student];",
    ];

    /// <summary>
    /// Expected document counts assume the freshly provisioned baseline database holds no
    /// dms.Document rows before the load.
    /// </summary>
    public static IReadOnlyList<PerfVerificationQuery> VerificationQueries(
        PerfFixtureDefinition definition
    ) =>
        [
            new("student-row-count", "SELECT COUNT(*) FROM [edfi].[Student];", definition.RowCount),
            new("document-row-count", "SELECT COUNT(*) FROM [dms].[Document];", definition.RowCount),
            new(
                "document-student-pairing",
                """
                SELECT COUNT(*)
                FROM [edfi].[Student] s
                INNER JOIN [dms].[Document] d ON d.[DocumentId] = s.[DocumentId];
                """,
                definition.RowCount
            ),
            new(
                "min-document-id",
                "SELECT MIN([DocumentId]) FROM [dms].[Document];",
                PerfFixtureDefinition.MinDocumentId
            ),
            new(
                "max-document-id",
                "SELECT MAX([DocumentId]) FROM [dms].[Document];",
                definition.MaxDocumentId
            ),
            new(
                "document-id-sum",
                "SELECT SUM([DocumentId]) FROM [dms].[Document];",
                definition.DocumentIdSum()
            ),
        ];
}
