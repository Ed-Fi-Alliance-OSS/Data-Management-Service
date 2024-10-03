// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Test.Integration;

[TestFixture]
public class DeleteTests : DatabaseTest
{
    private static readonly string _defaultResourceName = "DefaultResourceName";

    private static TraceId traceId = new("");

    [TestFixture]
    public class Given_a_delete_of_a_non_existing_document : DeleteTests
    {
        private DeleteResult? _deleteResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();

        [SetUp]
        public async Task Setup()
        {
            IDeleteRequest deleteRequest = CreateDeleteRequest(_defaultResourceName, _documentUuidGuid);
            _deleteResult = await CreateDeleteById().DeleteById(deleteRequest, Connection!, Transaction!);
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
            await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!, traceId);

            IDeleteRequest deleteRequest = CreateDeleteRequest(_defaultResourceName, _documentUuidGuid);
            _deleteResult = await CreateDeleteById().DeleteById(deleteRequest, Connection!, Transaction!);
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
                    await CreateUpsert().Upsert(upsertRequest, connection, transaction, traceId);
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
        public void It_should_be_not_exists_for_2nd_transaction_due_to_retry()
        {
            _deleteResult2!.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
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
                    await CreateUpsert().Upsert(upsertRequest, connection, transaction, traceId);
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
                    return await CreateUpdate().UpdateById(updateRequest, connection, transaction, traceId);
                }
            );
        }

        [Test]
        public void It_should_be_a_successful_delete_for_1st_transaction()
        {
            _deleteResult!.Should().BeOfType<DeleteResult.DeleteSuccess>();
        }

        [Test]
        public void It_should_be_an_update_not_exists_for_2nd_transaction_due_to_retry()
        {
            _updateResult.Should().BeOfType<UpdateResult.UpdateFailureNotExists>();
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
                    await CreateUpsert().Upsert(upsertRequest, connection, transaction, traceId);
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpdateRequest updateRequest = CreateUpdateRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString2
                    );
                    return await CreateUpdate().UpdateById(updateRequest, connection, transaction, traceId);
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
        public void It_should_be_a_successful_delete_for_2nd_transaction_due_to_retry()
        {
            _deleteResult.Should().BeOfType<DeleteResult.DeleteSuccess>();
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
                    await CreateUpsert().Upsert(upsertRequest, connection, transaction, traceId);
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
                    return await CreateUpsert().Upsert(upsertRequest, connection, transaction, traceId);
                }
            );
        }

        [Test]
        public void It_should_be_a_successful_delete_for_1st_transaction()
        {
            _deleteResult.Should().BeOfType<DeleteResult.DeleteSuccess>();
        }

        [Test]
        public void It_should_be_a_successful_insert_for_2nd_transaction_due_to_retry()
        {
            _upsertResult.Should().BeOfType<UpsertResult.InsertSuccess>();
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
                    await CreateUpsert().Upsert(upsertRequest, connection, transaction, traceId);
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpsertRequest upsertRequest = CreateUpsertRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString2
                    );
                    return await CreateUpsert().Upsert(upsertRequest, connection, transaction, traceId);
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
        public void It_should_be_a_successful_delete_for_2nd_transaction_due_to_retry()
        {
            _deleteResult.Should().BeOfType<DeleteResult.DeleteSuccess>();
        }
    }

    [TestFixture]
    public class Given_the_delete_of_a_document_referenced_by_another_document : DeleteTests
    {
        private DeleteResult? _deleteResult;
        private List<UpsertResult> _upsertResults;

        private static readonly string _referencedResourceName = "ReferencedResource";
        private static readonly Guid _resourcedDocUuidGuid = Guid.NewGuid();
        private static readonly Guid _referencedRefIdGuid = Guid.NewGuid();
        private static readonly string _referencedDocString = """{"abc":1}""";

        private static readonly string _referencingResourceName = "ReferencingResource";
        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            _upsertResults = new List<UpsertResult>();
            IUpsertRequest refUpsertRequest = CreateUpsertRequest(
                _referencedResourceName,
                _resourcedDocUuidGuid,
                _referencedRefIdGuid,
                _referencedDocString
            );
            _upsertResults.Add(
                await CreateUpsert().Upsert(refUpsertRequest, Connection!, Transaction!, traceId)
            );

            // Add references
            Reference[] references = [new(_referencingResourceName, _referencedRefIdGuid)];

            IUpsertRequest upsertRequest = CreateUpsertRequest(
                _referencingResourceName,
                _documentUuidGuid,
                _referentialIdGuid,
                _edFiDocString,
                CreateDocumentReferences(references)
            );

            _upsertResults.Add(
                await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!, traceId)
            );

            await Transaction!.CommitAsync();
            Transaction = await Connection!.BeginTransactionAsync(ConfiguredIsolationLevel);

            _deleteResult = await CreateDeleteById()
                .DeleteById(
                    CreateDeleteRequest(_referencedResourceName, _resourcedDocUuidGuid),
                    Connection!,
                    Transaction!
                );
        }

        [Test]
        public void It_should_be_a_successful_inserts()
        {
            _upsertResults.Should().HaveCount(2);
            _upsertResults.ForEach(x => x.Should().BeOfType<UpsertResult.InsertSuccess>());
        }

        [Test]
        public void It_should_be_a_delete_failure_reference()
        {
            _deleteResult.Should().BeOfType<DeleteResult.DeleteFailureReference>();
        }

        [Test]
        public void It_should_be_equal_to_referencing_resource_name()
        {
            var result = _deleteResult as DeleteResult.DeleteFailureReference;
            result.Should().NotBeNull();
            result?.ReferencingDocumentResourceNames.Should().NotBeNull();
            result?.ReferencingDocumentResourceNames.Should().Contain(_referencingResourceName);
        }
    }

    [TestFixture]
    public class Given_the_delete_of_a_document_with_outbound_reference_only : DeleteTests
    {
        private DeleteResult? _deleteResult;
        private List<UpsertResult> _upsertResults;

        private static readonly string _referencedResourceName = "ReferencedResource";
        private static readonly Guid _resourcedDocUuidGuid = Guid.NewGuid();
        private static readonly Guid _referencedRefIdGuid = Guid.NewGuid();
        private static readonly string _referencedDocString = """{"abc":1}""";

        private static readonly string _referencingResourceName = "ReferencingResource";
        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            _upsertResults = new List<UpsertResult>();
            IUpsertRequest refUpsertRequest = CreateUpsertRequest(
                _referencedResourceName,
                _resourcedDocUuidGuid,
                _referencedRefIdGuid,
                _referencedDocString
            );
            _upsertResults.Add(
                await CreateUpsert().Upsert(refUpsertRequest, Connection!, Transaction!, traceId)
            );

            // Add references
            Reference[] references = [new(_referencingResourceName, _referencedRefIdGuid)];

            IUpsertRequest upsertRequest = CreateUpsertRequest(
                _referencingResourceName,
                _documentUuidGuid,
                _referentialIdGuid,
                _edFiDocString,
                CreateDocumentReferences(references)
            );

            _upsertResults.Add(
                await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!, traceId)
            );

            await Transaction!.CommitAsync();
            Transaction = await Connection!.BeginTransactionAsync(ConfiguredIsolationLevel);

            _deleteResult = await CreateDeleteById()
                .DeleteById(
                    CreateDeleteRequest(_referencingResourceName, _documentUuidGuid),
                    Connection!,
                    Transaction!
                );
        }

        [Test]
        public void It_should_be_a_successful_inserts()
        {
            _upsertResults.Should().HaveCount(2);
            _upsertResults.ForEach(x => x.Should().BeOfType<UpsertResult.InsertSuccess>());
        }

        [Test]
        public void It_should_be_a_delete_success()
        {
            _deleteResult.Should().BeOfType<DeleteResult.DeleteSuccess>();
        }
    }

    [TestFixture]
    public class Given_delete_of_a_subclass_document_referenced_by_another_document_as_a_superclass
        : DeleteTests
    {
        private DeleteResult? _deleteResult;
        private List<UpsertResult> _upsertResults;

        private static readonly string _subclassName = "SubClass";
        private static readonly Guid _subClassDocumentUuidGuid = Guid.NewGuid();
        private static readonly Guid _subClassRefIdGuid = Guid.NewGuid();
        private static readonly string _subClassDocString = """{"abc":1}""";

        private static readonly string _superClassName = "SuperClass";
        private static readonly Guid _superClassReferentialIdGuid = Guid.NewGuid();

        private static readonly string _referencingClassName = "Class";
        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            _upsertResults = new List<UpsertResult>();
            IUpsertRequest subClassUpsertRequest = CreateUpsertRequest(
                _subclassName,
                _subClassDocumentUuidGuid,
                _subClassRefIdGuid,
                _subClassDocString,
                null,
                null,
                CreateSuperclassIdentity(_superClassName, _superClassReferentialIdGuid)
            );
            _upsertResults.Add(
                await CreateUpsert().Upsert(subClassUpsertRequest, Connection!, Transaction!, traceId)
            );

            // Add references
            Reference[] references = [new(_referencingClassName, _superClassReferentialIdGuid)];

            IUpsertRequest upsertRequest = CreateUpsertRequest(
                _referencingClassName,
                _documentUuidGuid,
                _referentialIdGuid,
                _edFiDocString,
                CreateDocumentReferences(references)
            );

            _upsertResults.Add(
                await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!, traceId)
            );

            await Transaction!.CommitAsync();

            Transaction = await Connection!.BeginTransactionAsync(ConfiguredIsolationLevel);

            _deleteResult = await CreateDeleteById()
                .DeleteById(
                    CreateDeleteRequest(_subclassName, _subClassDocumentUuidGuid),
                    Connection!,
                    Transaction!
                );
        }

        [Test]
        public void It_should_be_a_successful_inserts()
        {
            _upsertResults.Should().HaveCount(2);
            _upsertResults.ForEach(x => x.Should().BeOfType<UpsertResult.InsertSuccess>());
        }

        [Test]
        public void It_should_be_a_delete_failure_reference()
        {
            _deleteResult.Should().BeOfType<DeleteResult.DeleteFailureReference>();
        }

        [Test]
        public void It_should_be_equal_to_referencing_resource_name()
        {
            var result = _deleteResult as DeleteResult.DeleteFailureReference;
            result.Should().NotBeNull();
            result?.ReferencingDocumentResourceNames.Should().NotBeNull();
            result?.ReferencingDocumentResourceNames.Should().Contain(_referencingClassName);
        }
    }
}
