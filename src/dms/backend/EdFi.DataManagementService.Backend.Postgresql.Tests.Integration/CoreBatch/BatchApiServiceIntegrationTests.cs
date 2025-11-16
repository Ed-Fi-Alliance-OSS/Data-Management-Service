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
}
