// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Phase 1 exit criterion for the "drop dms.Document/ReferentialIdentity" migration: for every write
/// path (root insert, identity update, plain update, descriptor create/update) the per-resource row's
/// document-metadata columns must equal the values on the owning <c>dms.Document</c> row.
///
/// The six mirrored columns are <c>DocumentUuid</c>, <c>IdentityVersion</c>,
/// <c>IdentityLastModifiedAt</c>, <c>CreatedAt</c>, <c>ContentVersion</c> and
/// <c>ContentLastModifiedAt</c>. <c>CreatedByOwnershipTokenId</c> is deliberately excluded: there is no
/// such column on <c>dms.Document</c>, so the root/descriptor columns are unwritten NULL placeholders
/// reserved for a later phase.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard3)]
public class MssqlDocumentMetadataDualWriteTests
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/referential-identity";

    private const string SchoolTable = "[edfi].[School]";
    private const string StudentTable = "[edfi].[Student]";
    private const string ResourceATable = "[edfi].[ResourceA]";
    private const string DescriptorTable = "[dms].[Descriptor]";

    private MssqlGeneratedDdlFixture _fixture = null!;
    private IMssqlGeneratedDdlBaselineLease _databaseLease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _databaseLease = await MssqlBackendBaselineCache.AcquireLeaseAsync(
            FixtureRelativePath,
            strict: false,
            _fixture.GeneratedDdl
        );
        _database = _databaseLease.Database;
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_databaseLease is not null)
        {
            await _databaseLease.DisposeAsync();
            _database = null!;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Root table: insert
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Root_insert_copies_every_document_metadata_column_onto_the_root_row()
    {
        var seed = await InsertSchoolDocumentAsync(schoolId: 100);

        await AssertOwningRowMirrorsDocumentAsync(SchoolTable, seed.DocumentId);

        // Negative control: had the stamping trigger not copied the value, the row would have kept its
        // own newid() default, which cannot coincide with the client-supplied DocumentUuid.
        var root = await ReadMetadataAsync(SchoolTable, seed.DocumentId);
        root.DocumentUuid.Should()
            .Be(
                seed.DocumentUuid,
                "the root row must carry the client-supplied DocumentUuid, not its own newid() default"
            );

        // CreatedByOwnershipTokenId has no dms.Document counterpart on this schema version and must stay
        // an untouched NULL placeholder.
        (await ReadOwnershipTokenIdAsync(SchoolTable, seed.DocumentId))
            .Should()
            .BeNull("dms.Document has no CreatedByOwnershipTokenId column for the trigger to copy");
    }

    [Test]
    public async Task Root_insert_mirrors_per_row_for_a_multi_row_statement()
    {
        var seedA = await InsertDocumentAsync("School");
        var seedB = await InsertDocumentAsync("School");
        var seedC = await InsertDocumentAsync("School");

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[School] ([DocumentId], [EducationOrganizationId], [NameOfInstitution], [SchoolId])
            VALUES (@documentIdA, 201, 'School A', 201),
                   (@documentIdB, 202, 'School B', 202),
                   (@documentIdC, 203, 'School C', 203);
            """,
            new SqlParameter("@documentIdA", seedA.DocumentId),
            new SqlParameter("@documentIdB", seedB.DocumentId),
            new SqlParameter("@documentIdC", seedC.DocumentId)
        );

        foreach (var seed in new[] { seedA, seedB, seedC })
        {
            await AssertOwningRowMirrorsDocumentAsync(SchoolTable, seed.DocumentId);
            var root = await ReadMetadataAsync(SchoolTable, seed.DocumentId);
            root.DocumentUuid.Should().Be(seed.DocumentUuid);
        }

        var distinctUuids = await _database.ExecuteScalarAsync<int>(
            "SELECT COUNT(DISTINCT [DocumentUuid]) FROM [edfi].[School];"
        );
        distinctUuids
            .Should()
            .Be(
                3,
                "the mirror must be per row — the trigger's mirror UPDATE joins @stamped back to the root "
                    + "table on DocumentId, so a wrong join key would bleed one document's metadata across rows"
            );
    }

    // ---------------------------------------------------------------------------------------------
    // Root table: identity update
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Identity_update_re_mirrors_the_identity_stamp_onto_the_root_row()
    {
        var seed = await InsertSchoolDocumentAsync(schoolId: 300);
        var before = await ReadMetadataAsync(SchoolTable, seed.DocumentId);
        await DelayForDistinctTimestampsAsync();

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[School]
            SET [SchoolId] = @newSchoolId, [EducationOrganizationId] = @newEdOrgId
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@newSchoolId", 301),
            new SqlParameter("@newEdOrgId", 301),
            new SqlParameter("@documentId", seed.DocumentId)
        );

        await AssertOwningRowMirrorsDocumentAsync(SchoolTable, seed.DocumentId);

        var after = await ReadMetadataAsync(SchoolTable, seed.DocumentId);
        after
            .IdentityVersion.Should()
            .BeGreaterThan(before.IdentityVersion, "an identity change must bump IdentityVersion");
        after
            .IdentityLastModifiedAt.Should()
            .BeAfter(before.IdentityLastModifiedAt, "an identity change must move IdentityLastModifiedAt");
        after.DocumentUuid.Should().Be(before.DocumentUuid, "DocumentUuid is stable for a document's life");
        after.CreatedAt.Should().Be(before.CreatedAt, "CreatedAt is stable for a document's life");
    }

    // ---------------------------------------------------------------------------------------------
    // Root table: plain (non-identity) update
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Plain_update_re_mirrors_the_content_stamp_and_leaves_identity_metadata_intact()
    {
        var seed = await InsertSchoolDocumentAsync(schoolId: 400);
        var before = await ReadMetadataAsync(SchoolTable, seed.DocumentId);
        await DelayForDistinctTimestampsAsync();

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[School]
            SET [NameOfInstitution] = @nameOfInstitution
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@nameOfInstitution", "Renamed School"),
            new SqlParameter("@documentId", seed.DocumentId)
        );

        await AssertOwningRowMirrorsDocumentAsync(SchoolTable, seed.DocumentId);

        var after = await ReadMetadataAsync(SchoolTable, seed.DocumentId);
        after
            .ContentVersion.Should()
            .BeGreaterThan(before.ContentVersion, "a content change must bump ContentVersion");
        after
            .ContentLastModifiedAt.Should()
            .BeAfter(before.ContentLastModifiedAt, "a content change must move ContentLastModifiedAt");
        after
            .IdentityVersion.Should()
            .Be(before.IdentityVersion, "a non-identity change must not bump IdentityVersion");
        after
            .IdentityLastModifiedAt.Should()
            .Be(before.IdentityLastModifiedAt, "a non-identity change must not move IdentityLastModifiedAt");
        after.DocumentUuid.Should().Be(before.DocumentUuid);
        after.CreatedAt.Should().Be(before.CreatedAt);
    }

    // ---------------------------------------------------------------------------------------------
    // Cascaded identity update (a parent identity change propagating into a dependent's identity)
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Cascaded_identity_update_re_mirrors_the_parent_and_the_dependent_root_rows()
    {
        // A parent identity change rewrites the dependent's reference columns, which are themselves part
        // of the dependent's identity. Both documents' identity stamps therefore move within the one
        // statement, and both root rows must re-mirror — the dependent's stamp is the easy one to miss
        // because nothing wrote to that table directly.
        var student = await InsertStudentDocumentAsync("STU-CASCADE-OLD");
        var resourceA = await InsertResourceADocumentAsync("resA-1", student.DocumentId, "STU-CASCADE-OLD");

        var beforeStudent = await ReadMetadataAsync(StudentTable, student.DocumentId);
        var beforeResourceA = await ReadMetadataAsync(ResourceATable, resourceA.DocumentId);
        await DelayForDistinctTimestampsAsync();

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[Student]
            SET [StudentUniqueId] = @newStudentUniqueId
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@newStudentUniqueId", "STU-CASCADE-NEW"),
            new SqlParameter("@documentId", student.DocumentId)
        );

        await AssertOwningRowMirrorsDocumentAsync(StudentTable, student.DocumentId);
        await AssertOwningRowMirrorsDocumentAsync(ResourceATable, resourceA.DocumentId);

        var afterStudent = await ReadMetadataAsync(StudentTable, student.DocumentId);
        var afterResourceA = await ReadMetadataAsync(ResourceATable, resourceA.DocumentId);
        afterStudent
            .IdentityVersion.Should()
            .BeGreaterThan(beforeStudent.IdentityVersion, "the parent's identity changed");
        afterResourceA
            .IdentityVersion.Should()
            .BeGreaterThan(
                beforeResourceA.IdentityVersion,
                "the cascade must bump the dependent's identity stamp and re-mirror it onto the root row"
            );
        afterResourceA.DocumentUuid.Should().Be(beforeResourceA.DocumentUuid);
        afterResourceA.CreatedAt.Should().Be(beforeResourceA.CreatedAt);
    }

    // ---------------------------------------------------------------------------------------------
    // Abstract identity table
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Subclass_insert_copies_the_document_uuid_onto_the_abstract_identity_row()
    {
        var seed = await InsertSchoolDocumentAsync(schoolId: 600);

        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                abstractIdentity.[DocumentUuid] AS [IdentityDocumentUuid],
                document.[DocumentUuid] AS [DocumentDocumentUuid],
                root.[DocumentUuid] AS [RootDocumentUuid]
            FROM [edfi].[EducationOrganizationIdentity] abstractIdentity
            INNER JOIN [dms].[Document] document ON document.[DocumentId] = abstractIdentity.[DocumentId]
            INNER JOIN [edfi].[School] root ON root.[DocumentId] = abstractIdentity.[DocumentId]
            WHERE abstractIdentity.[DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", seed.DocumentId)
        );

        var row = rows.Should().ContainSingle().Subject;
        ((Guid)row["IdentityDocumentUuid"]!)
            .Should()
            .Be(
                seed.DocumentUuid,
                "the abstract-identity row must carry the owning document's DocumentUuid, not its own default"
            );
        ((Guid)row["IdentityDocumentUuid"]!).Should().Be((Guid)row["DocumentDocumentUuid"]!);
        ((Guid)row["IdentityDocumentUuid"]!).Should().Be((Guid)row["RootDocumentUuid"]!);
    }

    [Test]
    public async Task Abstract_identity_document_uuid_has_no_default_but_root_and_descriptor_keep_theirs()
    {
        // sys.default_constraints.definition returns the parenthesized form, e.g. "(newid())".
        (await _database.GetColumnDefaultAsync("edfi", "EducationOrganizationIdentity", "DocumentUuid"))
            .Should()
            .BeNull(
                "m23: an out-of-band abstract-identity insert must fail loudly, not acquire a random UUID"
            );
        (await _database.GetColumnDefaultAsync("edfi", "School", "DocumentUuid"))
            .Should()
            .NotBeNull("root tables keep the default (MSSQL AFTER-trigger mirroring needs it)");
        (await _database.GetColumnDefaultAsync("dms", "Descriptor", "DocumentUuid"))
            .Should()
            .NotBeNull("dms.Descriptor keeps the default (AFTER-trigger mirroring on both dialects)");
    }

    // ---------------------------------------------------------------------------------------------
    // dms.Descriptor
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Descriptor_create_and_update_mirror_document_metadata()
    {
        var seed = await InsertDescriptorDocumentAsync("Female");

        await AssertOwningRowMirrorsDocumentAsync(DescriptorTable, seed.DocumentId);

        var afterCreate = await ReadMetadataAsync(DescriptorTable, seed.DocumentId);
        afterCreate
            .DocumentUuid.Should()
            .Be(
                seed.DocumentUuid,
                "the descriptor row must carry the client-supplied DocumentUuid, not its own newid() default"
            );
        (await ReadOwnershipTokenIdAsync(DescriptorTable, seed.DocumentId)).Should().BeNull();

        await DelayForDistinctTimestampsAsync();

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[Descriptor]
            SET [ShortDescription] = @shortDescription
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@shortDescription", "Changed Short Description"),
            new SqlParameter("@documentId", seed.DocumentId)
        );

        await AssertOwningRowMirrorsDocumentAsync(DescriptorTable, seed.DocumentId);

        var afterUpdate = await ReadMetadataAsync(DescriptorTable, seed.DocumentId);
        afterUpdate
            .ContentVersion.Should()
            .BeGreaterThan(
                afterCreate.ContentVersion,
                "a descriptor content change must bump ContentVersion"
            );
        afterUpdate.ContentLastModifiedAt.Should().BeAfter(afterCreate.ContentLastModifiedAt);
        afterUpdate.DocumentUuid.Should().Be(afterCreate.DocumentUuid);
        afterUpdate.CreatedAt.Should().Be(afterCreate.CreatedAt);
    }

    // ---------------------------------------------------------------------------------------------
    // Phase 1 exit criterion, in the brief's own shape
    // ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Every_root_row_equals_its_document_row_after_the_full_write_sequence()
    {
        // Drive one document through create → identity update → plain update, then evaluate the phase's
        // contract query verbatim (server-side, so no client round-trip can mask a timestamp difference).
        var seed = await InsertSchoolDocumentAsync(schoolId: 700);

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[School]
            SET [SchoolId] = 701, [EducationOrganizationId] = 701
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", seed.DocumentId)
        );
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[School]
            SET [NameOfInstitution] = 'Final Name'
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", seed.DocumentId)
        );

        var rows = await _database.QueryRowsAsync(
            """
            SELECT CAST(CASE
                WHEN (r.[DocumentUuid] = d.[DocumentUuid])
                 AND (r.[IdentityVersion] = d.[IdentityVersion])
                 AND (r.[IdentityLastModifiedAt] = d.[IdentityLastModifiedAt])
                 AND (r.[CreatedAt] = d.[CreatedAt])
                 AND (r.[ContentVersion] = d.[ContentVersion])
                 AND (r.[ContentLastModifiedAt] = d.[ContentLastModifiedAt])
                THEN 1 ELSE 0 END AS bit) AS [Matches]
            FROM [edfi].[School] r
            INNER JOIN [dms].[Document] d ON d.[DocumentId] = r.[DocumentId];
            """
        );

        var row = rows.Should().ContainSingle().Subject;
        ((bool)row["Matches"]!)
            .Should()
            .BeTrue("the root row's document-metadata columns must equal dms.Document after every write");
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private sealed record SeededDocument(long DocumentId, Guid DocumentUuid);

    private sealed record DocumentMetadata(
        Guid DocumentUuid,
        long IdentityVersion,
        DateTimeOffset IdentityLastModifiedAt,
        DateTimeOffset CreatedAt,
        long ContentVersion,
        DateTimeOffset ContentLastModifiedAt
    );

    /// <summary>
    /// Compares each mirrored column server-side so that no client-side timestamp conversion can hide a
    /// difference, and names the offending columns when they disagree. Every mirrored column is NOT NULL
    /// on both sides, so a plain <c>=</c> is null-safe here; the T-SQL null-safe form
    /// (<c>a = b OR (a IS NULL AND b IS NULL)</c>) would only be needed for a nullable pair.
    /// </summary>
    private async Task AssertOwningRowMirrorsDocumentAsync(string qualifiedTable, long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            $"""
            SELECT
                CAST(CASE WHEN r.[DocumentUuid] = d.[DocumentUuid] THEN 1 ELSE 0 END AS bit) AS [DocumentUuid],
                CAST(CASE WHEN r.[IdentityVersion] = d.[IdentityVersion] THEN 1 ELSE 0 END AS bit) AS [IdentityVersion],
                CAST(CASE WHEN r.[IdentityLastModifiedAt] = d.[IdentityLastModifiedAt] THEN 1 ELSE 0 END AS bit) AS [IdentityLastModifiedAt],
                CAST(CASE WHEN r.[CreatedAt] = d.[CreatedAt] THEN 1 ELSE 0 END AS bit) AS [CreatedAt],
                CAST(CASE WHEN r.[ContentVersion] = d.[ContentVersion] THEN 1 ELSE 0 END AS bit) AS [ContentVersion],
                CAST(CASE WHEN r.[ContentLastModifiedAt] = d.[ContentLastModifiedAt] THEN 1 ELSE 0 END AS bit) AS [ContentLastModifiedAt]
            FROM {qualifiedTable} r
            INNER JOIN [dms].[Document] d ON d.[DocumentId] = r.[DocumentId]
            WHERE r.[DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );

        var row = rows.Should()
            .ContainSingle($"{qualifiedTable} must hold exactly one row for DocumentId={documentId}")
            .Subject;

        string[] mismatched =
        [
            .. row.Where(pair => !(bool)pair.Value!).Select(pair => pair.Key).Order(StringComparer.Ordinal),
        ];

        mismatched
            .Should()
            .BeEmpty(
                $"every mirrored column on {qualifiedTable} must equal dms.Document; mismatched: {string.Join(", ", mismatched)}"
            );
    }

    private async Task<DocumentMetadata> ReadMetadataAsync(string qualifiedTable, long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            $"""
            SELECT [DocumentUuid], [IdentityVersion], [IdentityLastModifiedAt], [CreatedAt],
                   [ContentVersion], [ContentLastModifiedAt]
            FROM {qualifiedTable}
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );

        var row = rows.Should().ContainSingle().Subject;
        return new DocumentMetadata(
            (Guid)row["DocumentUuid"]!,
            Convert.ToInt64(row["IdentityVersion"], CultureInfo.InvariantCulture),
            ReadDateTimeOffset(row["IdentityLastModifiedAt"]),
            ReadDateTimeOffset(row["CreatedAt"]),
            Convert.ToInt64(row["ContentVersion"], CultureInfo.InvariantCulture),
            ReadDateTimeOffset(row["ContentLastModifiedAt"])
        );
    }

    private async Task<short?> ReadOwnershipTokenIdAsync(string qualifiedTable, long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            $"""
            SELECT [CreatedByOwnershipTokenId]
            FROM {qualifiedTable}
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );

        var value = rows.Should().ContainSingle().Subject["CreatedByOwnershipTokenId"];
        return value is null ? null : Convert.ToInt16(value, CultureInfo.InvariantCulture);
    }

    private async Task<short> GetResourceKeyIdAsync(string resourceName)
    {
        return await _database.ExecuteScalarAsync<short>(
            """
            SELECT [ResourceKeyId]
            FROM [dms].[ResourceKey]
            WHERE [ProjectName] = 'Ed-Fi' AND [ResourceName] = @resourceName;
            """,
            new SqlParameter("@resourceName", resourceName)
        );
    }

    private async Task<SeededDocument> InsertDocumentAsync(string resourceName)
    {
        var resourceKeyId = await GetResourceKeyIdAsync(resourceName);
        var documentUuid = Guid.NewGuid();

        var documentId = await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([DocumentId] bigint);
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT INSERTED.[DocumentId] INTO @Inserted ([DocumentId])
            VALUES (@documentUuid, @resourceKeyId);
            SELECT TOP (1) [DocumentId] FROM @Inserted;
            """,
            new SqlParameter("@documentUuid", documentUuid),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );

        return new SeededDocument(documentId, documentUuid);
    }

    /// <summary>
    /// Mirrors the relational write path: INSERT dms.[Document] (capturing the identity), then the
    /// owning row, both inside one transaction. The owning INSERT names only client-supplied columns, so
    /// every mirrored column reaches its value through the stamping trigger alone.
    /// </summary>
    private async Task<SeededDocument> InsertDocumentWithOwningRowAsync(
        short resourceKeyId,
        string owningInsertSql,
        Func<long, SqlParameter[]> owningParameters
    )
    {
        var documentUuid = Guid.NewGuid();

        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        long documentId;
        await using (var documentCommand = connection.CreateCommand())
        {
            documentCommand.Transaction = transaction;
            documentCommand.CommandText = """
                DECLARE @Inserted TABLE ([DocumentId] bigint);
                INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
                OUTPUT INSERTED.[DocumentId] INTO @Inserted ([DocumentId])
                VALUES (@documentUuid, @resourceKeyId);
                SELECT TOP (1) [DocumentId] FROM @Inserted;
                """;
            documentCommand.Parameters.Add(new SqlParameter("@documentUuid", documentUuid));
            documentCommand.Parameters.Add(new SqlParameter("@resourceKeyId", resourceKeyId));
            documentId = (long)(await documentCommand.ExecuteScalarAsync())!;
        }

        await using (var owningCommand = connection.CreateCommand())
        {
            owningCommand.Transaction = transaction;
            owningCommand.CommandText = owningInsertSql;
            owningCommand.Parameters.AddRange(owningParameters(documentId));
            await owningCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return new SeededDocument(documentId, documentUuid);
    }

    private async Task<SeededDocument> InsertSchoolDocumentAsync(
        int schoolId,
        string nameOfInstitution = "Test School"
    )
    {
        return await InsertDocumentWithOwningRowAsync(
            await GetResourceKeyIdAsync("School"),
            """
            INSERT INTO [edfi].[School] ([DocumentId], [EducationOrganizationId], [NameOfInstitution], [SchoolId])
            VALUES (@documentId, @educationOrganizationId, @nameOfInstitution, @schoolId);
            """,
            documentId =>
                [
                    new SqlParameter("@documentId", documentId),
                    new SqlParameter("@educationOrganizationId", schoolId),
                    new SqlParameter("@nameOfInstitution", nameOfInstitution),
                    new SqlParameter("@schoolId", schoolId),
                ]
        );
    }

    private async Task<SeededDocument> InsertStudentDocumentAsync(string studentUniqueId)
    {
        return await InsertDocumentWithOwningRowAsync(
            await GetResourceKeyIdAsync("Student"),
            """
            INSERT INTO [edfi].[Student] ([DocumentId], [StudentUniqueId], [FirstName])
            VALUES (@documentId, @studentUniqueId, @firstName);
            """,
            documentId =>
                [
                    new SqlParameter("@documentId", documentId),
                    new SqlParameter("@studentUniqueId", studentUniqueId),
                    new SqlParameter("@firstName", "Test"),
                ]
        );
    }

    private async Task<SeededDocument> InsertResourceADocumentAsync(
        string resourceAId,
        long studentDocumentId,
        string studentUniqueId
    )
    {
        return await InsertDocumentWithOwningRowAsync(
            await GetResourceKeyIdAsync("ResourceA"),
            """
            INSERT INTO [edfi].[ResourceA] ([DocumentId], [ResourceAId], [StudentReference_DocumentId], [StudentReference_StudentUniqueId])
            VALUES (@documentId, @resourceAId, @studentDocumentId, @studentUniqueId);
            """,
            documentId =>
                [
                    new SqlParameter("@documentId", documentId),
                    new SqlParameter("@resourceAId", resourceAId),
                    new SqlParameter("@studentDocumentId", studentDocumentId),
                    new SqlParameter("@studentUniqueId", studentUniqueId),
                ]
        );
    }

    /// <summary>
    /// Mirrors DescriptorWriteHandler's create: INSERT dms.[Document] then dms.[Descriptor] in one
    /// transaction, supplying no value for any mirrored column.
    /// </summary>
    private async Task<SeededDocument> InsertDescriptorDocumentAsync(string codeValue)
    {
        // The fixture seeds no descriptor resource, and the mirror is resource-agnostic, so any
        // ResourceKey satisfies FK_Document_ResourceKey.
        var resourceKeyId = await _database.ExecuteScalarAsync<short>(
            "SELECT MIN([ResourceKeyId]) FROM [dms].[ResourceKey];"
        );
        var uri = $"uri://ed-fi.org/SexDescriptor#{codeValue}";

        return await InsertDocumentWithOwningRowAsync(
            resourceKeyId,
            """
            INSERT INTO [dms].[Descriptor]
                ([DocumentId], [Namespace], [CodeValue], [ShortDescription], [Description],
                 [EffectiveBeginDate], [EffectiveEndDate], [Discriminator], [Uri])
            VALUES (@documentId, @namespace, @codeValue, @shortDescription, @description,
                    NULL, NULL, @discriminator, @uri);
            """,
            documentId =>
                [
                    new SqlParameter("@documentId", documentId),
                    new SqlParameter("@namespace", "uri://ed-fi.org/SexDescriptor"),
                    new SqlParameter("@codeValue", codeValue),
                    new SqlParameter("@shortDescription", codeValue),
                    new SqlParameter("@description", codeValue),
                    new SqlParameter("@discriminator", uri),
                    new SqlParameter("@uri", uri),
                ]
        );
    }

    private async Task DelayForDistinctTimestampsAsync()
    {
        // Server-side delay so the post-write stamps are strictly greater than the seed stamps, letting
        // assertions use BeAfter instead of the weaker BeOnOrAfter (which cannot catch a stamp that
        // never moved).
        await _database.ExecuteNonQueryAsync("WAITFOR DELAY '00:00:00.020';");
    }

    private static DateTimeOffset ReadDateTimeOffset(object? value)
    {
        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(
                dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime
            ),
            string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(
                $"Unsupported timestamp value type '{value?.GetType().FullName ?? "<null>"}'."
            ),
        };
    }
}
