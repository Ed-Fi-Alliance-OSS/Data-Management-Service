// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
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

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard3)]
public class Given_MssqlDescriptorWriteHandler
{
    private MssqlReferenceResolverTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _database = await MssqlReferenceResolverTestDatabase.CreateProvisionedAsync();
    }

    [SetUp]
    public async Task Setup()
    {
        await _database.ResetAsync();
        await _database.SeedAsync();
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
    public async Task It_updates_tracking_stamps_when_descriptor_representation_changes()
    {
        var handler = ResolveHandler();

        var createRequest = CreatePostRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Regular",
                "shortDescription": "Regular"
            }
            """
        );
        var insertResult = await handler.HandlePostAsync(createRequest);
        var documentUuid = ((UpsertResult.InsertSuccess)insertResult).NewDocumentUuid;

        var stampsAfterInsert = await ReadTrackingStampsAsync(documentUuid);

        var putRequest = CreatePutRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            documentUuid,
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Regular",
                "shortDescription": "Regular School",
                "description": "Updated description"
            }
            """
        );
        await handler.HandlePutAsync(putRequest);

        var stampsAfterUpdate = await ReadTrackingStampsAsync(documentUuid);

        stampsAfterUpdate.ContentVersion.Should().BeGreaterThan(stampsAfterInsert.ContentVersion);
        stampsAfterUpdate.ContentLastModifiedAt.Should().BeOnOrAfter(stampsAfterInsert.ContentLastModifiedAt);
    }

    [Test]
    public async Task It_preserves_tracking_stamps_on_no_op_put()
    {
        var handler = ResolveHandler();

        var createRequest = CreatePostRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Regular",
                "shortDescription": "Regular"
            }
            """
        );
        var insertResult = await handler.HandlePostAsync(createRequest);
        var documentUuid = ((UpsertResult.InsertSuccess)insertResult).NewDocumentUuid;

        var stampsAfterInsert = await ReadTrackingStampsAsync(documentUuid);

        var putRequest = CreatePutRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            documentUuid,
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Regular",
                "shortDescription": "Regular"
            }
            """
        );
        await handler.HandlePutAsync(putRequest);

        var stampsAfterNoOp = await ReadTrackingStampsAsync(documentUuid);

        stampsAfterNoOp.ContentVersion.Should().Be(stampsAfterInsert.ContentVersion);
        stampsAfterNoOp.ContentLastModifiedAt.Should().Be(stampsAfterInsert.ContentLastModifiedAt);
    }

    // ── Helper methods ──────────────────────────────────────────────────

    private async Task<DocumentTrackingStamps> ReadTrackingStampsAsync(DocumentUuid documentUuid)
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [ContentVersion], [ContentLastModifiedAt]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """;
        command.Parameters.Add(new SqlParameter("@documentUuid", documentUuid.Value));
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("document should exist");

        return new DocumentTrackingStamps(reader.GetInt64(0), reader.GetDateTime(1));
    }

    private sealed record DocumentTrackingStamps(long ContentVersion, DateTime ContentLastModifiedAt);

    private IServiceScope CreateConfiguredScope()
    {
        var scope = _serviceProvider.CreateScope();
        var instanceSelection = scope.ServiceProvider.GetRequiredService<IDataStoreSelection>();
        instanceSelection.SetSelectedDataStore(
            new DataStore(
                Id: 1,
                DataStoreType: "test",
                Name: "MssqlDescriptorWriteIntegration",
                ConnectionString: _database.ConnectionString,
                RouteContext: []
            )
        );

        return scope;
    }

    private IDescriptorWriteHandler ResolveHandler()
    {
        var scope = CreateConfiguredScope();

        return scope.ServiceProvider.GetRequiredService<IDescriptorWriteHandler>();
    }

    private DescriptorWriteRequest CreatePostRequest(QualifiedResourceName resource, string bodyJson)
    {
        var body = JsonNode.Parse(bodyJson)!;
        var descriptorDoc = new Core.Model.DescriptorDocument(body);
        var resourceInfo = new BaseResourceInfo(
            new ProjectName(resource.ProjectName),
            new ResourceName(resource.ResourceName),
            true
        );
        var identity = descriptorDoc.ToDocumentIdentity();
        var referentialId = Core.Extraction.ReferentialIdCalculator.ReferentialIdFrom(resourceInfo, identity);

        return new DescriptorWriteRequest(
            _database.MappingSet,
            resource,
            body,
            new DocumentUuid(Guid.NewGuid()),
            referentialId,
            new TraceId("test-trace")
        );
    }

    private DescriptorWriteRequest CreatePutRequest(
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        string bodyJson
    )
    {
        return new DescriptorWriteRequest(
            _database.MappingSet,
            resource,
            JsonNode.Parse(bodyJson)!,
            documentUuid,
            referentialId: null,
            new TraceId("test-trace")
        );
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();

        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddTestReadableProfileProjector();
        services.AddMssqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }
}
