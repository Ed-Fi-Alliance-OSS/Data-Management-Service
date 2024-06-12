// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Test.Integration;

[TestFixture]
public class DeleteTests : DatabaseTest
{
    private static readonly string _defaultResourceName = "DefaultResourceName";

    [TestFixture]
    public class Given_a_delete_of_a_non_existing_document : DeleteTests
    {
        private DeleteResult? _deleteResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();

        [SetUp]
        public async Task Setup()
        {
            IDeleteRequest deleteRequest = CreateDeleteRequest(_defaultResourceName, _documentUuidGuid);
            _deleteResult = await CreateDeleteById()
                .DeleteById(deleteRequest, Connection!, Transaction!);
        }

        [Test]
        public void It_should_be_a_not_exists_failure()
        {
            _deleteResult!.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
        }
    }

    [TestFixture]
    public class Given_a_delete_of_a_document : DeleteTests
    {
        private DeleteResult? _deleteResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString = """{"abc":1}""";

        [SetUp]
        public async Task Setup()
        {
            // Insert document before deleting
            IUpsertRequest upsertRequest = CreateUpsertRequest(
                _defaultResourceName,
                _documentUuidGuid,
                _referentialIdGuid,
                _edFiDocString
            );
            await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!);

            IDeleteRequest deleteRequest = CreateDeleteRequest(_defaultResourceName, _documentUuidGuid);
            _deleteResult = await CreateDeleteById()
                .DeleteById(deleteRequest, Connection!, Transaction!);
        }

