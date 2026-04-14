// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Old.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_PostgresqlDescriptorWriteHandler
{
    private PostgresqlReferenceResolverTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _database = await PostgresqlReferenceResolverTestDatabase.CreateProvisionedAsync();
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
    public async Task It_inserts_a_new_descriptor_via_post()
    {
        var handler = ResolveHandler();
        var request = CreatePostRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Regular",
                "shortDescription": "Regular",
                "description": "A regular school type"
            }
            """
        );

        var result = await handler.HandlePostAsync(request);

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    [Test]
    public async Task It_updates_an_existing_descriptor_via_post_upsert()
    {
        var handler = ResolveHandler();

        // First POST creates the descriptor
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
        var createResult = await handler.HandlePostAsync(createRequest);
        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        // Second POST with same identity upserts (updates)
        var upsertRequest = CreatePostRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Regular",
                "shortDescription": "Regular School"
            }
            """
        );
        var upsertResult = await handler.HandlePostAsync(upsertRequest);

        upsertResult.Should().BeOfType<UpsertResult.UpdateSuccess>();
    }

    [Test]
    public async Task It_updates_non_identity_fields_via_put()
    {
        var handler = ResolveHandler();

        // Create descriptor first
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

        // PUT with updated description
        var putRequest = CreatePutRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            documentUuid,
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Regular",
                "shortDescription": "Regular School",
                "description": "A regular school"
            }
            """
        );
        var result = await handler.HandlePutAsync(putRequest);

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
    }

    [Test]
    public async Task It_returns_success_without_update_for_unchanged_put()
    {
        var handler = ResolveHandler();

        // Create descriptor
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

        // PUT with identical values
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
        var result = await handler.HandlePutAsync(putRequest);

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
    }

    [Test]
    public async Task It_rejects_identity_change_on_put()
    {
        var handler = ResolveHandler();

        // Create descriptor
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

        // PUT with changed CodeValue (identity change)
        var putRequest = CreatePutRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            documentUuid,
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Alternative",
                "shortDescription": "Alternative"
            }
            """
        );
        var result = await handler.HandlePutAsync(putRequest);

        result.Should().BeOfType<UpdateResult.UpdateFailureImmutableIdentity>();
    }

    [Test]
    public async Task It_deletes_a_descriptor()
    {
        var handler = ResolveHandler();

        // Create descriptor
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

        // Delete
        var result = await handler.HandleDeleteAsync(documentUuid, new TraceId("test-trace"));

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
    }

    [Test]
    public async Task It_returns_not_exists_when_deleting_nonexistent_descriptor()
    {
        var handler = ResolveHandler();

        var result = await handler.HandleDeleteAsync(
            new DocumentUuid(Guid.NewGuid()),
            new TraceId("test-trace")
        );

        result.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
    }

    [Test]
    public async Task It_returns_not_exists_for_put_on_nonexistent_document()
    {
        var handler = ResolveHandler();

        var putRequest = CreatePutRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            new DocumentUuid(Guid.NewGuid()),
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Regular",
                "shortDescription": "Regular"
            }
            """
        );
        var result = await handler.HandlePutAsync(putRequest);

        result.Should().BeOfType<UpdateResult.UpdateFailureNotExists>();
    }

    [Test]
    public async Task It_updates_tracking_stamps_when_descriptor_representation_changes()
    {
        var handler = ResolveHandler();

        // Create descriptor
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

        // PUT with changed description
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

        // Create descriptor
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

        // PUT with identical values
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

    [Test]
    public async Task It_rejects_namespace_change_on_put()
    {
        var handler = ResolveHandler();

        // Create descriptor
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

        // PUT with changed Namespace (identity change)
        var putRequest = CreatePutRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            documentUuid,
            """
            {
                "namespace": "uri://custom.org/SchoolTypeDescriptor",
                "codeValue": "Regular",
                "shortDescription": "Regular"
            }
            """
        );
        var result = await handler.HandlePutAsync(putRequest);

        result.Should().BeOfType<UpdateResult.UpdateFailureImmutableIdentity>();
    }

    // ── Helper methods ──────────────────────────────────────────────────

    private async Task<DocumentTrackingStamps> ReadTrackingStampsAsync(DocumentUuid documentUuid)
    {
        await using var dataSource = Npgsql.NpgsqlDataSource.Create(_database.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "ContentVersion", "ContentLastModifiedAt"
            FROM dms."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """;
        command.Parameters.AddWithValue("@documentUuid", documentUuid.Value);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("document should exist");

        return new DocumentTrackingStamps(reader.GetInt64(0), reader.GetDateTime(1));
    }

    private sealed record DocumentTrackingStamps(long ContentVersion, DateTime ContentLastModifiedAt);

    private IDescriptorWriteHandler ResolveHandler()
    {
        var scope = _serviceProvider.CreateScope();
        var instanceSelection = scope.ServiceProvider.GetRequiredService<IDmsInstanceSelection>();
        instanceSelection.SetSelectedDmsInstance(
            new DmsInstance(
                Id: 1,
                InstanceType: "test",
                InstanceName: "PostgresqlDescriptorWriteIntegration",
                ConnectionString: _database.ConnectionString,
                RouteContext: []
            )
        );

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

        services.AddSingleton<IHostApplicationLifetime, TestHostApplicationLifetime>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.AddPostgresqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }
}

file sealed class TestHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}
