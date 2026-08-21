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

    public static string DescriptorResourceKeyLookupSql(string resourceName) =>
        $"""
            SELECT [ResourceKeyId]
            FROM [dms].[ResourceKey]
            WHERE [ProjectName] = '{PerfFixtureDefinition.ProjectName}'
                AND [ResourceName] = '{resourceName}';
            """;

    public const string DescriptorDocumentInsertSql = $"""
        SET IDENTITY_INSERT [dms].[Document] ON;

        INSERT INTO [dms].[Document] ([DocumentId], [DocumentUuid], [ResourceKeyId])
        VALUES (@{PerfFixtureLoaderParameters.DescriptorDocumentId}, @{PerfFixtureLoaderParameters.DescriptorDocumentUuid}, @{PerfFixtureLoaderParameters.ResourceKeyId});

        SET IDENTITY_INSERT [dms].[Document] OFF;
        """;

    /// <summary>
    /// Mirrors the production descriptor write: Uri is namespace#codeValue, Discriminator is
    /// the resource name, and ShortDescription echoes the code value. ContentVersion is
    /// stamped by the production trigger.
    /// </summary>
    public static string DescriptorInsertSql(string resourceName) =>
        $"""
            INSERT INTO [dms].[Descriptor] ([DocumentId], [ResourceKeyId], [Namespace], [CodeValue], [ShortDescription], [Discriminator], [Uri])
            VALUES (
                @{PerfFixtureLoaderParameters.DescriptorDocumentId},
                @{PerfFixtureLoaderParameters.ResourceKeyId},
                '{PerfFixtureDefinition.DescriptorNamespaceFor(resourceName)}',
                '{PerfFixtureDefinition.DescriptorCodeValue}',
                '{PerfFixtureDefinition.DescriptorCodeValue}',
                '{resourceName}',
                '{PerfFixtureDefinition.DescriptorUriFor(resourceName)}');
            """;

    /// <summary>
    /// The production descriptor write inserts one referential-identity row per descriptor;
    /// reference validation resolves descriptor URIs against it.
    /// </summary>
    public const string DescriptorReferentialIdentityInsertSql = $"""
        INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
        VALUES (@{PerfFixtureLoaderParameters.DescriptorReferentialId}, @{PerfFixtureLoaderParameters.DescriptorDocumentId}, @{PerfFixtureLoaderParameters.ResourceKeyId});
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
        INSERT INTO [edfi].[Student] ([DocumentId], [StudentUniqueId], [FirstName], [LastSurname], [BirthDate], [BirthSexDescriptor_DescriptorId])
        SELECT
            ((s.value - 1) / 9) * 10 + ((s.value - 1) % 9) + 2,
            'perf-' + RIGHT(REPLICATE('0', 9) + CAST(s.value AS varchar(19)), 9),
            '{PerfFixtureDefinition.FirstName}',
            '{PerfFixtureDefinition.LastSurname}',
            '{PerfFixtureDefinition.BirthDateIso}',
            @{PerfFixtureLoaderParameters.BirthSexDescriptorId}
        FROM GENERATE_SERIES(@{PerfFixtureLoaderParameters.FromOrdinal}, @{PerfFixtureLoaderParameters.ToOrdinal}) AS s;
        """;

    /// <summary>
    /// One row per student in each child collection table, mirroring what a production POST
    /// of the control payload writes: Ordinal 0 for the single item, CollectionItemId from
    /// the shared sequence default, and only the payload-backed columns non-null.
    /// </summary>
    public static readonly IReadOnlyList<string> ChildCollectionInsertSqls =
    [
        $"""
            INSERT INTO [edfi].[StudentIdentificationDocument] ([Ordinal], [Student_DocumentId], [IdentificationDocumentUseDescriptor_DescriptorId], [PersonalInformationVerificationDescriptor_DescriptorId])
            SELECT
                0,
                ((s.value - 1) / 9) * 10 + ((s.value - 1) % 9) + 2,
                @{PerfFixtureLoaderParameters.IdentificationDocumentUseDescriptorId},
                @{PerfFixtureLoaderParameters.PersonalInformationVerificationDescriptorId}
            FROM GENERATE_SERIES(@{PerfFixtureLoaderParameters.FromOrdinal}, @{PerfFixtureLoaderParameters.ToOrdinal}) AS s;
            """,
        $"""
            INSERT INTO [edfi].[StudentOtherName] ([Ordinal], [Student_DocumentId], [OtherNameTypeDescriptor_DescriptorId], [FirstName], [LastSurname])
            SELECT
                0,
                ((s.value - 1) / 9) * 10 + ((s.value - 1) % 9) + 2,
                @{PerfFixtureLoaderParameters.OtherNameTypeDescriptorId},
                '{PerfFixtureDefinition.FirstName}',
                '{PerfFixtureDefinition.LastSurname}'
            FROM GENERATE_SERIES(@{PerfFixtureLoaderParameters.FromOrdinal}, @{PerfFixtureLoaderParameters.ToOrdinal}) AS s;
            """,
        $"""
            INSERT INTO [edfi].[StudentPersonalIdentificationDocument] ([Ordinal], [Student_DocumentId], [IdentificationDocumentUseDescriptor_DescriptorId], [PersonalInformationVerificationDescriptor_DescriptorId])
            SELECT
                0,
                ((s.value - 1) / 9) * 10 + ((s.value - 1) % 9) + 2,
                @{PerfFixtureLoaderParameters.IdentificationDocumentUseDescriptorId},
                @{PerfFixtureLoaderParameters.PersonalInformationVerificationDescriptorId}
            FROM GENERATE_SERIES(@{PerfFixtureLoaderParameters.FromOrdinal}, @{PerfFixtureLoaderParameters.ToOrdinal}) AS s;
            """,
        $"""
            INSERT INTO [edfi].[StudentVisa] ([Ordinal], [Student_DocumentId], [VisaDescriptor_DescriptorId])
            SELECT
                0,
                ((s.value - 1) / 9) * 10 + ((s.value - 1) % 9) + 2,
                @{PerfFixtureLoaderParameters.VisaDescriptorId}
            FROM GENERATE_SERIES(@{PerfFixtureLoaderParameters.FromOrdinal}, @{PerfFixtureLoaderParameters.ToOrdinal}) AS s;
            """,
    ];

    /// <summary>
    /// RESEED takes the current seed, so the next identity value follows the descriptor
    /// block that tops the student range — the same next value the PostgreSQL RESTART WITH
    /// form produces.
    /// </summary>
    public static string ReseedSql(PerfFixtureDefinition definition) =>
        $"DBCC CHECKIDENT ('[dms].[Document]', RESEED, {definition.ReseedTargetDocumentId});";

    public static readonly IReadOnlyList<string> StatisticsRefreshSqls =
    [
        "UPDATE STATISTICS [dms].[Document];",
        "UPDATE STATISTICS [edfi].[Student];",
        "UPDATE STATISTICS [edfi].[StudentIdentificationDocument];",
        "UPDATE STATISTICS [edfi].[StudentOtherName];",
        "UPDATE STATISTICS [edfi].[StudentPersonalIdentificationDocument];",
        "UPDATE STATISTICS [edfi].[StudentVisa];",
        "UPDATE STATISTICS [dms].[Descriptor];",
    ];

    /// <summary>
    /// Restricts a document query to student documents, so the analytic id-scheme checks
    /// stay exact with the descriptor block loaded above the student range.
    /// </summary>
    private const string StudentResourceKeyFilter = $"""
        [ResourceKeyId] = (
            SELECT [ResourceKeyId]
            FROM [dms].[ResourceKey]
            WHERE [ProjectName] = '{PerfFixtureDefinition.ProjectName}'
                AND [ResourceName] = '{PerfFixtureDefinition.ResourceName}')
        """;

    /// <summary>
    /// Expected document counts assume the freshly provisioned baseline database holds no
    /// dms.Document rows before the load. Student-id-scheme checks are scoped to student
    /// documents; the descriptor block and the child collection tables get their own checks.
    /// </summary>
    public static IReadOnlyList<PerfVerificationQuery> VerificationQueries(
        PerfFixtureDefinition definition
    ) =>
        [
            new("student-row-count", "SELECT COUNT(*) FROM [edfi].[Student];", definition.RowCount),
            new(
                "student-document-count",
                $"SELECT COUNT(*) FROM [dms].[Document] WHERE {StudentResourceKeyFilter};",
                definition.RowCount
            ),
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
                $"SELECT MIN([DocumentId]) FROM [dms].[Document] WHERE {StudentResourceKeyFilter};",
                PerfFixtureDefinition.MinDocumentId
            ),
            new(
                "max-student-document-id",
                $"SELECT MAX([DocumentId]) FROM [dms].[Document] WHERE {StudentResourceKeyFilter};",
                definition.MaxDocumentId
            ),
            new(
                "max-document-id",
                "SELECT MAX([DocumentId]) FROM [dms].[Document];",
                definition.ReseedTargetDocumentId
            ),
            new(
                "gap-count",
                $"SELECT MAX([DocumentId]) - COUNT(*) FROM [dms].[Document] WHERE {StudentResourceKeyFilter};",
                definition.GapCount
            ),
            new(
                "gap-id-emissions",
                // Scoped to student documents: the primary fixture's descriptor block above
                // the student range can legitimately occupy ids congruent to 1 modulo 10.
                $"SELECT COUNT(*) FROM [dms].[Document] WHERE [DocumentId] % 10 = 1 AND {StudentResourceKeyFilter};",
                0
            ),
            new(
                "document-id-sum",
                $"SELECT SUM([DocumentId]) FROM [dms].[Document] WHERE {StudentResourceKeyFilter};",
                definition.DocumentIdSum()
            ),
            new(
                "descriptor-row-count",
                "SELECT COUNT(*) FROM [dms].[Descriptor];",
                PerfFixtureDefinition.DescriptorCount
            ),
            new(
                "descriptor-document-pairing",
                """
                SELECT COUNT(*)
                FROM [dms].[Descriptor] r
                INNER JOIN [dms].[Document] d ON d.[DocumentId] = r.[DocumentId];
                """,
                PerfFixtureDefinition.DescriptorCount
            ),
            new(
                "students-with-birth-sex-descriptor",
                "SELECT COUNT(*) FROM [edfi].[Student] WHERE [BirthSexDescriptor_DescriptorId] IS NOT NULL;",
                definition.RowCount
            ),
            new(
                "student-identification-document-row-count",
                "SELECT COUNT(*) FROM [edfi].[StudentIdentificationDocument];",
                definition.RowCount
            ),
            new(
                "student-identification-document-descriptor-bindings",
                """
                SELECT COUNT(*)
                FROM [edfi].[StudentIdentificationDocument]
                WHERE [IdentificationDocumentUseDescriptor_DescriptorId] IS NOT NULL
                    AND [PersonalInformationVerificationDescriptor_DescriptorId] IS NOT NULL;
                """,
                definition.RowCount
            ),
            new(
                "student-other-name-row-count",
                "SELECT COUNT(*) FROM [edfi].[StudentOtherName];",
                definition.RowCount
            ),
            new(
                "student-personal-identification-document-row-count",
                "SELECT COUNT(*) FROM [edfi].[StudentPersonalIdentificationDocument];",
                definition.RowCount
            ),
            new("student-visa-row-count", "SELECT COUNT(*) FROM [edfi].[StudentVisa];", definition.RowCount),
        ];
}
