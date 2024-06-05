// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using ImpromptuInterface;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Test.Integration;

[TestFixture]
public class UpsertTests : DatabaseTest
{
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
    public class Given_an_upsert_of_a_new_document : UpsertTests
    {
        private UpsertResult? _upsertResult;
        private GetResult? _getResult;

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

    [TestFixture]
    public class Given_an_upsert_of_an_existing_document : UpsertTests
    {
        private UpsertResult? _upsertResult;
        private GetResult? _getResult;

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
