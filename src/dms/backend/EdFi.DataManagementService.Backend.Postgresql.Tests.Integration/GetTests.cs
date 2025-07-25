// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

public class GetTests : DatabaseTest
{
    private static readonly string _defaultResourceName = "DefaultResourceName";

    [TestFixture]
    public class Given_An_Nonexistent_Document : GetTests
    {
        private GetResult? _getResult;

        [SetUp]
        public async Task Setup()
        {
            IGetRequest getRequest = CreateGetRequest(_defaultResourceName, Guid.NewGuid());
            _getResult = await CreateGetById().GetById(getRequest, Connection!, Transaction!);
        }

        [Test]
        public void It_should_not_be_found()
        {
            _getResult!.Should().BeOfType<GetResult.GetFailureNotExists>();
        }
    }

    [TestFixture]
    public class Given_An_Existing_Document : GetTests
    {
        private GetResult? _getResult;

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
            await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!);

            _getResult = await CreateGetById()
                .GetById(
                    CreateGetRequest(_defaultResourceName, _documentUuidGuid),
                    Connection!,
                    Transaction!
                );
        }

        [Test]
        public void It_should_be_found_by_get_by_id()
        {
            _getResult!.Should().BeOfType<GetResult.GetSuccess>();
            (_getResult! as GetResult.GetSuccess)!.DocumentUuid.Value.Should().Be(_documentUuidGuid);
            (_getResult! as GetResult.GetSuccess)!.EdfiDoc.ToJsonString().Should().Contain("\"abc\":1");
        }
    }

    [TestFixture]
    public class Given_An_Existing_Document_For_A_Different_Resource : GetTests
    {
        private GetResult? _getResult;

        private static readonly string _resourceName1 = "ResourceName1";
        private static readonly string _resourceName2 = "ResourceName2";

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly string _edFiDocString = """{"abc":1}""";

        [SetUp]
        public async Task Setup()
        {
            IUpsertRequest upsertRequest = CreateUpsertRequest(
                _resourceName1,
                _documentUuidGuid,
                Guid.NewGuid(),
                _edFiDocString
            );
            await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!);

            _getResult = await CreateGetById()
                .GetById(CreateGetRequest(_resourceName2, _documentUuidGuid), Connection!, Transaction!);
        }

        [Test]
        public void It_should_not_be_found_by_get_by_id()
        {
            _getResult!.Should().BeOfType<GetResult.GetFailureNotExists>();
        }
    }

    [TestFixture]
    public class Given_An_Overlapping_Upsert_And_Get_Of_The_Same_Document_With_Upsert_Committed_First
        : GetTests
    {
        private UpsertResult? _upsertResult;
        private GetResult? _getResult;
        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly string _edFiDocString = """{"abc":1}""";

        [SetUp]
        public async Task Setup()
        {
            (_upsertResult, _getResult) = await OrchestrateOperations(
                (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    return Task.CompletedTask;
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpsertRequest upsertRequest = CreateUpsertRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        Guid.NewGuid(),
                        _edFiDocString
                    );
                    return await CreateUpsert().Upsert(upsertRequest, connection, transaction);
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    return await CreateGetById()
                        .GetById(
                            CreateGetRequest(_defaultResourceName, _documentUuidGuid),
                            Connection!,
                            Transaction!
                        );
                }
            );
        }

        [Test]
        public void It_should_be_a_successful_insert_for_1st_transaction()
        {
            _upsertResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        [Test]
        public void It_should_not_be_found_by_get()
        {
            _getResult.Should().BeOfType<GetResult.GetFailureNotExists>();
        }
    }

    [TestFixture]
    public class Given_An_Overlapping_Upsert_And_Get_Of_The_Same_Document_With_Get_Committed_First : GetTests
    {
        private UpsertResult? _upsertResult;
        private GetResult? _getResult;
        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly string _edFiDocString = """{"abc":1}""";

        [SetUp]
        public async Task Setup()
        {
            (_getResult, _upsertResult) = await OrchestrateOperations(
                (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    return Task.CompletedTask;
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    return await CreateGetById()
                        .GetById(
                            CreateGetRequest(_defaultResourceName, _documentUuidGuid),
                            Connection!,
                            Transaction!
                        );
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpsertRequest upsertRequest = CreateUpsertRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        Guid.NewGuid(),
                        _edFiDocString
                    );
                    return await CreateUpsert().Upsert(upsertRequest, connection, transaction);
                }
            );
        }

        [Test]
        public void It_should_be_a_successful_insert_for_1st_transaction()
        {
            _upsertResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        [Test]
        public void It_should_not_be_found_by_get()
        {
            _getResult.Should().BeOfType<GetResult.GetFailureNotExists>();
        }
    }
}
