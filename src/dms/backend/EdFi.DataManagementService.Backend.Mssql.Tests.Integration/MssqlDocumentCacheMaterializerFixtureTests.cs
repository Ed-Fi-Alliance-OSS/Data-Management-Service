// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category(MssqlCiShards.Shard4)]
public class Given_Mssql_DocumentCacheMaterializer_Fixtures
{
    private string _databaseName = null!;
    private string _connectionString = null!;
    private RecordingRelationalCommandExecutor _commandExecutor = null!;

    [SetUp]
    public void SetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "MSSQL connection string not configured."
        );

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);
        _connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);
        _commandExecutor = new RecordingRelationalCommandExecutor(
            new MssqlRelationalCommandExecutor(
                async cancellationToken =>
                {
                    var connection = new SqlConnection(_connectionString);
                    await connection.OpenAsync(cancellationToken);
                    return (DbConnection)connection;
                },
                NullLogger<MssqlRelationalCommandExecutor>.Instance
            )
        );
    }

    [TearDown]
    public void TearDown()
    {
        if (_databaseName is not null && MssqlTestDatabaseHelper.IsConfigured())
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public async Task It_materializes_the_descriptor_fixture_from_real_Mssql_source_rows()
    {
        var fixture = LoadFixture("descriptor-school-type");
        await SeedFixtureAsync(fixture);
        var mappingSet = MssqlDocumentCacheMaterializerFixtureMappingSet.CreateDescriptorFixture();

        var result = await CreateMaterializer().MaterializeAsync(CreateRequest(mappingSet, fixture));

        var success = result.Should().BeOfType<DocumentCacheMaterializationResult.Success>().Subject;
        AssertCandidateMatchesFixture(success.Candidate, fixture);
        AssertRecordedCommandsStayOnCanonicalSource();
    }

    [Test]
    public async Task It_materializes_the_extension_and_nested_collection_fixture_from_real_Mssql_hydration()
    {
        var fixture = LoadFixture("extension-student-school-association");
        await SeedFixtureAsync(fixture);
        var mappingSet = MssqlDocumentCacheMaterializerFixtureMappingSet.CreateExtensionFixture();

        var result = await CreateMaterializer().MaterializeAsync(CreateRequest(mappingSet, fixture));

        var success = result.Should().BeOfType<DocumentCacheMaterializationResult.Success>().Subject;
        AssertCandidateMatchesFixture(success.Candidate, fixture);
        AssertRecordedCommandsStayOnCanonicalSource();
    }

    [Test]
    public async Task It_returns_missing_source_for_an_absent_canonical_document_row()
    {
        var fixture = LoadFixture("descriptor-school-type");
        await SeedFixtureAsync(fixture);
        var mappingSet = MssqlDocumentCacheMaterializerFixtureMappingSet.CreateDescriptorFixture();

        var result = await CreateMaterializer()
            .MaterializeAsync(CreateRequest(mappingSet, documentId: 979999));

        result.Should().BeSameAs(DocumentCacheMaterializationResult.MissingSource.Instance);
        AssertRecordedCommandsStayOnCanonicalSource();
    }

    [Test]
    public async Task It_preserves_LastModifiedAt_precision_while_JSON_lastModifiedDate_uses_whole_second_text_without_rounding()
    {
        var fixture = LoadFixture("descriptor-school-type");
        await SeedFixtureAsync(fixture);
        var mappingSet = MssqlDocumentCacheMaterializerFixtureMappingSet.CreateDescriptorFixture();
        var preciseLastModifiedAt = new DateTimeOffset(2026, 7, 30, 14, 15, 16, TimeSpan.Zero).AddTicks(
            9_876_543
        );
        await UpdateContentLastModifiedAtAsync(
            fixture.SourceSetup.Documents[0].DocumentId,
            preciseLastModifiedAt
        );

        var result = await CreateMaterializer().MaterializeAsync(CreateRequest(mappingSet, fixture));

        var success = result.Should().BeOfType<DocumentCacheMaterializationResult.Success>().Subject;
        success.Candidate.LastModifiedAt.Should().Be(preciseLastModifiedAt);
        success.Candidate.DocumentJson["_lastModifiedDate"]!
            .GetValue<string>()
            .Should()
            .Be("2026-07-30T14:15:16Z");
        success.Candidate.StreamEtag.Should().Be(fixture.ExpectedStreamEtag);
        AssertRecordedCommandsStayOnCanonicalSource();
    }

    [Test]
    public async Task It_throws_the_fixture_projection_failure_when_source_metadata_is_stable_but_the_body_is_missing()
    {
        var fixture = LoadFixture("invariant-missing-school-body");
        await SeedFixtureAsync(fixture);
        await EnsureSchoolTableExistsAsync();
        var mappingSet = MssqlDocumentCacheMaterializerFixtureMappingSet.CreateMissingSchoolBodyFixture();

        var exception = await FluentActions
            .Invoking(async () =>
                await CreateMaterializer().MaterializeAsync(CreateRequest(mappingSet, fixture))
            )
            .Should()
            .ThrowAsync<DocumentCacheProjectionProcessingException>();
        var thrown = exception.Subject.Single();

        MaterializedDocumentFixtureAssertions.AssertProjectionFailureMatchesFixture(
            new MaterializedDocumentFixtureActualProjectionFailure(
                thrown.Reason.ToString(),
                thrown.FailureMetadata.DocumentId,
                thrown.FailureMetadata.ResourceKeyId,
                thrown.FailureMetadata.ProjectName,
                thrown.FailureMetadata.ResourceName,
                thrown.FailureMetadata.ResourceVersion
            ),
            fixture
        );
        AssertRecordedCommandsStayOnCanonicalSource();
    }

    [Test]
    public async Task It_throws_a_target_mapping_failure_when_the_document_resource_key_is_not_in_the_selected_mapping_set()
    {
        var fixture = LoadFixture("invariant-missing-school-body");
        await SeedFixtureAsync(fixture);
        var mappingSet = MssqlDocumentCacheMaterializerFixtureMappingSet.CreateWithoutSchoolResourceKey();

        var exception = await FluentActions
            .Invoking(async () =>
                await CreateMaterializer().MaterializeAsync(CreateRequest(mappingSet, fixture))
            )
            .Should()
            .ThrowAsync<DocumentCacheTargetMappingException>();
        var thrown = exception.Subject.Single();

        thrown.Reason.Should().Be(DocumentCacheTargetMappingFailureReason.ResourceKeyMissingFromMappingSet);
        thrown.FailureMetadata.DocumentId.Should().Be(fixture.SourceSetup.Documents[0].DocumentId);
        thrown.FailureMetadata.ResourceKeyId.Should().Be(244);
        AssertRecordedCommandsStayOnCanonicalSource();
    }

    [Test]
    public async Task It_throws_a_target_mapping_failure_when_the_selected_mapping_set_has_no_read_plan()
    {
        var fixture = LoadFixture("invariant-missing-school-body");
        await SeedFixtureAsync(fixture);
        var mappingSet =
            MssqlDocumentCacheMaterializerFixtureMappingSet.CreateSchoolResourceWithoutReadPlan();

        var exception = await FluentActions
            .Invoking(async () =>
                await CreateMaterializer().MaterializeAsync(CreateRequest(mappingSet, fixture))
            )
            .Should()
            .ThrowAsync<DocumentCacheTargetMappingException>();
        var thrown = exception.Subject.Single();

        thrown.Reason.Should().Be(DocumentCacheTargetMappingFailureReason.ReadPlanMissing);
        thrown.FailureMetadata.DocumentId.Should().Be(fixture.SourceSetup.Documents[0].DocumentId);
        thrown.FailureMetadata.ResourceKeyId.Should().Be(244);
        thrown.FailureMetadata.ProjectName.Should().Be("Ed-Fi");
        thrown.FailureMetadata.ResourceName.Should().Be("School");
        thrown.FailureMetadata.ResourceVersion.Should().Be("5.2.0");
        AssertRecordedCommandsStayOnCanonicalSource();
    }

    private static MaterializedDocumentFixture LoadFixture(string caseName) =>
        MaterializedDocumentFixtureCatalog.LoadCase(TestContext.CurrentContext.TestDirectory, caseName);

    private async Task SeedFixtureAsync(MaterializedDocumentFixture fixture)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await new MaterializedDocumentFixtureSeeder(MaterializedDocumentFixtureSqlDialect.Mssql).SeedAsync(
            connection,
            fixture
        );
    }

    private async Task EnsureSchoolTableExistsAsync()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'edfi') EXEC('CREATE SCHEMA [edfi]');

            IF OBJECT_ID(N'[edfi].[School]', N'U') IS NULL
            BEGIN
                CREATE TABLE [edfi].[School] (
                    [DocumentId] bigint NOT NULL CONSTRAINT [PK_School] PRIMARY KEY,
                    [SchoolId] int NULL,
                    [NameOfInstitution] varchar(1024) NULL
                );
            END;
            """,
            connection
        );
        await command.ExecuteNonQueryAsync();
    }

    private async Task UpdateContentLastModifiedAtAsync(long documentId, DateTimeOffset lastModifiedAt)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            UPDATE [dms].[Document]
            SET [ContentLastModifiedAt] = @lastModifiedAt
            WHERE [DocumentId] = @documentId;
            """,
            connection
        );
        command.Parameters.AddWithValue("@lastModifiedAt", lastModifiedAt);
        command.Parameters.AddWithValue("@documentId", documentId);
        await command.ExecuteNonQueryAsync();
    }

    private DocumentCacheMaterializer CreateMaterializer()
    {
        var materializationDataStore = new AmbientDocumentCacheMaterializationDataStore(
            _commandExecutor,
            new MssqlFixtureDocumentHydrator(_connectionString)
        );

        return new DocumentCacheMaterializer(
            new DocumentCacheSourceMetadataReader(materializationDataStore),
            new DocumentCacheDescriptorHydrator(materializationDataStore),
            materializationDataStore,
            new RelationalReadMaterializer(
                new DeterministicLinkSlugResolver(
                    new Dictionary<short, DocumentLinkSlugTriple>
                    {
                        [244] = new("ed-fi", "schools", "School"),
                        [282] = new("ed-fi", "students", "Student"),
                    }
                ),
                Options.Create(new ResourceLinksOptions { Enabled = false }),
                new ServedEtagComposer()
            ),
            new ServedEtagComposer()
        );
    }

    private static DocumentCacheMaterializationRequest CreateRequest(
        MappingSet mappingSet,
        MaterializedDocumentFixture fixture
    ) => CreateRequest(mappingSet, fixture.SourceSetup.Documents[0].DocumentId);

    private static DocumentCacheMaterializationRequest CreateRequest(
        MappingSet mappingSet,
        long documentId
    ) =>
        new(
            new DocumentCacheMaterializationTargetContext(
                new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7)),
                mappingSet,
                DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated
            ),
            documentId,
            selectedRequiredContentVersion: 456,
            DocumentCacheMaterializationPurpose.Fixture,
            CancellationToken.None
        );

    private static void AssertCandidateMatchesFixture(
        DocumentCacheMaterializationCandidate candidate,
        MaterializedDocumentFixture fixture
    )
    {
        MaterializedDocumentFixtureAssertions.AssertCandidateMatchesFixture(
            new MaterializedDocumentFixtureActualCacheRow(
                candidate.DocumentId,
                candidate.DocumentUuid.Value.ToString("D"),
                candidate.ProjectName,
                candidate.ResourceName,
                candidate.ResourceVersion,
                candidate.ContentVersion,
                candidate.LastModifiedAt,
                candidate.StreamEtag,
                candidate.DocumentJson
            ),
            fixture
        );
    }

    private void AssertRecordedCommandsStayOnCanonicalSource()
    {
        _commandExecutor
            .CommandTexts.Should()
            .Contain(command =>
                command.Contains("""FROM [dms].[Document] document""", StringComparison.Ordinal)
                && command.Contains("""WHERE document.[DocumentId] = @documentId""", StringComparison.Ordinal)
            );
        _commandExecutor
            .CommandTexts.Should()
            .NotContain(command => command.Contains("DocumentCache", StringComparison.OrdinalIgnoreCase));
        _commandExecutor
            .CommandTexts.Should()
            .NotContain(command =>
                command.Contains("DocumentProjectionWork", StringComparison.OrdinalIgnoreCase)
            );
    }

    private sealed class MssqlFixtureDocumentHydrator(string connectionString) : IDocumentHydrator
    {
        public async Task<HydratedPage> HydrateAsync(
            ResourceReadPlan plan,
            PageKeysetSpec keyset,
            HydrationExecutionOptions executionOptions,
            CancellationToken ct
        )
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);

            return await HydrationExecutor.ExecuteAsync(
                connection,
                plan,
                keyset,
                SqlDialect.Mssql,
                executionOptions,
                ct
            );
        }
    }

    private sealed class RecordingRelationalCommandExecutor(IRelationalCommandExecutor inner)
        : IRelationalCommandExecutor
    {
        private readonly List<string> _commandTexts = [];

        public SqlDialect Dialect => inner.Dialect;

        public IReadOnlyList<string> CommandTexts => _commandTexts;

        public Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            _commandTexts.Add(command.CommandText);
            return inner.ExecuteReaderAsync(command, readAsync, cancellationToken);
        }
    }
}

