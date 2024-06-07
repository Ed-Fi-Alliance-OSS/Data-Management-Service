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
    [TestFixture]
    public class Given_a_delete_of_a_document : DeleteTests
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
    public class Given_a_delete_of_a_non_existing_document : DeleteTests
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
