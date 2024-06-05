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
public class DeleteTests : DatabaseTest
{
    public static UpsertDocument CreateUpsert(NpgsqlDataSource dataSource)
    {
        return new UpsertDocument(dataSource, new SqlAction(), NullLogger<UpsertDocument>.Instance);
    }

    public static DeleteDocumentById CreateDeleteById(NpgsqlDataSource dataSource)
    {
        return new DeleteDocumentById(dataSource, new SqlAction(), NullLogger<DeleteDocumentById>.Instance);
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

    public static IDeleteRequest CreateDeleteRequest(Guid documentUuidGuid)
    {
        return (
            new
            {
                ResourceInfo = _resourceInfo,
                TraceId = new TraceId("123"),
                DocumentUuid = new DocumentUuid(documentUuidGuid)
            }
        ).ActLike<IDeleteRequest>();
    }

    [TestFixture]
    public class Given_an_delete_of_a_document : DeleteTests
    {
        private DeleteResult? _deleteResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly string _edFiDocString = """{"abc":1}""";

        [SetUp]
        public async Task Setup()
        {
            // Insert document before deleting
            IUpsertRequest upsertRequest = CreateUpsertRequest(_documentUuidGuid, _edFiDocString);
            await CreateUpsert(DataSource!).Upsert(upsertRequest);

            IDeleteRequest deleteRequest = CreateDeleteRequest(_documentUuidGuid);
            _deleteResult = await CreateDeleteById(DataSource!).DeleteById(deleteRequest);
        }

        [Test]
        public void It_should_be_a_successful_delete()
        {
            _deleteResult!.Should().BeOfType<DeleteResult.DeleteSuccess>();
        }
    }

    [TestFixture]
    public class Given_an_delete_of_non_existing_document : DeleteTests
    {
        private DeleteResult? _deleteResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();

        [SetUp]
        public async Task Setup()
        {
            IDeleteRequest deleteRequest = CreateDeleteRequest(_documentUuidGuid);
            _deleteResult = await CreateDeleteById(DataSource!).DeleteById(deleteRequest);
        }

        [Test]
        public void It_should_be_a_not_exists_failure()
        {
            _deleteResult!.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
        }
    }
}
