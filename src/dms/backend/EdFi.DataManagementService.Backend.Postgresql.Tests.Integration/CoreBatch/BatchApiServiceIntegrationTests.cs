// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration.CoreBatch;

[TestFixture]
public class BatchApiServiceIntegrationTests : CoreBatchIntegrationTestBase
{
    [Test]
    public async Task Given_Mixed_Operations_When_All_Succeed_Commits_Once()
    {
        Guid updateId = await InsertStudentAsync("existing-update", "Initial");
        string updateEtag = await GetStudentEtagAsync(updateId);
        Guid deleteId = await InsertStudentAsync("existing-delete", "ToRemove");

        IFrontendResponse response = await ExecuteBatchAsync(
            CreateCreateOperation("batch-new", "Ada"),
            CreateUpdateOperation(updateId, updateEtag, "existing-update", "Updated"),
            CreateDeleteOperation(deleteId)
        );

        response.StatusCode.Should().Be(200);
        JsonArray body = response.Body!.AsArray();
        body.Count.Should().Be(3);

        Guid createdId = Guid.Parse(body[0]!["documentId"]!.GetValue<string>());
        JsonObject? createdDocument = await GetStudentDocumentAsync(createdId);
        createdDocument.Should().NotBeNull();
        createdDocument!["givenName"]!.GetValue<string>().Should().Be("Ada");

        JsonObject? updatedDocument = await GetStudentDocumentAsync(updateId);
        updatedDocument.Should().NotBeNull();
        updatedDocument!["givenName"]!.GetValue<string>().Should().Be("Updated");

        JsonObject? deletedDocument = await GetStudentDocumentAsync(deleteId);
        deletedDocument.Should().BeNull();
    }

    [Test]
    public async Task Given_Two_Creates_With_Different_Identities_Succeeds()
    {
        string firstStudentId = $"batch-dupe-{Guid.NewGuid():N}".Substring(0, 12);
        string secondStudentId = $"batch-dupe-{Guid.NewGuid():N}".Substring(0, 12);

        IFrontendResponse response = await ExecuteBatchAsync(
            CreateCreateOperation(firstStudentId, "First"),
            CreateCreateOperation(secondStudentId, "Second")
        );

        response.StatusCode.Should().Be(200);
        JsonArray body = response.Body!.AsArray();
        body.Count.Should().Be(2);

        Guid firstDocumentId = Guid.Parse(body[0]!["documentId"]!.GetValue<string>());
        Guid secondDocumentId = Guid.Parse(body[1]!["documentId"]!.GetValue<string>());
        firstDocumentId.Should().NotBe(secondDocumentId);

        JsonObject? firstDocument = await GetStudentDocumentAsync(firstDocumentId);
        JsonObject? secondDocument = await GetStudentDocumentAsync(secondDocumentId);

        firstDocument.Should().NotBeNull();
        secondDocument.Should().NotBeNull();

        firstDocument!["studentUniqueId"]!.GetValue<string>().Should().Be(firstStudentId);
        secondDocument!["studentUniqueId"]!.GetValue<string>().Should().Be(secondStudentId);
        firstDocument["givenName"]!.GetValue<string>().Should().Be("First");
        secondDocument["givenName"]!.GetValue<string>().Should().Be("Second");
    }

    [Test]
    public async Task Given_Creates_With_Descriptor_References_Succeeds()
    {
        string firstStudentId = $"descriptor-{Guid.NewGuid():N}".Substring(0, 20);
        string secondStudentId = $"descriptor-{Guid.NewGuid():N}".Substring(0, 20);

        IFrontendResponse response = await ExecuteBatchAsync(
            CreateCreateOperationWithDescriptors(firstStudentId, "Alpha"),
            CreateCreateOperationWithDescriptors(secondStudentId, "Beta")
        );

        response.StatusCode.Should().Be(200);
        JsonArray body = response.Body!.AsArray();
        body.Count.Should().Be(2);
    }

    [Test]
    public async Task Given_Create_Update_Delete_Create_On_Same_Resource_Succeeds()
    {
        string initialStudentId = $"batch-chain-{Guid.NewGuid():N}".Substring(0, 12);
        string secondStudentId = $"batch-chain-{Guid.NewGuid():N}".Substring(0, 12);

        IFrontendResponse response = await ExecuteBatchAsync(
            CreateCreateOperation(initialStudentId, "First"),
            CreateUpdateByNaturalKeyOperation(
                naturalKeyValue: initialStudentId,
                etag: null,
                studentUniqueId: initialStudentId,
                givenName: "Updated"
            ),
            CreateDeleteByNaturalKeyOperation(initialStudentId),
            CreateCreateOperation(secondStudentId, "Second")
        );

        response.StatusCode.Should().Be(200);
        JsonArray body = response.Body!.AsArray();
        body.Count.Should().Be(4);

        Guid firstDocumentId = Guid.Parse(body[0]!["documentId"]!.GetValue<string>());
        Guid secondDocumentId = Guid.Parse(body[3]!["documentId"]!.GetValue<string>());

        JsonObject? deletedDocument = await GetStudentDocumentAsync(firstDocumentId);
        deletedDocument.Should().BeNull("first document should be deleted before final create");

        JsonObject? secondDocument = await GetStudentDocumentAsync(secondDocumentId);
        secondDocument.Should().NotBeNull();
        secondDocument!["studentUniqueId"]!.GetValue<string>().Should().Be(secondStudentId);
        secondDocument["givenName"]!.GetValue<string>().Should().Be("Second");
    }