internal static class MssqlDocumentCacheMaterializerFixtureMappingSet
{
    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName StudentResource = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName StudentSchoolAssociationResource = new(
        "Ed-Fi",
        "StudentSchoolAssociation"
    );
    private static readonly QualifiedResourceName SchoolTypeDescriptorResource = new(
        "Ed-Fi",
        "SchoolTypeDescriptor"
    );
    private static readonly QualifiedResourceName EntryGradeLevelDescriptorResource = new(
        "Ed-Fi",
        "GradeLevelDescriptor"
    );
    private static readonly QualifiedResourceName EducationPlanDescriptorResource = new(
        "Ed-Fi",
        "EducationPlanDescriptor"
    );
    private static readonly QualifiedResourceName MembershipTypeDescriptorResource = new(
        "Sample",
        "MembershipTypeDescriptor"
    );

    public static MappingSet CreateDescriptorFixture()
    {
        var descriptorKey = new ResourceKeyEntry(13, SchoolTypeDescriptorResource, "1.0", false);
        var descriptorModel = CreateConcreteModel(
            descriptorKey,
            ResourceStorageKind.SharedDescriptorTable,
            CreateDescriptorRelationalModel(SchoolTypeDescriptorResource)
        );

        return CreateMappingSet(
            effectiveSchemaHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            concreteModels: [descriptorModel],
            readPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>()
        );
    }

