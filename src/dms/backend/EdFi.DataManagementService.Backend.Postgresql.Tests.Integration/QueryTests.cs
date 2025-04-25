// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Test.Integration;

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
            PaginationParameters paginationParameters = new(25, 0, false);

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
            (_getResult! as GetResult.GetSuccess)!.EdfiDoc.ToJsonString().Should().Contain("\"abc\":1");
        }

        [Test]
        public void It_should_be_found_by_query()
        {
            _queryResult!.Should().BeOfType<QueryResult.QuerySuccess>();
            (_queryResult! as QueryResult.QuerySuccess)!.EdfiDocs.Count.Should().Be(1);
        }

        [Test]
        public void It_should_not_be_total_count()
        {
            _queryResult!.Should().BeOfType<QueryResult.QuerySuccess>();
            (_queryResult! as QueryResult.QuerySuccess)!.TotalCount.Should().Be(null);
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
            PaginationParameters paginationParameters = new(25, 0, true);

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
            (_queryResults! as QueryResult.QuerySuccess)!.EdfiDocs.Count.Should().Be(3);
        }

        [Test]
        public void It_should_be_found_by_query_and_total_count_in_header()
        {
            _queryResults!.Should().BeOfType<QueryResult.QuerySuccess>();
            (_queryResults! as QueryResult.QuerySuccess)!.TotalCount.Should().Be(3);
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
            _edFiDocString3,
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
            PaginationParameters paginationParameters = new(25, 0, false);

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
            JsonNode[] edfiDocs = success.EdfiDocs.ToArray()!;
            edfiDocs.Length.Should().Be(3);
            edfiDocs[0].ToJsonString().Should().NotContain("\"abc\":4");
            edfiDocs[1].ToJsonString().Should().NotContain("\"abc\":4");
            edfiDocs[2].ToJsonString().Should().NotContain("\"abc\":4");
        }

        [Test]
        public void It_should_find_1_document_for_resourcename2()
        {
            _queryResults2!.Should().BeOfType<QueryResult.QuerySuccess>();
            QueryResult.QuerySuccess success = (_queryResults2! as QueryResult.QuerySuccess)!;
            JsonNode[] edfiDocs = success.EdfiDocs.ToArray()!;
            edfiDocs.Length.Should().Be(1);
            edfiDocs[0].ToJsonString().Should().Contain("\"abc\":4");
        }

        [Test]
        public void It_should_not_be_total_count()
        {
            _queryResults2!.Should().BeOfType<QueryResult.QuerySuccess>();
            (_queryResults2! as QueryResult.QuerySuccess)!.TotalCount.Should().Be(null);
        }
    }

    [TestFixture]
    public class Given_insert_of_several_descriptors : QueryTests
    {
        private QueryResult? _queryByResource;
        private QueryResult? _queryByCodeValue;
        private QueryResult? _queryByNamespace;
        private QueryResult? _queryByTwo;

        private static readonly string _edFiDocString1 =
            """{"codeValue":"abc","shortDescription":"1","namespace":"uri://one"}""";
        private static readonly string _edFiDocString2 =
            """{"codeValue":"def","shortDescription":"2","namespace":"uri://one"}""";
        private static readonly string _edFiDocString3 =
            """{"codeValue":"ghi","shortDescription":"3","namespace":"uri://two"}""";
        private static readonly string _edFiDocString4 =
            """{"codeValue":"def","shortDescription":"4","namespace":"uri://three"}""";

        [SetUp]
        public async Task Setup()
        {
            var setupIndex =
                @"
CREATE EXTENSION IF NOT EXISTS btree_gin;
CREATE INDEX IF NOT EXISTS IX_Document_GIN on dms.document USING gin(EdfiDoc jsonb_path_ops, ResourceName);
";

            var command = Connection!.CreateCommand();
            command.CommandText = setupIndex;
            command.Transaction = Transaction;
            await command.ExecuteNonQueryAsync();

            var requests = CreateMultipleInsertRequest(
                "SomeDescriptor",
                [_edFiDocString1, _edFiDocString2, _edFiDocString3, _edFiDocString4]
            );

            foreach (var request in requests)
            {
                await CreateUpsert().Upsert(request, Connection!, Transaction!);
            }

            PaginationParameters paginationParameters = new(25, 0, false);

            // Query by resource name
            IQueryRequest queryRequest = CreateQueryRequest("SomeDescriptor", [], paginationParameters);
            _queryByResource = await CreateQueryDocument()
                .QueryDocuments(queryRequest, Connection!, Transaction!);

            // Query by code value - expect 2
            IQueryRequest queryRequest2 = CreateQueryRequest(
                "SomeDescriptor",
                new() { { "codeValue", "def" } },
                paginationParameters
            );
            _queryByCodeValue = await CreateQueryDocument()
                .QueryDocuments(queryRequest2, Connection!, Transaction!);

            // Query by namespace - expect 2
            IQueryRequest queryRequest3 = CreateQueryRequest(
                "SomeDescriptor",
                new() { { "namespace", "uri://one" } },
                paginationParameters
            );
            _queryByNamespace = await CreateQueryDocument()
                .QueryDocuments(queryRequest3, Connection!, Transaction!);

            // Query by two parameters - expect 1
            IQueryRequest queryRequest4 = CreateQueryRequest(
                "SomeDescriptor",
                new() { { "codeValue", "def" }, { "namespace", "uri://one" } },
                paginationParameters
            );
            _queryByTwo = await CreateQueryDocument()
                .QueryDocuments(queryRequest4, Connection!, Transaction!);
        }

        [Test]
        public void When_querying_by_resourceName_it_should_find_four_results()
        {
            _queryByResource!.Should().BeOfType<QueryResult.QuerySuccess>();
            QueryResult.QuerySuccess success = (_queryByResource! as QueryResult.QuerySuccess)!;

            success.EdfiDocs.Count.Should().Be(4);

            var docString = success.EdfiDocs.ToJsonString();

            docString.Should().Contain("1");
            docString.Should().Contain("2");
            docString.Should().Contain("3");
            docString.Should().Contain("4");
        }

        [Test]
        public void When_querying_by_codeValue_it_should_find_two_results()
        {
            _queryByCodeValue!.Should().BeOfType<QueryResult.QuerySuccess>();
            QueryResult.QuerySuccess success = (_queryByCodeValue! as QueryResult.QuerySuccess)!;

            success.EdfiDocs.Count.Should().Be(2);

            var docString = success.EdfiDocs.ToJsonString();

            docString.Should().Contain("2");
            docString.Should().Contain("4");
        }

        [Test]
        public void When_querying_by_namespace_it_should_find_two_results()
        {
            _queryByNamespace!.Should().BeOfType<QueryResult.QuerySuccess>();
            QueryResult.QuerySuccess success = (_queryByNamespace! as QueryResult.QuerySuccess)!;

            success.EdfiDocs.Count.Should().Be(2);

            var docString = success.EdfiDocs.ToJsonString();

            docString.Should().Contain("1");
            docString.Should().Contain("2");
        }

        [Test]
        public void When_querying_by_two_parameters_it_should_find_one_result()
        {
            _queryByTwo!.Should().BeOfType<QueryResult.QuerySuccess>();
            QueryResult.QuerySuccess success = (_queryByTwo! as QueryResult.QuerySuccess)!;

            success.EdfiDocs.Count.Should().Be(1);

            var docString = success.EdfiDocs.ToJsonString();

            docString.Should().Contain("2");
        }
    }
}
