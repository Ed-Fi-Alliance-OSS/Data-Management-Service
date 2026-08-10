// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
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
using Microsoft.Extensions.DependencyInjection;
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
        var mappingSet = DocumentCacheMaterializerFixtureMappingSet.CreateDescriptorFixture(SqlDialect.Mssql);

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
        var mappingSet = DocumentCacheMaterializerFixtureMappingSet.CreateExtensionFixture(SqlDialect.Mssql);

        var result = await CreateMaterializer().MaterializeAsync(CreateRequest(mappingSet, fixture));

        var success = result.Should().BeOfType<DocumentCacheMaterializationResult.Success>().Subject;
        AssertCandidateMatchesFixture(success.Candidate, fixture);
        AssertRecordedCommandsStayOnCanonicalSource();
    }

    [Test]
    public async Task It_materializes_the_collection_property_absence_fixture_from_real_Mssql_hydration()
    {
        var fixture = LoadFixture("school-address-property-absence");
        await SeedFixtureAsync(fixture);
        var mappingSet = DocumentCacheMaterializerFixtureMappingSet.CreateSchoolAddressFixture(
            SqlDialect.Mssql
        );

        var result = await CreateMaterializer().MaterializeAsync(CreateRequest(mappingSet, fixture));

        var success = result.Should().BeOfType<DocumentCacheMaterializationResult.Success>().Subject;
        AssertCandidateMatchesFixture(success.Candidate, fixture);
        MaterializedDocumentFixtureAssertions.AssertSchoolAddressDescriptorAbsence(
            success.Candidate.DocumentJson,
            fixture
        );
        AssertRecordedCommandsStayOnCanonicalSource();
    }

    [Test]
    public async Task It_materializes_an_ordinary_fixture_through_the_DI_registered_Mssql_target_adapter()
    {
        var fixture = LoadFixture("extension-student-school-association");
        await SeedFixtureAsync(fixture);
        var mappingSet = DocumentCacheMaterializerFixtureMappingSet.CreateExtensionFixture(SqlDialect.Mssql);
        await using var serviceProvider = CreateServiceProvider();
        using var scope = serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDocumentCacheMaterializationDataStore>()
            .Should()
            .BeOfType<MssqlDocumentCacheMaterializationDataStore>();

        var result = await scope
            .ServiceProvider.GetRequiredService<IDocumentCacheMaterializer>()
            .MaterializeAsync(CreateRequest(mappingSet, fixture, _connectionString));

        var success = result.Should().BeOfType<DocumentCacheMaterializationResult.Success>().Subject;
        AssertCandidateMatchesFixture(success.Candidate, fixture);
    }

    [Test]
    public async Task It_returns_missing_source_for_an_absent_canonical_document_row()
    {
        var fixture = LoadFixture("descriptor-school-type");
        await SeedFixtureAsync(fixture);
        var mappingSet = DocumentCacheMaterializerFixtureMappingSet.CreateDescriptorFixture(SqlDialect.Mssql);

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
        var mappingSet = DocumentCacheMaterializerFixtureMappingSet.CreateDescriptorFixture(SqlDialect.Mssql);
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
        var mappingSet = DocumentCacheMaterializerFixtureMappingSet.CreateMissingSchoolBodyFixture(
            SqlDialect.Mssql
        );

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
        var mappingSet = DocumentCacheMaterializerFixtureMappingSet.CreateWithoutSchoolResourceKey(
            SqlDialect.Mssql
        );

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
        var mappingSet = DocumentCacheMaterializerFixtureMappingSet.CreateSchoolResourceWithoutReadPlan(
            SqlDialect.Mssql
        );

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
        var lastModifiedAtParameter = command.Parameters.AddWithValue(
            "@lastModifiedAt",
            lastModifiedAt.UtcDateTime
        );
        lastModifiedAtParameter.SqlDbType = SqlDbType.DateTime2;
        command.Parameters.AddWithValue("@documentId", documentId);
        await command.ExecuteNonQueryAsync();
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IDocumentLinkSlugResolver>(
            new DeterministicLinkSlugResolver(
                new Dictionary<short, DocumentLinkSlugTriple>
                {
                    [244] = new("ed-fi", "schools", "School"),
                    [282] = new("ed-fi", "students", "Student"),
                }
            )
        );
        services.AddTestReadableProfileProjector();
        services.AddMssqlBackendIntegrationTestServices();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
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
        MaterializedDocumentFixture fixture,
        string targetConnectionString
    ) => CreateRequest(mappingSet, fixture.SourceSetup.Documents[0].DocumentId, targetConnectionString);

    private static DocumentCacheMaterializationRequest CreateRequest(
        MappingSet mappingSet,
        long documentId,
        string? targetConnectionString = null
    )
    {
        var targetKey = new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7));
        var targetContext = targetConnectionString is null
            ? new DocumentCacheMaterializationTargetContext(
                targetKey,
                mappingSet,
                DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated
            )
            : new DocumentCacheMaterializationTargetContext(
                targetKey,
                mappingSet,
                DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated,
                targetConnectionString
            );

        return new(
            targetContext,
            documentId,
            selectedRequiredContentVersion: 456,
            DocumentCacheMaterializationPurpose.Fixture,
            CancellationToken.None
        );
    }

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
