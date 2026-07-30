// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
public class Given_Postgresql_DocumentCacheMaterializer_Fixtures
{
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSource _dataSource = null!;
    private RecordingRelationalCommandExecutor _commandExecutor = null!;

    [SetUp]
    public async Task SetUp()
    {
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        _dataSource = NpgsqlDataSource.Create(_database.ConnectionString);
        _commandExecutor = new RecordingRelationalCommandExecutor(
            new PostgresqlRelationalCommandExecutor(
                async cancellationToken =>
                    (DbConnection)await _dataSource.OpenConnectionAsync(cancellationToken),
                NullLogger<PostgresqlRelationalCommandExecutor>.Instance
            )
        );
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task It_materializes_the_descriptor_fixture_from_real_Postgresql_source_rows()
    {
        var fixture = LoadFixture("descriptor-school-type");
        await SeedFixtureAsync(fixture);
        var mappingSet = DocumentCacheMaterializerFixtureMappingSet.CreateDescriptorFixture(SqlDialect.Pgsql);

        var result = await CreateMaterializer().MaterializeAsync(CreateRequest(mappingSet, fixture));

        var success = result.Should().BeOfType<DocumentCacheMaterializationResult.Success>().Subject;
        AssertCandidateMatchesFixture(success.Candidate, fixture);
        AssertRecordedCommandsStayOnCanonicalSource();
    }

    [Test]
    public async Task It_materializes_the_extension_and_nested_collection_fixture_from_real_Postgresql_hydration()
    {
        var fixture = LoadFixture("extension-student-school-association");
        await SeedFixtureAsync(fixture);
        var mappingSet = DocumentCacheMaterializerFixtureMappingSet.CreateExtensionFixture(SqlDialect.Pgsql);

        var result = await CreateMaterializer().MaterializeAsync(CreateRequest(mappingSet, fixture));

        var success = result.Should().BeOfType<DocumentCacheMaterializationResult.Success>().Subject;
        AssertCandidateMatchesFixture(success.Candidate, fixture);
        AssertRecordedCommandsStayOnCanonicalSource();
    }

    [Test]
    public async Task It_materializes_an_ordinary_fixture_through_the_DI_registered_Postgresql_target_adapter()
    {
        var fixture = LoadFixture("extension-student-school-association");
        await SeedFixtureAsync(fixture);
        var mappingSet = DocumentCacheMaterializerFixtureMappingSet.CreateExtensionFixture(SqlDialect.Pgsql);
        await using var serviceProvider = CreateServiceProvider();
        using var scope = serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDocumentCacheMaterializationDataStore>()
            .Should()
            .BeOfType<PostgresqlDocumentCacheMaterializationDataStore>();

        var result = await scope
            .ServiceProvider.GetRequiredService<IDocumentCacheMaterializer>()
            .MaterializeAsync(CreateRequest(mappingSet, fixture, _database.ConnectionString));

        var success = result.Should().BeOfType<DocumentCacheMaterializationResult.Success>().Subject;
        AssertCandidateMatchesFixture(success.Candidate, fixture);
    }

    [Test]
    public async Task It_preserves_LastModifiedAt_microsecond_precision_while_JSON_lastModifiedDate_uses_whole_second_text_without_rounding()
    {
        var fixture = LoadFixture("descriptor-school-type");
        await SeedFixtureAsync(fixture);
        var mappingSet = DocumentCacheMaterializerFixtureMappingSet.CreateDescriptorFixture(SqlDialect.Pgsql);
        var preciseLastModifiedAt = new DateTimeOffset(2026, 7, 30, 14, 15, 16, TimeSpan.Zero).AddTicks(
            9_876_540
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
    public async Task It_returns_missing_source_for_an_absent_canonical_document_row()
    {
        var fixture = LoadFixture("descriptor-school-type");
        await SeedFixtureAsync(fixture);
        var mappingSet = DocumentCacheMaterializerFixtureMappingSet.CreateDescriptorFixture(SqlDialect.Pgsql);

        var result = await CreateMaterializer()
            .MaterializeAsync(CreateRequest(mappingSet, documentId: 979999));

        result.Should().BeSameAs(DocumentCacheMaterializationResult.MissingSource.Instance);
        AssertRecordedCommandsStayOnCanonicalSource();
    }

    [Test]
    public async Task It_throws_the_fixture_projection_failure_when_source_metadata_is_stable_but_the_body_is_missing()
    {
        var fixture = LoadFixture("invariant-missing-school-body");
        await SeedFixtureAsync(fixture);
        await EnsureSchoolTableExistsAsync();
        var mappingSet = DocumentCacheMaterializerFixtureMappingSet.CreateMissingSchoolBodyFixture(
            SqlDialect.Pgsql
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
            SqlDialect.Pgsql
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
            SqlDialect.Pgsql
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
        await using var connection = await _dataSource.OpenConnectionAsync();
        await new MaterializedDocumentFixtureSeeder(
            MaterializedDocumentFixtureSqlDialect.Postgresql
        ).SeedAsync(connection, fixture);
    }

    private async Task EnsureSchoolTableExistsAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            CREATE SCHEMA IF NOT EXISTS edfi;

            CREATE TABLE IF NOT EXISTS edfi."School" (
                "DocumentId" bigint NOT NULL PRIMARY KEY,
                "SchoolId" integer NULL,
                "NameOfInstitution" varchar(1024) NULL
            );
            """,
            connection
        );
        await command.ExecuteNonQueryAsync();
    }

    private async Task UpdateContentLastModifiedAtAsync(long documentId, DateTimeOffset lastModifiedAt)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE dms."Document"
            SET "ContentLastModifiedAt" = @lastModifiedAt
            WHERE "DocumentId" = @documentId;
            """,
            connection
        );
        command.Parameters.AddWithValue("@lastModifiedAt", lastModifiedAt);
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
        services.AddPostgresqlBackendIntegrationTestServices();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private DocumentCacheMaterializer CreateMaterializer()
    {
        var materializationDataStore = new AmbientDocumentCacheMaterializationDataStore(
            _commandExecutor,
            new PostgresqlFixtureDocumentHydrator(_dataSource)
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
                command.Contains("""FROM dms."Document" document""", StringComparison.Ordinal)
                && command.Contains("""WHERE document."DocumentId" = @documentId""", StringComparison.Ordinal)
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

    private sealed class PostgresqlFixtureDocumentHydrator(NpgsqlDataSource dataSource) : IDocumentHydrator
    {
        public async Task<HydratedPage> HydrateAsync(
            ResourceReadPlan plan,
            PageKeysetSpec keyset,
            HydrationExecutionOptions executionOptions,
            CancellationToken ct
        )
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);

            return await HydrationExecutor.ExecuteAsync(
                connection,
                plan,
                keyset,
                SqlDialect.Pgsql,
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