    public static MappingSet CreateExtensionFixture()
    {
        var studentSchoolAssociationReadPlan = new ReadPlanCompiler(SqlDialect.Mssql).Compile(
            CreateStudentSchoolAssociationRelationalModel()
        );
        var studentSchoolAssociationKey = new ResourceKeyEntry(
            310,
            StudentSchoolAssociationResource,
            "5.2.0",
            false
        );
        var schoolKey = new ResourceKeyEntry(244, SchoolResource, "5.2.0", false);
        var studentKey = new ResourceKeyEntry(282, StudentResource, "5.2.0", false);
        var membershipDescriptorKey = new ResourceKeyEntry(
            356,
            MembershipTypeDescriptorResource,
            "5.2.0",
            false
        );
        var educationPlanDescriptorKey = new ResourceKeyEntry(
            103,
            EducationPlanDescriptorResource,
            "5.2.0",
            false
        );
        var entryGradeLevelDescriptorKey = new ResourceKeyEntry(
            123,
            EntryGradeLevelDescriptorResource,
            "5.2.0",
            false
        );

        return CreateMappingSet(
            effectiveSchemaHash: "53ba4ec60123456789abcdef0123456789abcdef0123456789abcdef01234567",
            concreteModels:
            [
                CreateConcreteModel(
                    studentSchoolAssociationKey,
                    ResourceStorageKind.RelationalTables,
                    studentSchoolAssociationReadPlan.Model
                ),
                CreateConcreteModel(schoolKey, ResourceStorageKind.RelationalTables, CreateSchoolModel()),
                CreateConcreteModel(studentKey, ResourceStorageKind.RelationalTables, CreateStudentModel()),
                CreateConcreteModel(
                    membershipDescriptorKey,
                    ResourceStorageKind.SharedDescriptorTable,
                    CreateDescriptorRelationalModel(MembershipTypeDescriptorResource)
                ),
                CreateConcreteModel(
                    educationPlanDescriptorKey,
                    ResourceStorageKind.SharedDescriptorTable,
                    CreateDescriptorRelationalModel(EducationPlanDescriptorResource)
                ),
                CreateConcreteModel(
                    entryGradeLevelDescriptorKey,
                    ResourceStorageKind.SharedDescriptorTable,
                    CreateDescriptorRelationalModel(EntryGradeLevelDescriptorResource)
                ),
            ],
            readPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>
            {
                [StudentSchoolAssociationResource] = studentSchoolAssociationReadPlan,
            }
        );
    }

