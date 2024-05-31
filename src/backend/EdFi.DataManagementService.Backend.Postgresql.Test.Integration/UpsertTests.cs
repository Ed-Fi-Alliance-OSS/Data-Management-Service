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
[Ignore("Only works on Brad's dev env")]
public class UpsertTests
{
    public static UpsertDocument CreateUpsert(NpgsqlDataSource dataSource)
    {
        return new UpsertDocument(dataSource, new SqlAction(), NullLogger<UpsertDocument>.Instance);
    }

    public static GetDocumentById CreateGetById(NpgsqlDataSource dataSource)
    {
        return new GetDocumentById(dataSource, NullLogger<GetDocumentById>.Instance);
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
        private static readonly string _edFiDocString = """{"abc":1}""";

        [SetUp]
        public async Task Setup()
        {
            // Try upsert as insert
            IUpsertRequest upsertRequest = CreateUpsertRequest(_documentUuidGuid, _edFiDocString);
            _upsertResult = await CreateUpsert(DataSource!).Upsert(upsertRequest);

            // Confirm it's there
            IGetRequest getRequest = CreateGetRequest(_documentUuidGuid);
            _getResult = await CreateGetById(DataSource!).GetById(getRequest);
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

    [TestFixture, DatabaseTestWithRollback]
    public class Given_an_upsert_of_an_existing_document : UpsertTests, IDatabaseTest
    {
        private UpsertResult? _upsertResult;
        private GetResult? _getResult;

        public NpgsqlDataSource? DataSource { get; set; }

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly string _edFiDocString1 = """{"abc":1}""";
        private static readonly string _edFiDocString2 = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            // Create
            IUpsertRequest upsertRequest1 = CreateUpsertRequest(_documentUuidGuid, _edFiDocString1);
            await CreateUpsert(DataSource!).Upsert(upsertRequest1);

            // Update
            IUpsertRequest upsertRequest2 = CreateUpsertRequest(_documentUuidGuid, _edFiDocString2);
            _upsertResult = await CreateUpsert(DataSource!).Upsert(upsertRequest2);

            // Confirm change was made
            IGetRequest getRequest = CreateGetRequest(_documentUuidGuid);
            _getResult = await CreateGetById(DataSource!).GetById(getRequest);
        }

        [Test]
        public void It_should_be_a_successful_update()
        {
            _upsertResult!.Should().BeOfType<UpsertResult.UpdateSuccess>();
        }

        [Test]
        public void It_should_be_found_updated_by_get()
        {
            _getResult!.Should().BeOfType<GetResult.GetSuccess>();
            (_getResult! as GetResult.GetSuccess)!.DocumentUuid.Value.Should().Be(_documentUuidGuid);
            (_getResult! as GetResult.GetSuccess)!.EdfiDoc.ToJsonString().Should().Be(_edFiDocString2);
        }
    }
}