    [Test]
    public async Task Given_Natural_Key_Update_With_Mismatched_Identity_When_Resource_Immutable_Returns_400()
    {
        Guid documentId = await InsertStudentAsync("immutable-match", "Original");
        string etag = await GetStudentEtagAsync(documentId);

        IFrontendResponse response = await ExecuteBatchAsync(
            CreateUpdateByNaturalKeyOperation(
                naturalKeyValue: "immutable-match",
                etag: etag,
                studentUniqueId: "different",
                givenName: "ShouldFail"
            )
        );

        response.StatusCode.Should().Be(400);
        JsonObject? document = await GetStudentDocumentAsync(documentId);
        document!["givenName"]!.GetValue<string>().Should().Be("Original");
    }

    [Test]
    public async Task Given_Authorization_Failure_Mid_Batch_Rolls_Back_Previous_Operations()
    {
        SetAuthorizedActions("Create");
        Guid updateId = await InsertStudentAsync("authorized-update", "Original");
        string etag = await GetStudentEtagAsync(updateId);

        IFrontendResponse response = await ExecuteBatchAsync(
            CreateCreateOperation("blocked-create", "Eve"),
            CreateUpdateOperation(updateId, etag, "authorized-update", "ShouldFail")
        );

        response.StatusCode.Should().Be(403);
        (await StudentExistsAsync("blocked-create")).Should().BeFalse();
        JsonObject? persisted = await GetStudentDocumentAsync(updateId);
        persisted!["givenName"]!.GetValue<string>().Should().Be("Original");
    }

    [Test]
    public async Task Given_Stale_Etag_When_Updating_Returns_412_And_Rolls_Back()
    {
        Guid documentId = await InsertStudentAsync("etag-student", "Original");
        string validEtag = await GetStudentEtagAsync(documentId);

        IFrontendResponse response = await ExecuteBatchAsync(
            CreateUpdateOperation(documentId, validEtag + "-stale", "etag-student", "NewName")
        );

        response.StatusCode.Should().Be(412);
        JsonObject? persisted = await GetStudentDocumentAsync(documentId);
        persisted!["givenName"]!.GetValue<string>().Should().Be("Original");
    }

    [Test]
    public async Task Given_Batch_Exceeding_Limit_Returns_413_Without_DB_Writes()
    {
        AppSettings.BatchMaxOperations = 1;

        IFrontendResponse response = await ExecuteBatchAsync(
            CreateCreateOperation("limit-one", "One"),
            CreateCreateOperation("limit-two", "Two")
        );

        response.StatusCode.Should().Be(413);
        (await StudentExistsAsync("limit-one")).Should().BeFalse();
        (await StudentExistsAsync("limit-two")).Should().BeFalse();
    }

    [Test]
    public async Task Given_Empty_Batch_Returns_200_With_No_Ops()
    {
        IFrontendResponse response = await ExecuteBatchAsync();
        response.StatusCode.Should().Be(200);
        response.Body!.AsArray().Should().BeEmpty();
    }

    [Test]
    public async Task Given_Create_Update_Delete_By_NaturalKey_When_Update_Uses_Same_Batch_Document_Succeeds()
    {
        string uniqueId = $"batch-{Guid.NewGuid():N}".Substring(0, 12);

        JsonObject create = CreateCreateOperation(uniqueId, "Initial");
        JsonObject update = CreateUpdateByNaturalKeyOperation(
            naturalKeyValue: uniqueId,
            etag: null,
            studentUniqueId: uniqueId,
            givenName: "Updated"
        );
        JsonObject delete = CreateDeleteByNaturalKeyOperation(uniqueId);

        IFrontendResponse response = await ExecuteBatchAsync(create, update, delete);

        response.StatusCode.Should().Be(200);
        JsonArray body = response.Body!.AsArray();
        body.Count.Should().Be(3);

        Guid createdId = Guid.Parse(body[0]!["documentId"]!.GetValue<string>());
        JsonObject? deletedDocument = await GetStudentDocumentAsync(createdId);
        deletedDocument.Should().BeNull();
    }
}