    public static MappingSet CreateMissingSchoolBodyFixture()
    {
        var schoolReadPlan = new ReadPlanCompiler(SqlDialect.Mssql).Compile(CreateSchoolModel());
        var schoolKey = new ResourceKeyEntry(244, SchoolResource, "5.2.0", false);

        return CreateMappingSet(
            effectiveSchemaHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            concreteModels:
            [
                CreateConcreteModel(schoolKey, ResourceStorageKind.RelationalTables, schoolReadPlan.Model),
            ],
            readPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>
            {
                [SchoolResource] = schoolReadPlan,
            }
        );
    }

    public static MappingSet CreateWithoutSchoolResourceKey()
    {
        var descriptorKey = new ResourceKeyEntry(13, SchoolTypeDescriptorResource, "1.0", false);

        return CreateMappingSet(
            effectiveSchemaHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            concreteModels:
            [
                CreateConcreteModel(
                    descriptorKey,
                    ResourceStorageKind.SharedDescriptorTable,
                    CreateDescriptorRelationalModel(SchoolTypeDescriptorResource)
                ),
            ],
            readPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>()
        );
    }

    public static MappingSet CreateSchoolResourceWithoutReadPlan()
    {
        var schoolKey = new ResourceKeyEntry(244, SchoolResource, "5.2.0", false);

        return CreateMappingSet(
            effectiveSchemaHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            concreteModels:
            [
                CreateConcreteModel(schoolKey, ResourceStorageKind.RelationalTables, CreateSchoolModel()),
            ],
            readPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>()
        );
    }