        [Test]
        public void It_should_be_a_successful_delete()
        {
            _deleteResult!.Should().BeOfType<DeleteResult.DeleteSuccess>();
        }
    }

    [TestFixture]
    public class Given_a_delete_of_the_same_document_with_two_overlapping_requests : DeleteTests
    {
        private DeleteResult? _deleteResult1;
        private DeleteResult? _deleteResult2;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString = """{"abc":1}""";

        [SetUp]
        public async Task Setup()
        {
            (_deleteResult1, _deleteResult2) = await OrchestrateOperations(
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    // Insert document before deleting
                    IUpsertRequest upsertRequest = CreateUpsertRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString
                    );
                    await CreateUpsert().Upsert(upsertRequest, connection, transaction);
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    return await CreateDeleteById()
                        .DeleteById(
                            CreateDeleteRequest(_defaultResourceName, _documentUuidGuid),
                            connection,
                            transaction
                        );
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    return await CreateDeleteById()
                        .DeleteById(
                            CreateDeleteRequest(_defaultResourceName, _documentUuidGuid),
                            connection,
                            transaction
                        );
                }
            );
        }

        [Test]
        public void It_should_be_a_successful_delete_for_1st_transaction()
        {
            _deleteResult1!.Should().BeOfType<DeleteResult.DeleteSuccess>();
        }

        [Test]
        public void It_should_be_a_write_conflict_for_2nd_transaction()
        {
            _deleteResult2!.Should().BeOfType<DeleteResult.DeleteFailureWriteConflict>();
        }
    }

    [TestFixture]
    public class Given_an_overlapping_delete_and_update_of_the_same_document_with_delete_committed_first
        : DeleteTests
    {
        private DeleteResult? _deleteResult;
        private UpdateResult? _updateResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString1 = """{"abc":1}""";
        private static readonly string _edFiDocString2 = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            (_deleteResult, _updateResult) = await OrchestrateOperations(
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpsertRequest upsertRequest = CreateUpsertRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString1
                    );
                    await CreateUpsert().Upsert(upsertRequest, connection, transaction);
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    return await CreateDeleteById()
                        .DeleteById(
                            CreateDeleteRequest(_defaultResourceName, _documentUuidGuid),
                            connection,
                            transaction
                        );
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpdateRequest updateRequest = CreateUpdateRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString2
                    );
                    return await CreateUpdate().UpdateById(updateRequest, connection, transaction);
                }
            );
        }

        [Test]
        public void It_should_be_a_successful_delete_for_1st_transaction()
        {
            _deleteResult!.Should().BeOfType<DeleteResult.DeleteSuccess>();
        }

        [Test]
        public void It_should_be_an_update_write_conflict_for_2nd_transaction()
        {
            _updateResult.Should().BeOfType<UpdateResult.UpdateFailureWriteConflict>();
        }
    }

    [TestFixture]
    public class Given_an_overlapping_delete_and_update_of_the_same_document_with_update_committed_first
        : DeleteTests
    {
        private DeleteResult? _deleteResult;
        private UpdateResult? _updateResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString1 = """{"abc":1}""";
        private static readonly string _edFiDocString2 = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            (_updateResult, _deleteResult) = await OrchestrateOperations(
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpsertRequest upsertRequest = CreateUpsertRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString1
                    );
                    await CreateUpsert().Upsert(upsertRequest, connection, transaction);
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpdateRequest updateRequest = CreateUpdateRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString2
                    );
                    return await CreateUpdate().UpdateById(updateRequest, connection, transaction);
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    return await CreateDeleteById()
                        .DeleteById(
                            CreateDeleteRequest(_defaultResourceName, _documentUuidGuid),
                            connection,
                            transaction
                        );
                }
            );
        }

        [Test]
        public void It_should_be_a_successful_update_for_1st_transaction()
        {
            _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        }

        [Test]
        public void It_should_be_a_delete_write_conflict_for_2nd_transaction()
        {
            _deleteResult.Should().BeOfType<DeleteResult.DeleteFailureWriteConflict>();
        }
    }

    [TestFixture]
    public class Given_an_overlapping_delete_and_upsert_as_update_of_the_same_document_with_delete_committed_first
        : DeleteTests
    {
        private DeleteResult? _deleteResult;
        private UpsertResult? _upsertResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString1 = """{"abc":1}""";
        private static readonly string _edFiDocString2 = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            (_deleteResult, _upsertResult) = await OrchestrateOperations(
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpsertRequest upsertRequest = CreateUpsertRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString1
                    );
                    await CreateUpsert().Upsert(upsertRequest, connection, transaction);
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    return await CreateDeleteById()
                        .DeleteById(
                            CreateDeleteRequest(_defaultResourceName, _documentUuidGuid),
                            connection,
                            transaction
                        );
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpsertRequest upsertRequest = CreateUpsertRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString2
                    );
                    return await CreateUpsert().Upsert(upsertRequest, connection, transaction);
                }
            );
        }

        [Test]
        public void It_should_be_a_successful_delete_for_1st_transaction()
        {
            _deleteResult.Should().BeOfType<DeleteResult.DeleteSuccess>();
        }

        [Test]
        public void It_should_be_an_update_write_conflict_for_2nd_transaction()
        {
            _upsertResult.Should().BeOfType<UpsertResult.UpsertFailureWriteConflict>();
        }
    }

    [TestFixture]
    public class Given_an_overlapping_delete_and_upsert_as_update_of_the_same_document_with_upsert_committed_first
        : DeleteTests
    {
        private DeleteResult? _deleteResult;
        private UpsertResult? _upsertResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString1 = """{"abc":1}""";
        private static readonly string _edFiDocString2 = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            (_upsertResult, _deleteResult) = await OrchestrateOperations(
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpsertRequest upsertRequest = CreateUpsertRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString1
                    );
                    await CreateUpsert().Upsert(upsertRequest, connection, transaction);
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpsertRequest upsertRequest = CreateUpsertRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString2
                    );
                    return await CreateUpsert().Upsert(upsertRequest, connection, transaction);
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    return await CreateDeleteById()
                        .DeleteById(
                            CreateDeleteRequest(_defaultResourceName, _documentUuidGuid),
                            connection,
                            transaction
                        );
                }
            );
        }

        [Test]
        public void It_should_be_a_successful_update_for_1st_transaction()
        {
            _upsertResult.Should().BeOfType<UpsertResult.UpdateSuccess>();
        }

        [Test]
        public void It_should_be_a_delete_write_conflict_for_2nd_transaction()
        {
            _deleteResult.Should().BeOfType<DeleteResult.DeleteFailureWriteConflict>();
        }
    }

    // Future tests - from Meadowlark

    // given the delete of a document referenced by another document

    // given the delete of a document with outbound reference only

    // given delete of a subclass document referenced by another document as a superclass
}
