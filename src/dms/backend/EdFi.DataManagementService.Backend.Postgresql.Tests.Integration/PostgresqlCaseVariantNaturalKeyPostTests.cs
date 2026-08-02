// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// The PostgreSQL control for the SQL Server write-path collation delta: a case-variant string natural key
/// is still a DIFFERENT identity here, so the POST creates a second document exactly as it always did.
/// </summary>
/// <remarks>
/// The twin of <c>Given_A_Mssql_Relational_Post_With_A_Case_Variant_String_Natural_Key</c>, which measures
/// the same request resolving to an EXISTING document and being refused as an immutable-identity violation.
/// PostgreSQL's default collations are deterministic, so <c>UX_Program_NK</c> compares byte-for-byte and
/// the two spellings coexist. Pinning the unchanged side is what makes the SQL Server test readable as a
/// dialect delta rather than a behavior regression.
/// </remarks>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Post_With_A_Case_Variant_String_Natural_Key
{
    private const string StoredProgramName = "Career Readiness";
    private const string CaseVariantProgramName = "CAREER READINESS";

    private static readonly DocumentUuid StoredProgramDocumentUuid = new(
        Guid.Parse("cccc0004-0000-0000-0000-000000000001")
    );
    private static readonly DocumentUuid CaseVariantProgramDocumentUuid = new(
        Guid.Parse("cccc0004-0000-0000-0000-000000000002")
    );

    private static readonly ResourceInfo ProgramResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("Program"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileTopLevelCollectionMergeSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        _serviceProvider = CreateServiceProvider();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null!;
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null!;
        }
    }

    [Test]
    public async Task It_creates_a_second_document_for_a_case_variant_natural_key_post()
    {
        var created = await ExecuteUpsertAsync(StoredProgramName, StoredProgramDocumentUuid);
        created.Should().BeOfType<UpsertResult.InsertSuccess>();

        var caseVariant = await ExecuteUpsertAsync(CaseVariantProgramName, CaseVariantProgramDocumentUuid);

        caseVariant
            .Should()
            .BeOfType<UpsertResult.InsertSuccess>(
                "PostgreSQL compares the natural key byte-for-byte, so a case variant is a different identity"
            );

        // The SQL Server delta, stated as its negation on this dialect.
        caseVariant.Should().NotBeOfType<UpsertResult.UpsertFailureImmutableIdentity>();

        var storedNames = await ReadProgramNamesAsync();
        storedNames
            .Should()
            .BeEquivalentTo(
                [StoredProgramName, CaseVariantProgramName],
                "both spellings are distinct natural keys here, so both rows persist"
            );
    }

    private async Task<IReadOnlyList<string>> ReadProgramNamesAsync()
    {
        List<string> programNames = [];

        await using NpgsqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        // Ordered by DocumentId, not by ProgramName: collation would decide the latter and this test is
        // deliberately dialect-collation-sensitive elsewhere.
        command.CommandText = """SELECT "ProgramName" FROM edfi."Program" ORDER BY "DocumentId";""";

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            programNames.Add(reader.GetString(0));
        }

        return programNames;
    }

    private async Task<UpsertResult> ExecuteUpsertAsync(string programName, DocumentUuid documentUuid)
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "PostgresqlCaseVariantNaturalKeyPost",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var identity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.programName"), programName),
        ]);
        var documentInfo = new DocumentInfo(
            DocumentIdentity: identity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(ProgramResourceInfo, identity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );

        var upsertRequest = new UpsertRequest(
            ResourceInfo: ProgramResourceInfo,
            DocumentInfo: documentInfo,
            MappingSet: _mappingSet,
            EdfiDoc: new JsonObject { ["programName"] = programName },
            Headers: [],
            TraceId: new TraceId("pgsql-case-variant-natural-key-post"),
            DocumentUuid: documentUuid
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    private ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();

        // The production registration: the natural-key resolver wins the IReferenceResolver slot.
        services.AddPostgresqlNaturalKeyReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }
}