    private static MappingSet CreateMappingSet(
        string effectiveSchemaHash,
        IReadOnlyList<ConcreteResourceModel> concreteModels,
        IReadOnlyDictionary<QualifiedResourceName, ResourceReadPlan> readPlansByResource
    )
    {
        var resourceKeys = concreteModels
            .Select(model => model.ResourceKey)
            .OrderBy(key => key.ResourceKeyId)
            .ToArray();
        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: effectiveSchemaHash,
            ResourceKeyCount: checked((short)resourceKeys.Length),
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: resourceKeys
        );

        return new MappingSet(
            new MappingSetKey(effectiveSchema.EffectiveSchemaHash, SqlDialect.Mssql, "v1"),
            new DerivedRelationalModelSet(
                effectiveSchema,
                SqlDialect.Mssql,
                ProjectSchemasInEndpointOrder: [],
                ConcreteResourcesInNameOrder:
                [
                    .. concreteModels.OrderBy(
                        model =>
                            model.ResourceKey.Resource.ProjectName
                            + "."
                            + model.ResourceKey.Resource.ResourceName,
                        StringComparer.Ordinal
                    ),
                ],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: readPlansByResource,
            ResourceKeyIdByResource: resourceKeys.ToDictionary(key => key.Resource, key => key.ResourceKeyId),
            ResourceKeyById: resourceKeys.ToDictionary(key => key.ResourceKeyId),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static ConcreteResourceModel CreateConcreteModel(
        ResourceKeyEntry resourceKey,
        ResourceStorageKind storageKind,
        RelationalResourceModel relationalModel
    ) => new(resourceKey, storageKind, relationalModel);

    private static RelationalResourceModel CreateSchoolModel()
    {
        var root = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            JsonPath("$"),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                ParentDocumentIdColumn(),
                ScalarColumn("SchoolId", ScalarKind.Int32, "$.schoolId"),
                ScalarColumn("NameOfInstitution", ScalarKind.String, "$.nameOfInstitution"),
            ],
            []
        )
        {
            IdentityMetadata = RootIdentityMetadata(),
        };

        return new RelationalResourceModel(
            SchoolResource,
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
    }

    private static RelationalResourceModel CreateStudentModel()
    {
        var root = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "Student"),
            JsonPath("$"),
            new TableKey(
                "PK_Student",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                ParentDocumentIdColumn(),
                ScalarColumn("StudentUniqueId", ScalarKind.String, "$.studentUniqueId"),
                ScalarColumn("FirstName", ScalarKind.String, "$.firstName"),
                ScalarColumn("LastSurname", ScalarKind.String, "$.lastSurname"),
            ],
            []
        )
        {
            IdentityMetadata = RootIdentityMetadata(),
        };

