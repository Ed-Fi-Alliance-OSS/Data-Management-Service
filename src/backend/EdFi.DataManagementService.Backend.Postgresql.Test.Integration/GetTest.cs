// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Test.Integration;

internal record PaginationParameters(
    int? limit,

    int? offset
) : IPaginationParameters;

[TestFixture]
public class GetTest : DatabaseTest
{
    [TestFixture]
    public class Given_an_upsert_of_a_new_document : GetTest
    {
        private UpsertResult? _upsertResult;
        private GetResult? _getResult;
        private QueryResult? _getByKeyResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly string _edFiDocString = """{"abc":1}""";
        
        [SetUp]
        public async Task Setup()
        {
            IUpsertRequest upsertRequest = CreateUpsertRequest(_documentUuidGuid, _edFiDocString);
            _upsertResult = await CreateUpsert(DataSource!).Upsert(upsertRequest);

            // Confirm it's there
            IGetRequest getRequest = CreateGetRequest(_documentUuidGuid);
            _getResult = await CreateGetById(DataSource!).GetById(getRequest);

            Dictionary<string, string>? searchParameters = new Dictionary<string, string>();
            PaginationParameters paginationParameters = new PaginationParameters(25, 0);

            IQueryRequest queryRequest = CreateGetRequestbyKey(searchParameters, paginationParameters, string.Empty);
            _getByKeyResult = await GetDocumentByKey(DataSource!).QueryDocuments(queryRequest);
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
        public void It_should_be_found_by_get_by_key()
        {
            _getByKeyResult!.Should().BeOfType<QueryResult.QuerySuccess>();
            (_getByKeyResult! as QueryResult.QuerySuccess)!.EdfiDocs.Length.Should().Be(1);
        }
    }

    [TestFixture]
    public class Given_multiple_upserts_of_a_document_whit_same_resource_name : GetTest
    {
        private UpsertResult? _upsertResult;
        private QueryResult? _getByKeyResults;

        [SetUp]
        public async Task Setup()
        {
            var documents = new Dictionary<Guid, string>
            {
                { Guid.NewGuid(), """{"abc":1}""" },
                { Guid.NewGuid(), """{"abc":2}""" },
                { Guid.NewGuid(), """{"abc":3}""" }
            };
            
            List<IUpsertRequest> upsertRequests = CreateMultipleUpsertRequest(documents, string.Empty);

            foreach (var upsertRequest in upsertRequests)
            {   
                _upsertResult = await CreateUpsert(DataSource!).Upsert(upsertRequest);
            }
            
            Dictionary<string, string>? searchParameters = new Dictionary<string, string>();
            PaginationParameters paginationParameters = new PaginationParameters(25, 0);

            IQueryRequest queryRequest = CreateGetRequestbyKey(searchParameters, paginationParameters, string.Empty);
            _getByKeyResults = await GetDocumentByKey(DataSource!).QueryDocuments(queryRequest);
        }

        [Test]
        public void It_should_be_found_by_get_by_key()
        {
            _getByKeyResults!.Should().BeOfType<QueryResult.QuerySuccess>();
            (_getByKeyResults! as QueryResult.QuerySuccess)!.EdfiDocs.Length.Should().Be(3);
        }
    }

    [TestFixture]
    public class Given_multiple_upserts_of_a_document_whit_diferent_resource_name : GetTest
    {
        private UpsertResult? _upsertResult;
        private QueryResult? _getByKeyResults;
        private QueryResult? _getByKeyResults2;

        [SetUp]
        public async Task Setup()
        {
            var resource1 = new Dictionary<Guid, string>
            {
                { Guid.NewGuid(), """{"abc":1}""" },
                { Guid.NewGuid(), """{"abc":2}""" },
                { Guid.NewGuid(), """{"abc":3}""" }
            };

            List<IUpsertRequest> upsertRequests = CreateMultipleUpsertRequest(resource1, string.Empty);

            foreach (var upsertRequest in upsertRequests)
            {
                _upsertResult = await CreateUpsert(DataSource!).Upsert(upsertRequest);
            }

            var resource2 = new Dictionary<Guid, string>
            {
                { Guid.NewGuid(), """{"abcde":10}""" }
            };

            List<IUpsertRequest> upsertRequests2 = CreateMultipleUpsertRequest(resource2, "abc");

            foreach (var upsertRequest2 in upsertRequests2)
            {
                _upsertResult = await CreateUpsert(DataSource!).Upsert(upsertRequest2);
            }

            Dictionary<string, string>? searchParameters = new Dictionary<string, string>();
            PaginationParameters paginationParameters = new PaginationParameters(25, 0);

            IQueryRequest queryRequest = CreateGetRequestbyKey(searchParameters, paginationParameters, string.Empty);
            _getByKeyResults = await GetDocumentByKey(DataSource!).QueryDocuments(queryRequest);

            IQueryRequest queryRequest2 = CreateGetRequestbyKey(searchParameters, paginationParameters, "abc");
            _getByKeyResults2 = await GetDocumentByKey(DataSource!).QueryDocuments(queryRequest2);
        }

        [Test]
        public void It_should_be_found_by_get_by_key_with_no_resource_name ()
        {
            _getByKeyResults!.Should().BeOfType<QueryResult.QuerySuccess>();
            (_getByKeyResults! as QueryResult.QuerySuccess)!.EdfiDocs.Length.Should().Be(3);
        }

        [Test]
        public void It_should_be_found_by_get_by_key_with_a_specific_resource_name()
        {
            _getByKeyResults2!.Should().BeOfType<QueryResult.QuerySuccess>();
            (_getByKeyResults2! as QueryResult.QuerySuccess)!.EdfiDocs.Length.Should().Be(1);
        }
    }
}
