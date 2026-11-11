// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// F-05 (D-5): the staging database is created from the pristine <c>template0</c> in <c>UTF8</c>, with no locale
/// clause, so nothing local to <c>template1</c> reaches it and a session on <c>template1</c> cannot refuse it.
/// </summary>
public class PostgresqlStagingQueriesTests
{
    [TestFixture]
    public class Given_a_staging_database_name
    {
        private string _name = string.Empty;
        private string _statement = string.Empty;

        [SetUp]
        public void Setup()
        {
            DmsDatabaseName staging = DmsOperationId.New().StagingDatabaseName;
            _name = staging.Value;
            _statement = PostgresqlStagingQueries.CreateDatabaseStatement(staging);
        }

        [Test]
        public void It_creates_the_staging_database_from_template0_in_utf8() =>
            _statement.Should().Be($"CREATE DATABASE \"{_name}\" TEMPLATE template0 ENCODING 'UTF8';");
    }
}