        return new RelationalResourceModel(
            StudentResource,
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
    }

    private static RelationalResourceModel CreateStudentSchoolAssociationRelationalModel()
    {
        var root = CreateStudentSchoolAssociationRootTable();
        var educationPlan = CreateEducationPlanTable();
        var extension = CreateStudentSchoolAssociationExtensionTable();

        return new RelationalResourceModel(
            StudentSchoolAssociationResource,
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            root,
            [root, educationPlan, extension],
            CreateStudentSchoolAssociationReferenceBindings(root.Table),
            CreateStudentSchoolAssociationDescriptorEdges(root.Table, educationPlan.Table, extension.Table)
        );
    }

    private static DbTableModel CreateStudentSchoolAssociationRootTable() =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
            JsonPath("$"),
            new TableKey(
                "PK_StudentSchoolAssociation",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                ParentDocumentIdColumn(),
                DocumentFkColumn("School_DocumentId", "$.schoolReference", SchoolResource),
                ScalarColumn("School_SchoolId", ScalarKind.Int32, "$.schoolReference.schoolId"),
                DocumentFkColumn("Student_DocumentId", "$.studentReference", StudentResource),
                ScalarColumn(
                    "Student_StudentUniqueId",
                    ScalarKind.String,
                    "$.studentReference.studentUniqueId"
                ),
                DescriptorColumn(
                    "EntryGradeLevelDescriptor_DescriptorId",
                    "$.entryGradeLevelDescriptor",
                    EntryGradeLevelDescriptorResource
                ),
                ScalarColumn("EntryDate", ScalarKind.Date, "$.entryDate"),
                ScalarColumn("PrimarySchool", ScalarKind.Boolean, "$.primarySchool"),
            ],
            []
        )
        {
            IdentityMetadata = RootIdentityMetadata(),
        };

    private static DbTableModel CreateEducationPlanTable() =>
        new(
            new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociationEducationPlan"),
            JsonPath(
                "$.educationPlans[*]",
                new JsonPathSegment.Property("educationPlans"),
                new JsonPathSegment.AnyArrayElement()
            ),
            new TableKey(
                "PK_StudentSchoolAssociationEducationPlan",
                [
                    new DbKeyColumn(
                        new DbColumnName("StudentSchoolAssociation_DocumentId"),
                        ColumnKind.ParentKeyPart
                    ),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
            [
                CollectionItemIdColumn(),
                ParentDocumentIdColumn("StudentSchoolAssociation_DocumentId"),
                OrdinalColumn(),
                DescriptorColumn(
                    "EducationPlanDescriptor_DescriptorId",
                    "$.educationPlans[*].educationPlanDescriptor",
                    EducationPlanDescriptorResource
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("StudentSchoolAssociation_DocumentId")],
                [new DbColumnName("StudentSchoolAssociation_DocumentId")],
                []
            ),
        };

    private static DbTableModel CreateStudentSchoolAssociationExtensionTable() =>
        new(
            new DbTableName(new DbSchemaName("sample"), "StudentSchoolAssociationExtension"),
            JsonPath(
                "$._ext.sample",
                new JsonPathSegment.Property("_ext"),
                new JsonPathSegment.Property("sample")
            ),
            new TableKey(
                "PK_StudentSchoolAssociationExtension",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                ParentDocumentIdColumn(),
                DescriptorColumn(
                    "MembershipTypeDescriptor_DescriptorId",
                    "$._ext.sample.membershipTypeDescriptor",
                    MembershipTypeDescriptorResource
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.RootExtension,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                []
            ),
        };

    private static IReadOnlyList<DocumentReferenceBinding> CreateStudentSchoolAssociationReferenceBindings(
        DbTableName rootTable
    )
    {
        var schoolReferencePath = JsonPath(
            "$.schoolReference",
            new JsonPathSegment.Property("schoolReference")
        );
        var schoolIdPath = JsonPath(
            "$.schoolReference.schoolId",
            new JsonPathSegment.Property("schoolReference"),
            new JsonPathSegment.Property("schoolId")
        );
        var studentReferencePath = JsonPath(
            "$.studentReference",
            new JsonPathSegment.Property("studentReference")
        );
        var studentUniqueIdPath = JsonPath(
            "$.studentReference.studentUniqueId",
            new JsonPathSegment.Property("studentReference"),
            new JsonPathSegment.Property("studentUniqueId")
        );

        return
        [
            new DocumentReferenceBinding(
                IsIdentityComponent: true,
                ReferenceObjectPath: schoolReferencePath,
                Table: rootTable,
                FkColumn: new DbColumnName("School_DocumentId"),
                TargetResource: SchoolResource,
                IdentityBindings:
                [
                    new ReferenceIdentityBinding(
                        IdentityJsonPath: schoolIdPath,
                        ReferenceJsonPath: schoolIdPath,
                        Column: new DbColumnName("School_SchoolId")
                    ),
                ]
            ),
            new DocumentReferenceBinding(
                IsIdentityComponent: true,
                ReferenceObjectPath: studentReferencePath,
                Table: rootTable,
                FkColumn: new DbColumnName("Student_DocumentId"),
                TargetResource: StudentResource,
                IdentityBindings:
                [
                    new ReferenceIdentityBinding(
                        IdentityJsonPath: studentUniqueIdPath,
                        ReferenceJsonPath: studentUniqueIdPath,
                        Column: new DbColumnName("Student_StudentUniqueId")
                    ),
                ]
            ),
        ];
    }

    private static IReadOnlyList<DescriptorEdgeSource> CreateStudentSchoolAssociationDescriptorEdges(
        DbTableName rootTable,
        DbTableName educationPlanTable,
        DbTableName extensionTable
    ) =>
        [
            new(
                IsIdentityComponent: false,
                DescriptorValuePath: JsonPath(
                    "$.entryGradeLevelDescriptor",
                    new JsonPathSegment.Property("entryGradeLevelDescriptor")
                ),
                Table: rootTable,
                FkColumn: new DbColumnName("EntryGradeLevelDescriptor_DescriptorId"),
                DescriptorResource: EntryGradeLevelDescriptorResource
            ),
            new(
                IsIdentityComponent: false,
                DescriptorValuePath: JsonPath(
                    "$.educationPlans[*].educationPlanDescriptor",
                    new JsonPathSegment.Property("educationPlans"),
                    new JsonPathSegment.AnyArrayElement(),
                    new JsonPathSegment.Property("educationPlanDescriptor")
                ),
                Table: educationPlanTable,
                FkColumn: new DbColumnName("EducationPlanDescriptor_DescriptorId"),
                DescriptorResource: EducationPlanDescriptorResource
            ),
            new(
                IsIdentityComponent: false,
                DescriptorValuePath: JsonPath(
                    "$._ext.sample.membershipTypeDescriptor",
                    new JsonPathSegment.Property("_ext"),
                    new JsonPathSegment.Property("sample"),
                    new JsonPathSegment.Property("membershipTypeDescriptor")
                ),
                Table: extensionTable,
                FkColumn: new DbColumnName("MembershipTypeDescriptor_DescriptorId"),
                DescriptorResource: MembershipTypeDescriptorResource
            ),
        ];

    private static RelationalResourceModel CreateDescriptorRelationalModel(QualifiedResourceName resource)
    {
        var descriptorTable = new DbTableModel(
            new DbTableName(new DbSchemaName("dms"), "Descriptor"),
            JsonPath("$"),
            new TableKey(
                "PK_Descriptor",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [ParentDocumentIdColumn()],
            []
        )
        {
            IdentityMetadata = RootIdentityMetadata(),
        };

        return new RelationalResourceModel(
            resource,
            new DbSchemaName("dms"),
            ResourceStorageKind.SharedDescriptorTable,
            descriptorTable,
            [descriptorTable],
            [],
            []
        );
    }

    private static DbColumnModel ParentDocumentIdColumn(string name = "DocumentId") =>
        new(
            new DbColumnName(name),
            ColumnKind.ParentKeyPart,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );

    private static DbColumnModel CollectionItemIdColumn() =>
        new(
            new DbColumnName("CollectionItemId"),
            ColumnKind.CollectionKey,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );

    private static DbColumnModel OrdinalColumn() =>
        new(
            new DbColumnName("Ordinal"),
            ColumnKind.Ordinal,
            new RelationalScalarType(ScalarKind.Int32),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );

    private static DbColumnModel ScalarColumn(string name, ScalarKind kind, string path) =>
        new(
            new DbColumnName(name),
            ColumnKind.Scalar,
            new RelationalScalarType(kind),
            IsNullable: true,
            SourceJsonPath: JsonPath(path),
            TargetResource: null
        );

    private static DbColumnModel DocumentFkColumn(
        string name,
        string path,
        QualifiedResourceName targetResource
    ) =>
        new(
            new DbColumnName(name),
            ColumnKind.DocumentFk,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: true,
            SourceJsonPath: JsonPath(path),
            TargetResource: targetResource
        );

    private static DbColumnModel DescriptorColumn(
        string name,
        string path,
        QualifiedResourceName targetResource
    ) =>
        new(
            new DbColumnName(name),
            ColumnKind.DescriptorFk,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: true,
            SourceJsonPath: JsonPath(path),
            TargetResource: targetResource
        );

    private static DbTableIdentityMetadata RootIdentityMetadata() =>
        new(DbTableKind.Root, [new DbColumnName("DocumentId")], [new DbColumnName("DocumentId")], [], []);

    private static JsonPathExpression JsonPath(string canonical, params JsonPathSegment[] segments)
    {
        if (segments.Length == 0 && canonical != "$")
        {
            segments = canonical
                .TrimStart('$', '.')
                .Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Select<string, JsonPathSegment>(segment => new JsonPathSegment.Property(segment))
                .ToArray();
        }

        return new JsonPathExpression(canonical, segments);
    }
}
