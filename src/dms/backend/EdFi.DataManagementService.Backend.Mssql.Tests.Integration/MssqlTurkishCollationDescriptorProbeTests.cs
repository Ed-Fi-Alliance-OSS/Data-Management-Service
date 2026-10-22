// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// SQL Server evidence that every descriptor probe surface folds its input under the identity collation
/// inside <c>LOWER</c>, so the database default never decides a descriptor's identity. ds-5.2 is provisioned
/// into a <c>Turkish_100_CS_AS</c> database, where an unqualified <c>LOWER(N'I')</c> yields dotless
/// <c>i</c> (U+0131) while the stored <c>UriLowered</c> holds <c>i</c>.
/// </summary>
/// <remarks>
/// Each stored descriptor carries an uppercase ASCII <c>I</c> and each probe input is a case variant that
/// keeps one (<c>INTERIM</c> against <c>Interim</c>). A lowercase-<c>i</c> input lowers to <c>i</c> under
/// either collation, so it would pass even with the inner <c>COLLATE</c> removed. Every test seeds its own
/// descriptor, so the fixture needs no per-test reset.
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard3)]
public class Given_A_Mssql_Turkish_Default_Database_With_Descriptor_Probes
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";
    private const string TurkishCollation = "Turkish_100_CS_AS";
    private const long SchoolId = 255901;
    private const string ProgramTypeNamespace = "uri://ed-fi.org/ProgramTypeDescriptor";

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName ProgramResource = new("Ed-Fi", "Program");
    private static readonly QualifiedResourceName StudentProgramAssociationResource = new(
        "Ed-Fi",
        "StudentProgramAssociation"
    );
    private static readonly QualifiedResourceName ProgramTypeDescriptorResource = new(
        "Ed-Fi",
        "ProgramTypeDescriptor"
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private long _schoolDocumentId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        if (!await MssqlTestDatabaseHelper.CollationExistsAsync(TurkishCollation))
        {
            Assert.Ignore($"SQL Server instance does not support collation '{TurkishCollation}'.");
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(
            _fixture.GeneratedDdl,
            databaseCollation: TurkishCollation
        );
        _serviceProvider = CreateServiceProvider();

        _schoolDocumentId = await InsertDocumentAsync(SchoolResource, Guid.NewGuid());
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[School] ([DocumentId], [NameOfInstitution], [SchoolId])
            VALUES (@documentId, N'Turkish Collation High School', @schoolId);
            """,
            new SqlParameter("@documentId", _schoolDocumentId),
            new SqlParameter("@schoolId", SchoolId)
        );
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task It_keeps_the_turkish_database_default()
    {
        (
            await _database.ExecuteScalarAsync<string>(
                "SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128));"
            )
        )
            .Should()
            .Be(TurkishCollation);
    }

    [Test]
    public async Task It_resolves_the_descriptor_post_target_for_an_uppercase_I_case_variant()
    {
        SeededDescriptor seeded = await InsertDescriptorAsync("INTERIM PROBE");

        RelationalWriteTargetLookupResult target = await WithSessionAsync(session =>
            RelationalWriteTargetLookupSupport.ResolveDescriptorForPostAsync(
                session.CreateCommandExecutor(),
                _mappingSet,
                ProgramTypeDescriptorResource,
                $"{ProgramTypeNamespace}#Interim Probe",
                new DocumentUuid(Guid.NewGuid())
            )
        );

        target
            .Should()
            .BeOfType<RelationalWriteTargetLookupResult.ExistingDocument>()
            .Which.DocumentId.Should()
            .Be(seeded.DocumentId);
    }

    [Test]
    public async Task It_updates_the_stored_descriptor_on_a_post_of_an_uppercase_I_case_variant()
    {
        SeededDescriptor seeded = await InsertDescriptorAsync("INTERIM POST");
        int descriptorCountBefore = await CountDescriptorsAsync();

        await using AsyncServiceScope scope = CreateConfiguredScope();
        // A probe miss would insert a second row and violate UX_Descriptor_UriLowered_ResourceKeyId.
        UpsertResult result = await scope
            .ServiceProvider.GetRequiredService<IDescriptorWriteHandler>()
            .HandlePostWithSamePolicyForCreateAndUpdateAsync(
                new DescriptorWriteRequest(
                    _mappingSet,
                    ProgramTypeDescriptorResource,
                    JsonNode.Parse(
                        $$"""
                        {
                            "namespace": "{{ProgramTypeNamespace}}",
                            "codeValue": "Interim Post",
                            "shortDescription": "Updated through the case variant"
                        }
                        """
                    )!,
                    new DocumentUuid(Guid.NewGuid()),
                    new TraceId("mssql-turkish-descriptor-post")
                )
            );

        result
            .Should()
            .BeOfType<UpsertResult.UpdateSuccess>()
            .Which.ExistingDocumentUuid.Should()
            .Be(seeded.DocumentUuid);
        (await CountDescriptorsAsync()).Should().Be(descriptorCountBefore);
    }

    [Test]
    public async Task It_resolves_a_descriptor_reference_to_an_uppercase_I_case_variant()
    {
        SeededDescriptor seeded = await InsertDescriptorAsync("INTERIM REFERENCE");

        ResolvedReferenceSet result = await ResolveReferencesAsync(
            [],
            [
                new DescriptorReference(
                    new BaseResourceInfo(
                        new ProjectName(ProgramTypeDescriptorResource.ProjectName),
                        new ResourceName(ProgramTypeDescriptorResource.ResourceName),
                        true
                    ),
                    Identity(
                        (
                            DocumentIdentity.DescriptorIdentityJsonPath.Value,
                            $"{ProgramTypeNamespace}#Interim Reference"
                        )
                    ),
                    new JsonPath("$.programTypeDescriptor")
                ),
            ]
        );

        result.HasFailures.Should().BeFalse();
        result
            .SuccessfulDescriptorReferencesByPath[new JsonPath("$.programTypeDescriptor")]
            .DocumentId.Should()
            .Be(seeded.DocumentId);
    }

    [Test]
    public async Task It_resolves_a_descriptor_valued_identity_with_an_uppercase_I_case_variant()
    {
        SeededDescriptor seeded = await InsertDescriptorAsync("INTERIM IDENTITY");
        long programDocumentId = await InsertDocumentAsync(ProgramResource, Guid.NewGuid());
        await InsertProgramRootAsync(programDocumentId, seeded.DocumentId, "Interim Identity Program");

        ResolvedReferenceSet result = await ResolveReferencesAsync(
            [
                new DocumentReference(
                    new BaseResourceInfo(
                        new ProjectName(ProgramResource.ProjectName),
                        new ResourceName(ProgramResource.ResourceName),
                        false
                    ),
                    Identity(
                        (
                            "$.educationOrganizationReference.educationOrganizationId",
                            SchoolId.ToString(CultureInfo.InvariantCulture)
                        ),
                        ("$.programName", "Interim Identity Program"),
                        ("$.programTypeDescriptor", $"{ProgramTypeNamespace}#Interim Identity")
                    ),
                    new JsonPath("$.programReference")
                ),
            ],
            []
        );

        result.HasFailures.Should().BeFalse();
        result
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.programReference")]
            .DocumentId.Should()
            .Be(programDocumentId);
    }

    [Test]
    public async Task It_filters_descriptor_queries_by_an_uppercase_I_case_variant()
    {
        SeededDescriptor seeded = await InsertDescriptorAsync("INTERIM QUERY");

        await using AsyncServiceScope scope = CreateConfiguredScope();
        QueryResult result = await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .QueryDocuments(
                new RelationalQueryRequest(
                    ResourceInfo: DescriptorReadIntegrationTestSupport.CreateResourceInfo(
                        _fixture.EffectiveSchemaSet,
                        "ed-fi",
                        ProgramTypeDescriptorResource.ResourceName
                    ),
                    AuthorizationContext: new RelationalAuthorizationContext([]),
                    MappingSet: _mappingSet,
                    QueryElements:
                    [
                        new QueryElement(
                            "codeValue",
                            [new JsonPath("$.codeValue")],
                            "Interim Query",
                            "string"
                        ),
                    ],
                    AuthorizationStrategyEvaluators: [],
                    Paging: new CollectionPaging.Traditional(
                        new PaginationParameters(
                            Limit: 25,
                            Offset: 0,
                            TotalCount: false,
                            MaximumPageSize: 500
                        )
                    ),
                    TraceId: new TraceId("mssql-turkish-descriptor-query"),
                    PageOrderingMode: PageOrderingMode.DocumentId
                )
            );

        result
            .Should()
            .BeOfType<QueryResult.QuerySuccess>()
            .Which.EdfiDocs.Select(document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(seeded.DocumentUuid.Value.ToString("D"));
    }

    [Test]
    public async Task It_suppresses_a_deleted_descriptor_recreated_as_an_uppercase_I_case_variant()
    {
        long windowStart = await NextChangeVersionAsync();
        SeededDescriptor deleted = await InsertDescriptorAsync("INTERIM DELETES");

        await DeleteDescriptorAsync(deleted.DocumentId);
        await InsertDescriptorAsync("Interim Deletes");

        TrackedChangeQueryResult result = await QueryDeletesAsync(
            ProgramTypeDescriptorResource,
            windowStart,
            strategies: []
        );

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0L);
    }

    [Test]
    public async Task It_suppresses_a_deleted_program_recreated_on_an_uppercase_I_case_variant_descriptor()
    {
        long windowStart = await NextChangeVersionAsync();
        long programDocumentId = await DeleteProgramAndRecreateItsDescriptorAsync(
            "INTERIM PROGRAM",
            "Interim Program"
        );
        long recreatedDescriptorDocumentId = await ReadDescriptorDocumentIdAsync("Interim Program");
        await InsertProgramRootAsync(programDocumentId, recreatedDescriptorDocumentId, "Interim Program");

        TrackedChangeQueryResult result = await QueryDeletesAsync(
            ProgramResource,
            windowStart,
            strategies: []
        );

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0L);
    }

    [Test]
    public async Task It_authorizes_a_deleted_program_through_a_descriptor_seek_of_an_uppercase_I_case_variant()
    {
        const string StrategyName = "ProgramTypeDescriptorWithTurkishInterimSeek";
        long windowStart = await NextChangeVersionAsync();
        await DeleteProgramAndRecreateItsDescriptorAsync("INTERIM SEEK", "Interim Seek");
        await _database.ExecuteNonQueryAsync($"DROP VIEW IF EXISTS [auth].[{StrategyName}];");
        await _database.ExecuteNonQueryAsync(
            $"""
            CREATE VIEW [auth].[{StrategyName}] AS
            SELECT descriptor.[DocumentId]
            FROM [dms].[Descriptor] descriptor
            WHERE descriptor.[ResourceKeyId] = {_mappingSet.ResourceKeyIdByResource[
                ProgramTypeDescriptorResource
            ]};
            """
        );

        // The Program tombstone carries the deleted spelling; only the lowered-URI seek reaches the live
        // recreated descriptor the view authorizes.
        TrackedChangeQueryResult result = await QueryDeletesAsync(
            ProgramResource,
            windowStart,
            strategies: [StrategyName]
        );

        result.AuthorizationFailure.Should().BeNull();
        result.TotalCount.Should().Be(1L);
        result.Items.Should().ContainSingle().Subject!["keyValues"]!["programTypeDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be($"{ProgramTypeNamespace}#INTERIM SEEK");
    }

    private sealed record SeededDescriptor(long DocumentId, DocumentUuid DocumentUuid);

    /// <summary>
    /// Inserts a Program on a new descriptor stored as <paramref name="storedCodeValue"/>, deletes the Program
    /// root and the descriptor, and recreates the descriptor as <paramref name="recreatedCodeValue"/>. Returns
    /// the Program's DocumentId. The Program is named after the recreated code value.
    /// </summary>
    private async Task<long> DeleteProgramAndRecreateItsDescriptorAsync(
        string storedCodeValue,
        string recreatedCodeValue
    )
    {
        SeededDescriptor stored = await InsertDescriptorAsync(storedCodeValue);
        long programDocumentId = await InsertDocumentAsync(ProgramResource, Guid.NewGuid());
        await InsertProgramRootAsync(programDocumentId, stored.DocumentId, recreatedCodeValue);
        await _database.ExecuteNonQueryAsync(
            "DELETE FROM [edfi].[Program] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", programDocumentId)
        );
        await DeleteDescriptorAsync(stored.DocumentId);
        await InsertDescriptorAsync(recreatedCodeValue);

        return programDocumentId;
    }

    private ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddMssqlBackendIntegrationTestServices();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private AsyncServiceScope CreateConfiguredScope()
    {
        AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlTurkishDescriptorProbes",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        return scope;
    }

    private async Task<TResult> WithSessionAsync<TResult>(Func<IRelationalWriteSession, Task<TResult>> action)
    {
        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        return await action(new RelationalWriteSession(connection, transaction));
    }

    private Task<ResolvedReferenceSet> ResolveReferencesAsync(
        IReadOnlyList<DocumentReference> documents,
        IReadOnlyList<DescriptorReference> descriptors
    ) =>
        WithSessionAsync(session =>
            new NaturalKeyReferenceResolver(
                new MssqlNaturalKeyLookupCommandBuilder(),
                new ResolvedDocumentReferenceMapFactory()
            ).ResolveAsync(
                new ReferenceResolverRequest(
                    _mappingSet,
                    StudentProgramAssociationResource,
                    documents,
                    descriptors
                ),
                session.CreateCommandExecutor()
            )
        );

    private static DocumentIdentity Identity(params (string Path, string Value)[] elements) =>
        new([
            .. elements.Select(element => new DocumentIdentityElement(
                new JsonPath(element.Path),
                element.Value
            )),
        ]);

    private async Task<TrackedChangeQueryResult> QueryDeletesAsync(
        QualifiedResourceName resource,
        long windowStart,
        IReadOnlyList<string> strategies
    )
    {
        bool isDescriptor = resource == ProgramTypeDescriptorResource;
        ConcreteResourceModel resourceModel = _mappingSet.Model.ConcreteResourcesInNameOrder.Single(model =>
            model.RelationalModel.Resource == resource
        );
        TrackedChangeTableInfo trackedTable = _mappingSet.Model.TrackedChangeTablesInNameOrder.Single(table =>
            isDescriptor
                ? table.Kind is TrackedChangeTableKind.SharedDescriptor
                : table.SourceTable == resourceModel.RelationalModel.Root.Table
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
            new TurkishCollationTrackedChangeQueryRequest(
                ResourceInfo: new ResourceInfo(
                    new ProjectName(resource.ProjectName),
                    new ResourceName(resource.ResourceName),
                    isDescriptor,
                    new SemVer("5.2.0"),
                    AllowIdentityUpdates: false
                ),
                Operation: ChangeQueryEndpointOperation.Deletes,
                PaginationParameters: new PaginationParameters(
                    Limit: 25,
                    Offset: 0,
                    TotalCount: true,
                    MaximumPageSize: 500
                ),
                ChangeVersionRange: new ChangeVersionRange(windowStart, long.MaxValue),
                TraceId: new TraceId($"mssql-turkish-deletes-{resource.ResourceName}"),
                AuthorizationContext: new RelationalAuthorizationContext([], []),
                AuthorizationStrategyEvaluators:
                [
                    .. strategies.Select(name => new AuthorizationStrategyEvaluator(
                        name,
                        [],
                        FilterOperator.And
                    )),
                ],
                MappingSet: _mappingSet,
                ResourceModel: resourceModel,
                TrackedChangeTable: trackedTable
            )
        );
    }

    private async Task<long> NextChangeVersionAsync() =>
        await _database.ExecuteScalarAsync<long>("SELECT [dms].[GetMaxChangeVersion]() + 1;");

    private async Task<int> CountDescriptorsAsync() =>
        await _database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM [dms].[Descriptor];");

    private async Task<long> InsertDocumentAsync(QualifiedResourceName resource, Guid documentUuid)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @inserted TABLE ([DocumentId] bigint NOT NULL);

            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT INSERTED.[DocumentId] INTO @inserted ([DocumentId])
            VALUES (@documentUuid, @resourceKeyId);

            SELECT [DocumentId] FROM @inserted;
            """,
            new SqlParameter("@documentUuid", documentUuid),
            new SqlParameter("@resourceKeyId", _mappingSet.ResourceKeyIdByResource[resource])
        );
    }

    private async Task<SeededDescriptor> InsertDescriptorAsync(string codeValue)
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        long documentId = await InsertDocumentAsync(ProgramTypeDescriptorResource, documentUuid.Value);
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [dms].[Descriptor] ([DocumentId], [ResourceKeyId], [Namespace], [CodeValue], [ShortDescription])
            VALUES (@documentId, @resourceKeyId, @namespace, @codeValue, @codeValue);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter(
                "@resourceKeyId",
                _mappingSet.ResourceKeyIdByResource[ProgramTypeDescriptorResource]
            ),
            new SqlParameter("@namespace", ProgramTypeNamespace),
            new SqlParameter("@codeValue", codeValue)
        );

        return new SeededDescriptor(documentId, documentUuid);
    }

    // Reads by the exact stored spelling, so a case-variant row could never answer for it.
    private async Task<long> ReadDescriptorDocumentIdAsync(string codeValue) =>
        await _database.ExecuteScalarAsync<long>(
            """
            SELECT [DocumentId] FROM [dms].[Descriptor]
            WHERE [CodeValue] COLLATE Latin1_General_100_BIN2 = @codeValue;
            """,
            new SqlParameter("@codeValue", codeValue)
        );

    private async Task DeleteDescriptorAsync(long documentId)
    {
        await _database.ExecuteNonQueryAsync(
            "DELETE FROM [dms].[Descriptor] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", documentId)
        );
    }

    private async Task InsertProgramRootAsync(
        long documentId,
        long programTypeDescriptorDocumentId,
        string programName
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[Program] (
                [DocumentId],
                [EducationOrganization_DocumentId],
                [EducationOrganization_EducationOrganizationId],
                [ProgramTypeDescriptor_DescriptorId],
                [ProgramName]
            )
            VALUES (
                @documentId,
                @schoolDocumentId,
                @schoolId,
                (SELECT [DescriptorId] FROM [dms].[Descriptor] WHERE [DocumentId] = @descriptorDocumentId),
                @programName
            );
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@schoolDocumentId", _schoolDocumentId),
            new SqlParameter("@schoolId", SchoolId),
            new SqlParameter("@descriptorDocumentId", programTypeDescriptorDocumentId),
            new SqlParameter("@programName", programName)
        );
    }
}

file sealed record TurkishCollationTrackedChangeQueryRequest(
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
