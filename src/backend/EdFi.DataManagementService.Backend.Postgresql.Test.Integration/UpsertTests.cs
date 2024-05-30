// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Postgresql.Operation;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using ImpromptuInterface;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Test.Integration;

[TestFixture]
public class UpsertTests
{
    public static UpsertDocument CreateUpsert()
    {
        return new UpsertDocument(new SqlAction(), NullLogger<UpsertDocument>.Instance);
    }

    public static GetDocumentById CreateGetById()
    {
        return new GetDocumentById(NullLogger<GetDocumentById>.Instance);
    }

    public static T AsValueType<T, U>(U value)
        where T : class
    {
        return (new { Value = value }).ActLike<T>();
    }

    public static readonly IResourceInfo _resourceInfo = (
        new
        {
            ResourceVersion = AsValueType<ISemVer, string>("5.0.0"),
            AllowIdentityUpdates = false,
            ProjectName = AsValueType<IMetaEdProjectName, string>("ProjectName"),
            ResourceName = AsValueType<IMetaEdResourceName, string>("ResourceName"),
            IsDescriptor = false
        }
    ).ActLike<IResourceInfo>();

    public static readonly IDocumentInfo _documentInfo = (
        new
        {
            DocumentIdentity = (
                new { IdentityValue = "", IdentityJsonPath = AsValueType<IJsonPath, string>("$") }
            ).ActLike<IResourceInfo>(),
            ReferentialId = new ReferentialId(Guid.Empty),
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
                ResourceInfo = _resourceInfo,
                DocumentInfo = _documentInfo,
                EdfiDoc = JsonNode.Parse(edFiDocString),
                TraceId = new TraceId("123"),
                DocumentUuid = new DocumentUuid(documentUuidGuid)
            }
        ).ActLike<IUpsertRequest>();
    }

    public static IGetRequest CreateGetRequest(Guid documentUuidGuid)
    {
        return (
            new
            {
                ResourceInfo = _resourceInfo,
                TraceId = new TraceId("123"),
                DocumentUuid = new DocumentUuid(documentUuidGuid)
            }
        ).ActLike<IGetRequest>();
    }

    [TestFixture, DatabaseTestWithRollback]
    public class Given_an_upsert_of_a_new_document : UpsertTests, IDatabaseTest
    {
        private UpsertResult? _upsertResult;
        private GetResult? _getResult;

        public NpgsqlDataSource? DataSource { get; set; }

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly string _edFiDocString = """{"abc":123}""";

        [SetUp]
        public async Task Setup()
        {
            // Try upsert as insert
            IUpsertRequest upsertRequest = CreateUpsertRequest(_documentUuidGuid, _edFiDocString);
            _upsertResult = await CreateUpsert().Upsert(upsertRequest, DataSource!);

            // Confirm it's there
            IGetRequest getRequest = CreateGetRequest(_documentUuidGuid);
            _getResult = await CreateGetById().GetById(getRequest, DataSource!);
        }

        [Test]
        public void It_should_be_a_successful_insert()
        {
            _upsertResult!.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        [Test]
        public void It_should_be_found_by_get()
        {
            _getResult!.Should().BeOfType<GetResult.GetSuccess>();
            (_getResult! as GetResult.GetSuccess)!.DocumentUuid.Value.Should().Be(_documentUuidGuid);
            (_getResult! as GetResult.GetSuccess)!.EdfiDoc.ToJsonString().Should().Be(_edFiDocString);
        }
    }
}
