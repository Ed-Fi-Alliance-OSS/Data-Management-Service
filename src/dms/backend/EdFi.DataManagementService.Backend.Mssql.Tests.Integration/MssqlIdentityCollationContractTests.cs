// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// SQL Server evidence that the identity collation contract holds independent of the database default:
/// ds-5.2 is provisioned into a case-sensitive database, and identity columns still compare
/// case-insensitively in storage, in <c>/deletes</c> recreation suppression and in GET-many filters.
/// </summary>
/// <remarks>
/// Provisioned directly rather than through the shared baseline cache, because the cache's databases
/// use the server default collation.
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard3)]
public class Given_A_Mssql_Case_Sensitive_Default_Database_With_Identity_Collation
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";
    private const string CaseSensitiveCollation = "Latin1_General_100_CS_AS_SC_UTF8";
    private const string IdentityCollation = "SQL_Latin1_General_CP1_CI_AS";
    private const long SchoolId = 255901;

    /// <summary>
    /// Enough students that a scan is plainly the expensive option against a single-row identity match.
    /// </summary>
    private const int SeededStudentCount = 2_000;

    private const string MatchingStudentUniqueId = "ABC123";

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName AcademicWeekResource = new("Ed-Fi", "AcademicWeek");
    private static readonly QualifiedResourceName StudentResource = new("Ed-Fi", "Student");

    private static readonly ResourceInfo AcademicWeekResourceInfo = new(
        new ProjectName("Ed-Fi"),
        new ResourceName("AcademicWeek"),
        IsDescriptor: false,
        new SemVer("5.2.0"),
        AllowIdentityUpdates: false
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private long _schoolDocumentId;
    private long _matchingStudentDocumentId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        if (!await MssqlTestDatabaseHelper.CollationExistsAsync(CaseSensitiveCollation))
        {
            Assert.Ignore($"SQL Server instance does not support collation '{CaseSensitiveCollation}'.");
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(
            _fixture.GeneratedDdl,
            databaseCollation: CaseSensitiveCollation
        );

        _schoolDocumentId = await SeedSchoolAsync();
        _matchingStudentDocumentId = await SeedStudentsAsync();
        await _database.ExecuteNonQueryAsync("UPDATE STATISTICS [edfi].[Student];");
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task It_keeps_the_case_sensitive_database_default()
    {
        (
            await _database.ExecuteScalarAsync<string>(
                "SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128));"
            )
        )
            .Should()
            .Be(CaseSensitiveCollation);
    }

    [Test]
    public async Task It_pins_representative_identity_columns()
    {
        string[] identityColumns =
        [
            "edfi.StudentSchoolAssociation.Student_StudentUniqueId",
            "edfi.GeneralStudentProgramAssociationIdentity.Program_ProgramName",
            "edfi.StudentEducationOrganizationAssociationAddress.City",
            "tracked_changes_edfi.Descriptor.OldNamespace",
        ];

        var collations = await ReadColumnCollationsAsync(identityColumns);

        collations
            .Should()
            .BeEquivalentTo(identityColumns.ToDictionary(column => column, _ => IdentityCollation));
    }

    [Test]
    public async Task It_gives_key_unified_aliases_the_identity_collation()
    {
        var (table, alias) = _fixture
            .MappingSet.Model.ConcreteResourcesInNameOrder.Where(resource =>
                resource.StorageKind == ResourceStorageKind.RelationalTables
            )
            .SelectMany(resource => resource.RelationalModel.TablesInDependencyOrder)
            .SelectMany(table => table.Columns.Select(column => (Table: table, Column: column)))
            .First(entry =>
                entry.Column.Storage is ColumnStorage.UnifiedAlias
                && entry.Column.ScalarType?.Kind == ScalarKind.String
                && entry.Column.UsesSqlServerIdentityCollation
            );
        var canonical = ((ColumnStorage.UnifiedAlias)alias.Storage).CanonicalColumn;
        string aliasKey = $"{table.Table.Schema.Value}.{table.Table.Name}.{alias.ColumnName.Value}";
        string canonicalKey = $"{table.Table.Schema.Value}.{table.Table.Name}.{canonical.Value}";

        var collations = await ReadColumnCollationsAsync([aliasKey, canonicalKey]);

        collations
            .Should()
            .BeEquivalentTo(
                new Dictionary<string, string>
                {
                    [aliasKey] = IdentityCollation,
                    [canonicalKey] = IdentityCollation,
                },
                "the computed alias carries no COLLATE text, so it must inherit the pinned canonical column"
            );
    }

    /// <summary>
    /// The compact descriptor catalog verifier must accept the pinned descriptor identity collation on a
    /// database whose default differs from it, rather than requiring the database default.
    /// </summary>
    [Test]
    public async Task It_passes_the_compact_descriptor_catalog_verifier()
    {
        string repositoryRoot = FixturePathResolver.FindRepositoryRoot(
            TestContext.CurrentContext.TestDirectory
        );
        string assertionSql = await CompactDescriptorCatalogAssertions.RenderAsync(
            repositoryRoot,
            "mssql",
            Path.Combine(
                repositoryRoot,
                FixtureRelativePath,
                "expected",
                "relational-model.mssql.manifest.json"
            )
        );

        Func<Task> verify = () => _database.ExecuteNonQueryAsync(assertionSql);

        await verify.Should().NotThrowAsync();
    }

    [Test]
    public async Task It_suppresses_a_deleted_academic_week_recreated_with_only_identity_casing_changed()
    {
        long windowStart = await NextChangeVersionAsync();
        long academicWeekDocumentId = await InsertAcademicWeekAsync("Week One Casing");

        await DeleteAcademicWeekRootAsync(academicWeekDocumentId);
        await ReinsertAcademicWeekRootAsync(academicWeekDocumentId, "WEEK ONE CASING");

        TrackedChangeQueryResult result = await QueryAcademicWeekDeletesAsync(windowStart);

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0L);
    }

    [Test]
    public async Task It_still_reports_a_deleted_academic_week_recreated_with_a_different_identity()
    {
        long windowStart = await NextChangeVersionAsync();
        long academicWeekDocumentId = await InsertAcademicWeekAsync("Week One Control");

        await DeleteAcademicWeekRootAsync(academicWeekDocumentId);
        await ReinsertAcademicWeekRootAsync(academicWeekDocumentId, "Week Two Control");

        TrackedChangeQueryResult result = await QueryAcademicWeekDeletesAsync(windowStart);

        result.Items.Should().ContainSingle();
        result.TotalCount.Should().Be(1L);
        JsonObject keyValues = result.Items[0]!["keyValues"]!.AsObject();
        keyValues["weekIdentifier"]!.GetValue<string>().Should().Be("Week One Control");
    }

    [Test]
    public async Task It_raises_no_collation_conflict()
    {
        long windowStart = await NextChangeVersionAsync();
        long academicWeekDocumentId = await InsertAcademicWeekAsync("Week One Conflict");

        await DeleteAcademicWeekRootAsync(academicWeekDocumentId);
        await ReinsertAcademicWeekRootAsync(academicWeekDocumentId, "week one conflict");

        Func<Task> query = () => QueryAcademicWeekDeletesAsync(windowStart);

        await query.Should().NotThrowAsync<SqlException>();
    }

    [Test]
    public async Task It_matches_a_case_variant_identity_filter()
    {
        PageKeysetSpec.Query keyset = PlanStudentUniqueIdFilter("abc123");

        var documentIds = new List<long>();
        await using (SqlConnection connection = new(_database.ConnectionString))
        {
            await connection.OpenAsync();
            await using SqlCommand command = CreatePageCommand(connection, keyset);
            await using SqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                documentIds.Add(reader.GetInt64(0));
            }
        }

        documentIds.Should().Equal(_matchingStudentDocumentId);
    }

    [Test]
    public async Task It_seeks_the_identity_index_for_the_filter()
    {
        PageKeysetSpec.Query keyset = PlanStudentUniqueIdFilter("abc123");
        IReadOnlySet<string> studentUniqueIdLeadingIndexes = await ReadStudentUniqueIdLeadingIndexesAsync();

        string plan = await CapturePlanAsync(keyset);

        XNamespace showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        var studentAccesses = XDocument
            .Parse(plan)
            .Descendants(showplan + "Object")
            .Where(o =>
                (string?)o.Attribute("Schema") == "[edfi]" && (string?)o.Attribute("Table") == "[Student]"
            )
            .Where(o => (string?)o.Parent?.Attribute("Lookup") is not ("1" or "true"))
            .Select(o => new
            {
                PhysicalOp = (string?)
                    o.Ancestors(showplan + "RelOp").FirstOrDefault()?.Attribute("PhysicalOp"),
                Index = (string?)o.Attribute("Index"),
            })
            .ToList();

        studentAccesses.Should().NotBeEmpty();
        studentAccesses
            .Should()
            .OnlyContain(
                access =>
                    access.PhysicalOp == "Index Seek"
                    && access.Index != null
                    && studentUniqueIdLeadingIndexes.Contains(access.Index),
                "a case-variant identity filter must seek the StudentUniqueId index rather than scan "
                    + "edfi.Student, which a query-side COLLATE would force"
            );
    }

    private PageKeysetSpec.Query PlanStudentUniqueIdFilter(string value)
    {
        SupportedRelationalQueryField supportedField = _fixture
            .MappingSet.GetQueryCapabilityOrThrow(StudentResource)
            .SupportedFieldsByQueryField["studentUniqueId"];
        var queryElement = new QueryElement(
            "studentUniqueId",
            [new JsonPath("$.studentUniqueId")],
            value,
            "string"
        );

        return new RelationalQueryPageKeysetPlanner(SqlDialect.Mssql).Plan(
            _fixture.MappingSet.GetReadPlanOrThrow(StudentResource).Model.Root,
            new RelationalQueryPreprocessingResult(
                new RelationalQueryPreprocessingOutcome.Continue(),
                [
                    new PreprocessedRelationalQueryElement(
                        queryElement,
                        supportedField,
                        new PreprocessedRelationalQueryValue.Raw(value)
                    ),
                ]
            ),
            new CollectionPaging.Traditional(
                new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
            )
        );
    }

    private static SqlCommand CreatePageCommand(SqlConnection connection, PageKeysetSpec.Query keyset)
    {
        SqlCommand command = connection.CreateCommand();
        command.CommandText = keyset.Plan.PageDocumentIdSql;
        command.CommandTimeout = 300;

        foreach (var parameter in keyset.ParameterValues)
        {
            command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
        }

        return command;
    }

    private async Task<string> CapturePlanAsync(PageKeysetSpec.Query keyset)
    {
        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();

        await using (SqlCommand on = connection.CreateCommand())
        {
            on.CommandText = "SET STATISTICS XML ON;";
            await on.ExecuteNonQueryAsync();
        }

        await using SqlCommand command = CreatePageCommand(connection, keyset);
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        StringBuilder plan = new();
        do
        {
            while (await reader.ReadAsync())
            {
                if (
                    reader.FieldCount == 1
                    && reader.GetFieldType(0) == typeof(string)
                    && !await reader.IsDBNullAsync(0)
                )
                {
                    plan.AppendLine(reader.GetString(0));
                }
            }
        } while (await reader.NextResultAsync());

        await TestContext.Out.WriteLineAsync(keyset.Plan.PageDocumentIdSql);
        await TestContext.Out.WriteLineAsync(plan.ToString());

        return plan.ToString();
    }

    private async Task<IReadOnlySet<string>> ReadStudentUniqueIdLeadingIndexesAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT QUOTENAME(i.name) AS [IndexName]
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic
                ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1
            INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'edfi.Student') AND c.name = N'StudentUniqueId';
            """
        );

        return rows.Select(row => (string)row["IndexName"]!).ToHashSet(StringComparer.Ordinal);
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadColumnCollationsAsync(
        IReadOnlyCollection<string> qualifiedColumns
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT s.name + N'.' + t.name + N'.' + c.name AS [QualifiedColumn], c.collation_name AS [Collation]
            FROM sys.columns c
            INNER JOIN sys.tables t ON t.object_id = c.object_id
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE c.collation_name IS NOT NULL;
            """
        );

        return rows.Select(row => ((string)row["QualifiedColumn"]!, (string)row["Collation"]!))
            .Where(entry => qualifiedColumns.Contains(entry.Item1))
            .ToDictionary(entry => entry.Item1, entry => entry.Item2, StringComparer.Ordinal);
    }

    private async Task<long> NextChangeVersionAsync() =>
        await _database.ExecuteScalarAsync<long>("SELECT [dms].[GetMaxChangeVersion]() + 1;");

    private async Task<long> InsertDocumentAsync(QualifiedResourceName resource)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @inserted TABLE ([DocumentId] bigint NOT NULL);

            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT INSERTED.[DocumentId] INTO @inserted ([DocumentId])
            VALUES (NEWID(), @resourceKeyId);

            SELECT [DocumentId] FROM @inserted;
            """,
            new SqlParameter("@resourceKeyId", _fixture.MappingSet.ResourceKeyIdByResource[resource])
        );
    }

    private async Task<long> SeedSchoolAsync()
    {
        long documentId = await InsertDocumentAsync(SchoolResource);

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[School] ([DocumentId], [ContentVersion], [NameOfInstitution], [SchoolId])
            SELECT [DocumentId], [ContentVersion], N'Identity Collation High School', @schoolId
            FROM [dms].[Document]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@schoolId", SchoolId)
        );

        return documentId;
    }

    /// <summary>
    /// Seeds <see cref="SeededStudentCount"/> students, exactly one of which carries
    /// <see cref="MatchingStudentUniqueId"/>, and returns that student's document id.
    /// </summary>
    private async Task<long> SeedStudentsAsync()
    {
        await _database.ExecuteNonQueryAsync(
            """
            WITH [numbers] AS (
                SELECT TOP (@rowCount) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS [Ordinal]
                FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b
            )
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            SELECT NEWID(), @resourceKeyId
            FROM [numbers];

            WITH [students] AS (
                SELECT [DocumentId], [ContentVersion], ROW_NUMBER() OVER (ORDER BY [DocumentId]) AS [Ordinal]
                FROM [dms].[Document]
                WHERE [ResourceKeyId] = @resourceKeyId
            )
            INSERT INTO [edfi].[Student]
                ([DocumentId], [ContentVersion], [BirthDate], [FirstName], [LastSurname], [StudentUniqueId])
            SELECT
                [DocumentId],
                [ContentVersion],
                '2010-01-01',
                N'Seek',
                N'Student',
                CASE WHEN [Ordinal] = 1 THEN @matchingStudentUniqueId ELSE CONCAT(N'SEEK', [Ordinal]) END
            FROM [students];
            """,
            new SqlParameter("@resourceKeyId", _fixture.MappingSet.ResourceKeyIdByResource[StudentResource]),
            new SqlParameter("@rowCount", SeededStudentCount),
            new SqlParameter("@matchingStudentUniqueId", MatchingStudentUniqueId)
        );

        return await _database.ExecuteScalarAsync<long>(
            "SELECT [DocumentId] FROM [edfi].[Student] WHERE [StudentUniqueId] = @studentUniqueId;",
            new SqlParameter("@studentUniqueId", MatchingStudentUniqueId)
        );
    }

    private async Task<long> InsertAcademicWeekAsync(string weekIdentifier)
    {
        long documentId = await InsertDocumentAsync(AcademicWeekResource);
        await InsertAcademicWeekRowAsync(documentId, weekIdentifier);

        return documentId;
    }

    private Task ReinsertAcademicWeekRootAsync(long documentId, string weekIdentifier) =>
        InsertAcademicWeekRowAsync(documentId, weekIdentifier);

    private async Task InsertAcademicWeekRowAsync(long documentId, string weekIdentifier)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[AcademicWeek] (
                [DocumentId],
                [School_DocumentId],
                [School_SchoolId],
                [BeginDate],
                [EndDate],
                [TotalInstructionalDays],
                [WeekIdentifier]
            )
            VALUES (@documentId, @schoolDocumentId, @schoolId, '2025-08-15', '2025-08-22', 5, @weekIdentifier);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@schoolDocumentId", _schoolDocumentId),
            new SqlParameter("@schoolId", SchoolId),
            new SqlParameter("@weekIdentifier", weekIdentifier)
        );
    }

    private async Task DeleteAcademicWeekRootAsync(long documentId)
    {
        await _database.ExecuteNonQueryAsync(
            "DELETE FROM [edfi].[AcademicWeek] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", documentId)
        );
    }

    private async Task<TrackedChangeQueryResult> QueryAcademicWeekDeletesAsync(long windowStart)
    {
        ConcreteResourceModel resourceModel = _fixture.MappingSet.Model.ConcreteResourcesInNameOrder.Single(
            resource => resource.RelationalModel.Resource == AcademicWeekResource
        );
        TrackedChangeTableInfo trackedTable = _fixture.MappingSet.Model.TrackedChangeTablesInNameOrder.Single(
            table => table.SourceTable == resourceModel.RelationalModel.Root.Table
        );

        var repository = new RelationalChangeQueryRepository(
            new MssqlRelationalCommandExecutor(
                async ct =>
                {
                    var connection = new SqlConnection(_database.ConnectionString);
                    await connection.OpenAsync(ct);
                    return connection;
                },
                NullLogger<MssqlRelationalCommandExecutor>.Instance
            ),
            new MssqlRelationalParameterConfigurator()
        );

        return await repository.QueryTrackedChanges(
            new IdentityCollationTrackedChangeQueryRequest(
                ResourceInfo: AcademicWeekResourceInfo,
                Operation: ChangeQueryEndpointOperation.Deletes,
                PaginationParameters: new PaginationParameters(
                    Limit: 25,
                    Offset: 0,
                    TotalCount: true,
                    MaximumPageSize: 500
                ),
                ChangeVersionRange: new ChangeVersionRange(windowStart, long.MaxValue),
                TraceId: new TraceId("mssql-identity-collation-deletes"),
                AuthorizationContext: new RelationalAuthorizationContext([]),
                AuthorizationStrategyEvaluators: [],
                MappingSet: _fixture.MappingSet,
                ResourceModel: resourceModel,
                TrackedChangeTable: trackedTable
            )
        );
    }
}

file sealed record IdentityCollationTrackedChangeQueryRequest(
    ResourceInfo ResourceInfo,
    ChangeQueryEndpointOperation Operation,
    PaginationParameters PaginationParameters,
    ChangeVersionRange ChangeVersionRange,
    TraceId TraceId,
    RelationalAuthorizationContext AuthorizationContext,
    IReadOnlyList<AuthorizationStrategyEvaluator> AuthorizationStrategyEvaluators,
    MappingSet MappingSet,
    ConcreteResourceModel ResourceModel,
    TrackedChangeTableInfo TrackedChangeTable
) : IRelationalTrackedChangeQueryRequest;
