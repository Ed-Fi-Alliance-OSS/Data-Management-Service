// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using System.Text.Json.Nodes;
using ImpromptuInterface;
using NUnit.Framework;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Postgresql.Test.Integration;

[TestFixture]
public class UpdateTests : DatabaseTest
{
    public static readonly IDocumentInfo OtherDocumentInfo = (
        new
        {
            DocumentIdentity = (
                new { IdentityValue = "", IdentityJsonPath = AsValueType<IJsonPath, string>("$") }
            ).ActLike<IResourceInfo>(),
            ReferentialId = new ReferentialId(Guid.NewGuid()),
            DocumentReferences = new List<IDocumentReference>(),
            DescriptorReferences = new List<IDocumentReference>(),
            SuperclassIdentity = null as ISuperclassIdentity
        }
    ).ActLike<IDocumentInfo>();

    public static IUpsertRequest CreateUpsertRequest(Guid documentUuidGuid, string edFiDocString)
    {
        return (
            new
            {
                ResourceInfo = ResourceInfo,
                DocumentInfo = DocumentInfo,
                EdfiDoc = JsonNode.Parse(edFiDocString),
                TraceId = new TraceId("123"),
                DocumentUuid = new DocumentUuid(documentUuidGuid)
            }
        ).ActLike<IUpsertRequest>();
    }

    public static IUpdateRequest CreateUpdateRequest(Guid documentUuidGuid, Guid referentialIdGuid, string edFiDocString)
    {
        return (
            new
            {
                ResourceInfo = ResourceInfo,
                DocumentInfo = (
                    new
                    {
                        DocumentIdentity = (
                            new { IdentityValue = "", IdentityJsonPath = AsValueType<IJsonPath, string>("$") }
                        ).ActLike<IResourceInfo>(),
                        ReferentialId = new ReferentialId(referentialIdGuid),
                        DocumentReferences = new List<IDocumentReference>(),
                        DescriptorReferences = new List<IDocumentReference>(),
                        SuperclassIdentity = null as ISuperclassIdentity
                    }
                ).ActLike<IDocumentInfo>(),
                EdfiDoc = JsonNode.Parse(edFiDocString),
                TraceId = new TraceId("123"),
                DocumentUuid = new DocumentUuid(documentUuidGuid)
            }
        ).ActLike<IUpdateRequest>();
    }

    public static IGetRequest CreateGetRequest(Guid documentUuidGuid)
    {
        return (
            new
            {
                ResourceInfo = ResourceInfo,
                TraceId = new TraceId("123"),
                DocumentUuid = new DocumentUuid(documentUuidGuid)
            }
        ).ActLike<IGetRequest>();
    }

    [TestFixture]
    public class GivenAnUpdateOfAnExistingDocument : UpdateTests
    {
        private UpdateResult? _updateResult;
        private GetResult? _getResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly string _edFiDocString1 = """{"abc":1}""";
        private static readonly string _edFiDocString2 = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            // Create
            IUpsertRequest upsertRequest = CreateUpsertRequest(_documentUuidGuid, _edFiDocString1);
            await CreateUpsert(DataSource!).Upsert(upsertRequest);

            // Update
            IUpdateRequest updateRequest = CreateUpdateRequest(_documentUuidGuid, upsertRequest.DocumentInfo.ReferentialId.Value, _edFiDocString2);
            _updateResult = await CreateUpdate(DataSource!).UpdateById(updateRequest);

            // Confirm change was made
            IGetRequest getRequest = CreateGetRequest(_documentUuidGuid);
            _getResult = await CreateGetById(DataSource!).GetById(getRequest);
        }

        [Test]
        public void It_should_be_a_successful_update()
        {
            _updateResult!.Should().BeOfType<UpdateResult.UpdateSuccess>();
        }

        [Test]
        public void It_should_be_found_updated_by_get()
        {
            _getResult!.Should().BeOfType<GetResult.GetSuccess>();
            (_getResult! as GetResult.GetSuccess)!.DocumentUuid.Value.Should().Be(_documentUuidGuid);
            (_getResult! as GetResult.GetSuccess)!.EdfiDoc.ToJsonString().Should().Be(_edFiDocString2);
        }
    }

    [TestFixture]
    public class GivenAnUpdateOfAnExistingDocumentWithDifferentReferentialId : UpdateTests
    {
        private UpdateResult? _updateResult;
        private GetResult? _getResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString1 = """{"abc":1}""";
        private static readonly string _edFiDocString2 = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            // Create
            IUpsertRequest upsertRequest = CreateUpsertRequest(_documentUuidGuid, _edFiDocString1);
            await CreateUpsert(DataSource!).Upsert(upsertRequest);

            // Update
            IUpdateRequest updateRequest = CreateUpdateRequest(_documentUuidGuid, _referentialIdGuid, _edFiDocString2);
            _updateResult = await CreateUpdate(DataSource!).UpdateById(updateRequest);

            // Confirm change was made
            IGetRequest getRequest = CreateGetRequest(_documentUuidGuid);
            _getResult = await CreateGetById(DataSource!).GetById(getRequest);
        }

        [Test]
        public void It_should_be_a_successful_update()
        {
            _updateResult!.Should().BeOfType<UpdateResult.UpdateFailureImmutableIdentity>();
        }
    }
}
