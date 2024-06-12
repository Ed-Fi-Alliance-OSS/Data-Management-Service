// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Test.Integration;

internal record PaginationParameters(int? limit, int? offset) : IPaginationParameters;

[TestFixture]
public class QueryTests : DatabaseTest
{
    private static readonly string _defaultResourceName = "DefaultResourceName";

    [TestFixture]
    public class Given_an_upsert_of_a_new_document : QueryTests
    {
        private UpsertResult? _upsertResult;
        private GetResult? _getResult;
        private QueryResult? _queryResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly string _edFiDocString = """{"abc":1}""";

        [SetUp]
        public async Task Setup()
        {
            IUpsertRequest upsertRequest = CreateUpsertRequest(
                _defaultResourceName,
                _documentUuidGuid,
                Guid.NewGuid(),
                _edFiDocString
            );
            _upsertResult = await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!);

            // Confirm it's there
            IGetRequest getRequest = CreateGetRequest(_defaultResourceName, _documentUuidGuid);
            _getResult = await CreateGetById().GetById(getRequest, Connection!, Transaction!);

            Dictionary<string, string>? searchParameters = [];
            PaginationParameters paginationParameters = new(25, 0);

            IQueryRequest queryRequest = CreateQueryRequest(
                _defaultResourceName,
                searchParameters,
                paginationParameters
            );
            _queryResult = await CreateQueryDocument()
                .QueryDocuments(queryRequest, Connection!, Transaction!);
        }

        [Test]
        public void It_should_be_a_successful_insert()
        {
            _upsertResult!.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        [Test]
        public void It_should_be_found_by_get_by_id()
        {
            _getResult!.Should().BeOfType<GetResult.GetSuccess>();
            (_getResult! as GetResult.GetSuccess)!.DocumentUuid.Value.Should().Be(_documentUuidGuid);
            (_getResult! as GetResult.GetSuccess)!.EdfiDoc.ToJsonString().Should().Be(_edFiDocString);
        }

        [Test]
        public void It_should_be_found_by_query()
        {
            _queryResult!.Should().BeOfType<QueryResult.QuerySuccess>();
            (_queryResult! as QueryResult.QuerySuccess)!.EdfiDocs.Length.Should().Be(1);
        }
    }

    [TestFixture]
    public class Given_multiple_upserts_of_a_document_with_the_same_resource_name : QueryTests
    {
        private QueryResult? _queryResults;

        private static readonly string _edFiDocString1 = """{"abc":1}""";
        private static readonly string _edFiDocString2 = """{"abc":2}""";
        private static readonly string _edFiDocString3 = """{"abc":3}""";

        [SetUp]
        public async Task Setup()
        {
            var requests = CreateMultipleInsertRequest(
                _defaultResourceName,
                [_edFiDocString1, _edFiDocString2, _edFiDocString3]
            );

            foreach (var request in requests)
            {
                await CreateUpsert().Upsert(request, Connection!, Transaction!);
            }

            Dictionary<string, string>? searchParameters = [];
            PaginationParameters paginationParameters = new(25, 0);

            IQueryRequest queryRequest = CreateQueryRequest(
                _defaultResourceName,
                searchParameters,
                paginationParameters
            );
            _queryResults = await CreateQueryDocument()
                .QueryDocuments(queryRequest, Connection!, Transaction!);
        }

        [Test]
        public void It_should_be_found_by_query()
        {
            _queryResults!.Should().BeOfType<QueryResult.QuerySuccess>();
            (_queryResults! as QueryResult.QuerySuccess)!.EdfiDocs.Length.Should().Be(3);
        }
    }

    [TestFixture]
    public class Given_multiple_upserts_of_a_document_with_different_resource_names : QueryTests
    {
        private QueryResult? _queryResults1;
        private QueryResult? _queryResults2;

        private static readonly string _edFiDocString1 = """{"abc":1}""";
        private static readonly string _edFiDocString2 = """{"abc":2}""";
        private static readonly string _edFiDocString3 = """{"abc":3}""";
        private static readonly string _edFiDocString4 = """{"abc":4}""";

        private static readonly string[] _resourceName1Docs =
        [
            _edFiDocString1,
            _edFiDocString2,
            _edFiDocString3
        ];

        [SetUp]
        public async Task Setup()
        {
            var requests = CreateMultipleInsertRequest("ResourceName1", _resourceName1Docs);

            foreach (var request in requests)
            {
                await CreateUpsert().Upsert(request, Connection!, Transaction!);
            }

            await CreateUpsert()
                .Upsert(
                    CreateUpsertRequest("ResourceName2", Guid.NewGuid(), Guid.NewGuid(), _edFiDocString4),
                    Connection!,
                    Transaction!
                );

            Dictionary<string, string>? searchParameters = [];
            PaginationParameters paginationParameters = new(25, 0);

            IQueryRequest queryRequest = CreateQueryRequest(
                "ResourceName1",
                searchParameters,
                paginationParameters
            );
            _queryResults1 = await CreateQueryDocument()
                .QueryDocuments(queryRequest, Connection!, Transaction!);

            IQueryRequest queryRequest2 = CreateQueryRequest(
                "ResourceName2",
                searchParameters,
                paginationParameters
            );
            _queryResults2 = await CreateQueryDocument()
                .QueryDocuments(queryRequest2, Connection!, Transaction!);
        }

        [Test]
        public void It_should_find_3_documents_for_resourcename1()
        {
            _queryResults1!.Should().BeOfType<QueryResult.QuerySuccess>();
            QueryResult.QuerySuccess success = (_queryResults1! as QueryResult.QuerySuccess)!;
            success.EdfiDocs.Length.Should().Be(3);
            success.EdfiDocs[0].ToJsonString().Should().BeOneOf(_resourceName1Docs);
            success.EdfiDocs[1].ToJsonString().Should().BeOneOf(_resourceName1Docs);
            success.EdfiDocs[2].ToJsonString().Should().BeOneOf(_resourceName1Docs);
        }

        [Test]
        public void It_should_find_1_document_for_resourcename2()
        {
            _queryResults2!.Should().BeOfType<QueryResult.QuerySuccess>();
            QueryResult.QuerySuccess success = (_queryResults2! as QueryResult.QuerySuccess)!;
            success.EdfiDocs.Length.Should().Be(1);
            success.EdfiDocs[0].ToJsonString().Should().Be(_edFiDocString4);
        }
    }
}
