// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Policy;
using EdFi.DataManagementService.Core.External.Backend;
using FakeItEasy;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Test.Integration;

[TestFixture]
public class UpsertTests : DatabaseTest
{
    private static readonly string _defaultResourceName = "DefaultResourceName";

    [TestFixture]
    public class Given_an_upsert_of_a_new_document : UpsertTests
    {
        private UpsertResult? _upsertResult;
        private GetResult? _getResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString = """{"abc":1}""";

        [SetUp]
        public async Task Setup()
        {
            // Try upsert as insert
            IUpsertRequest upsertRequest = CreateUpsertRequest(
                _defaultResourceName,
                _documentUuidGuid,
                _referentialIdGuid,
                _edFiDocString
            );
            _upsertResult = await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!);

            // Confirm it's there
            IGetRequest getRequest = CreateGetRequest(_defaultResourceName, _documentUuidGuid);
            _getResult = await CreateGetById().GetById(getRequest, Connection!, Transaction!);
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

            var successResult = _getResult as GetResult.GetSuccess;
            var actualJson = JObject.Parse(successResult!.EdfiDoc.ToJsonString());
            var expectedJson = JObject.Parse(_edFiDocString);
            expectedJson["id"] = _documentUuidGuid;

            actualJson.Should()
                .BeEquivalentTo(expectedJson, options => options.ComparingByMembers<JObject>());
        }
    }

    [TestFixture]
    public class Given_an_upsert_of_an_existing_document_that_changes_the_edfidoc : UpsertTests
    {
        private UpsertResult? _upsertResult1;
        private UpsertResult? _upsertResult2;
        private GetResult? _getResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString2 = """{"abc":2}""";
        private static readonly string _edFiDocString3 = """{"abc":3}""";

        [SetUp]
        public async Task Setup()
        {
            // Create
            IUpsertRequest upsertRequest1 = CreateUpsertRequest(
                _defaultResourceName,
                _documentUuidGuid,
                _referentialIdGuid,
                _edFiDocString2
            );
            _upsertResult1 = await CreateUpsert().Upsert(upsertRequest1, Connection!, Transaction!);

            // Update
            IUpsertRequest upsertRequest2 = CreateUpsertRequest(
                _defaultResourceName,
                _documentUuidGuid,
                _referentialIdGuid,
                _edFiDocString3
            );
            _upsertResult2 = await CreateUpsert().Upsert(upsertRequest2, Connection!, Transaction!);

            // Confirm change was made
            IGetRequest getRequest = CreateGetRequest(_defaultResourceName, _documentUuidGuid);
            _getResult = await CreateGetById().GetById(getRequest, Connection!, Transaction!);
        }

        [Test]
        public void It_should_be_a_successful_insert()
        {
            _upsertResult1!.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        [Test]
        public void It_should_be_a_successful_update()
        {
            _upsertResult2!.Should().BeOfType<UpsertResult.UpdateSuccess>();
        }

        [Test]
        public void It_should_be_found_updated_by_get()
        {
            _getResult!.Should().BeOfType<GetResult.GetSuccess>();
            (_getResult! as GetResult.GetSuccess)!.DocumentUuid.Value.Should().Be(_documentUuidGuid);

            var successResult = _getResult as GetResult.GetSuccess;
            var actualJson = JObject.Parse(successResult!.EdfiDoc.ToJsonString());
            var expectedJson = JObject.Parse(_edFiDocString3);
            expectedJson["id"] = _documentUuidGuid;

            actualJson.Should()
                .BeEquivalentTo(expectedJson, options => options.ComparingByMembers<JObject>());
        }
    }

    [TestFixture]
    public class Given_an_insert_of_the_same_new_document_with_two_overlapping_requests : UpsertTests
    {
        private UpsertResult? _upsertResult1;
        private UpsertResult? _upsertResult2;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();

        private static readonly string _edFiDocString4 = """{"abc":4}""";
        private static readonly string _edFiDocString5 = """{"abc":5}""";

        [SetUp]
        public async Task Setup()
        {
            (_upsertResult1, _upsertResult2) = await OrchestrateOperations(
                (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    return Task.CompletedTask;
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpsertRequest upsertRequest1 = CreateUpsertRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString4
                    );
                    return await CreateUpsert().Upsert(upsertRequest1, connection, transaction);
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpsertRequest upsertRequest2 = CreateUpsertRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString5
                    );
                    return await CreateUpsert().Upsert(upsertRequest2, connection, transaction);
                }
            );
        }

        [Test]
        public void It_should_be_a_successful_insert_for_1st_transaction()
        {
            _upsertResult1!.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        [Test]
        public void It_should_be_a_write_conflict_for_2nd_transaction()
        {
            _upsertResult2!.Should().BeOfType<UpsertResult.UpsertFailureWriteConflict>();
        }

        [Test]
        public async Task It_should_be_the_1st_transaction_document_found_by_get()
        {
            IGetRequest getRequest = CreateGetRequest(_defaultResourceName, _documentUuidGuid);
            GetResult? _getResult = await CreateGetById().GetById(getRequest, Connection!, Transaction!);

            (_getResult! as GetResult.GetSuccess)!.DocumentUuid.Value.Should().Be(_documentUuidGuid);

            var successResult = _getResult as GetResult.GetSuccess;
            var actualJson = JObject.Parse(successResult!.EdfiDoc.ToJsonString());
            var expectedJson = JObject.Parse(_edFiDocString4);
            expectedJson["id"] = _documentUuidGuid;

            actualJson.Should()
                .BeEquivalentTo(expectedJson, options => options.ComparingByMembers<JObject>());
        }
    }

    [TestFixture]
    public class Given_an_insert_of_different_documents_with_overlapping_requests : UpsertTests
    {
        private UpsertResult? _upsertResult1;
        private UpsertResult? _upsertResult2;

        private static readonly Guid _documentUuidGuid1 = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid1 = Guid.NewGuid();
        private static readonly Guid _documentUuidGuid2 = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid2 = Guid.NewGuid();
        private static readonly string _edFiDocStringA = """{"abc":"a"}""";
        private static readonly string _edFiDocStringB = """{"abc":"b"}""";

        [SetUp]
        public async Task Setup()
        {
            (_upsertResult1, _upsertResult2) = await OrchestrateOperations(
                (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    return Task.CompletedTask;
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpsertRequest upsertRequest1 = CreateUpsertRequest(
                        _defaultResourceName,
                        _documentUuidGuid1,
                        _referentialIdGuid1,
                        _edFiDocStringA
                    );

                    return await CreateUpsert().Upsert(upsertRequest1, connection, transaction);
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpsertRequest upsertRequest2 = CreateUpsertRequest(
                        _defaultResourceName,
                        _documentUuidGuid2,
                        _referentialIdGuid2,
                        _edFiDocStringB
                    );
                    return await CreateUpsert().Upsert(upsertRequest2, connection, transaction);
                }
            );
        }

        [Test]
        public async Task It_should_be_a_successful_insert_for_1st_transaction()
        {
            _upsertResult1!.Should().BeOfType<UpsertResult.InsertSuccess>();

            GetResult? _getResult = await CreateGetById()
                .GetById(
                    CreateGetRequest(_defaultResourceName, _documentUuidGuid1),
                    Connection!,
                    Transaction!
                );

            var successResult = _getResult as GetResult.GetSuccess;
            var actualJson = JObject.Parse(successResult!.EdfiDoc.ToJsonString());
            var expectedJson = JObject.Parse(_edFiDocStringA);
            expectedJson["id"] = _documentUuidGuid1;

            actualJson.Should()
                .BeEquivalentTo(expectedJson, options => options.ComparingByMembers<JObject>());
        }

        [Test]
        public async Task It_should_be_a_successful_insert_for_2nd_transaction()
        {
            _upsertResult2!.Should().BeOfType<UpsertResult.InsertSuccess>();

            GetResult? _getResult = await CreateGetById()
                .GetById(
                    CreateGetRequest(_defaultResourceName, _documentUuidGuid2),
                    Connection!,
                    Transaction!
                );

            var successResult = _getResult as GetResult.GetSuccess;
            var actualJson = JObject.Parse(successResult!.EdfiDoc.ToJsonString());
            var expectedJson = JObject.Parse(_edFiDocStringB);
            expectedJson["id"] = _documentUuidGuid1;

            actualJson.Should()
                .BeEquivalentTo(expectedJson, options => options.ComparingByMembers<JObject>());
        }
    }

    [TestFixture]
    public class Given_an_update_of_the_same_document_with_two_overlapping_requests : UpsertTests
    {
        private UpsertResult? _upsertResult1;
        private UpsertResult? _upsertResult2;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString6 = """{"abc":6}""";
        private static readonly string _edFiDocString7 = """{"abc":7}""";
        private static readonly string _edFiDocString8 = """{"abc":8}""";

        [SetUp]
        public async Task Setup()
        {
            (_upsertResult1, _upsertResult2) = await OrchestrateOperations(
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    // Insert the original document
                    await CreateUpsert()
                        .Upsert(
                            CreateUpsertRequest(
                                _defaultResourceName,
                                _documentUuidGuid,
                                _referentialIdGuid,
                                _edFiDocString6
                            ),
                            connection,
                            transaction
                        );
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpsertRequest upsertRequest1 = CreateUpsertRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString7
                    );
                    return await CreateUpsert().Upsert(upsertRequest1, connection, transaction);
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpsertRequest upsertRequest2 = CreateUpsertRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString8
                    );
                    return await CreateUpsert().Upsert(upsertRequest2, connection, transaction);
                }
            );
        }

        [Test]
        public void It_should_be_a_successful_update_for_1st_transaction()
        {
            _upsertResult1!.Should().BeOfType<UpsertResult.UpdateSuccess>();
        }

        [Test]
        public void It_should_be_a_write_conflict_for_2nd_transaction()
        {
            _upsertResult2!.Should().BeOfType<UpsertResult.UpsertFailureWriteConflict>();
        }

        [Test]
        public async Task It_should_be_the_1st_updated_document_found_by_get()
        {
            IGetRequest getRequest = CreateGetRequest(_defaultResourceName, _documentUuidGuid);
            GetResult? _getResult = await CreateGetById().GetById(getRequest, Connection!, Transaction!);

            (_getResult! as GetResult.GetSuccess)!.DocumentUuid.Value.Should().Be(_documentUuidGuid);

            var successResult = _getResult as GetResult.GetSuccess;
            var actualJson = JObject.Parse(successResult!.EdfiDoc.ToJsonString());
            var expectedJson = JObject.Parse(_edFiDocString7);
            expectedJson["id"] = _documentUuidGuid;

            actualJson.Should()
                .BeEquivalentTo(expectedJson, options => options.ComparingByMembers<JObject>());
        }
    }

    [TestFixture]
    public class
        Given_an_upsert_of_a_subclass_document_when_a_different_subclass_has_the_same_superclass_identity : UpsertTests
    {
        private UpsertResult? _subclass1UpsertResult;
        private UpsertResult? _subclass2UpsertResult;

        private static readonly Guid _subclass1DocumentUuidGuid = Guid.NewGuid();
        private static readonly Guid _subclass2DocumentUuidGuid = Guid.NewGuid();
        private static readonly Guid _subclass1ReferentialIdGuid = Guid.NewGuid();
        private static readonly Guid _subclass2ReferentialIdGuid = Guid.NewGuid();
        private static readonly Guid _superclassReferentialIdGuid = Guid.NewGuid();
        private static readonly string _subclass1EdFiDocString = """{"abc":1}""";
        private static readonly string _subclass2EdFiDocString = """{"abc":1}""";

        [SetUp]
        public async Task Setup()
        {
            // Try upsert as insert
            IUpsertRequest subclass1UpsertRequest = CreateUpsertRequest(
                _defaultResourceName,
                _subclass1DocumentUuidGuid,
                _subclass1ReferentialIdGuid,
                _subclass1EdFiDocString,
                _superclassReferentialIdGuid
            );
            _subclass1UpsertResult = await CreateUpsert().Upsert(subclass1UpsertRequest, Connection!, Transaction!);

            // Try upsert a different subclass with same superclass identity
            IUpsertRequest subclass2UpsertRequest = CreateUpsertRequest(
                "AnotherResourceName",
                _subclass2DocumentUuidGuid,
                _subclass2ReferentialIdGuid,
                _subclass2EdFiDocString,
                _superclassReferentialIdGuid
            );
            _subclass2UpsertResult = await CreateUpsert().Upsert(subclass2UpsertRequest, Connection!, Transaction!);
        }

        [Test]
        public void It_should_be_an_identity_conflict_for_2nd_transaction()
        {
            _subclass1UpsertResult!.Should().BeOfType<UpsertResult.InsertSuccess>();
            _subclass2UpsertResult!.Should().BeOfType<UpsertResult.UpsertFailureIdentityConflict>();
        }
    }

    // Future tests - from Meadowlark

    // given an upsert of a new document that references an existing document

    // given an upsert of a new document with one existing and one non-existent reference

    // given an upsert of a subclass document when a different subclass has the same superclass identity

    // given an update of a document that references a non-existent document

    // given an update of a document that references an existing document

    // given an update of a document with one existing and one non-existent reference

    // given an update of a subclass document referenced by an existing document as a superclass


    // Future tests - new concurrency-based

    // given an upsert of a new document that references an existing document that is concurrently deleted

    // given an update of a document that references an existing document that is concurrently deleted
}
