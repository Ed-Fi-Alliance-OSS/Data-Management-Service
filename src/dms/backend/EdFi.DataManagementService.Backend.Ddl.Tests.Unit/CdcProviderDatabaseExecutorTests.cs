// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_DbConnectionCdcProviderDatabaseExecutor
{
    [Test]
    public void It_should_preserve_the_existing_public_connection_transaction_constructor()
    {
        typeof(DbConnectionCdcProviderDatabaseExecutor)
            .GetConstructor([typeof(DbConnection), typeof(DbTransaction)])
            .Should()
            .NotBeNull();
    }

    [Test]
    public void It_should_map_public_provider_number_and_state_by_default()
    {
        var executor = new DbConnectionCdcProviderDatabaseExecutor(new UnusedDbConnection());

        var identity = executor.MapProviderErrorIdentity(new ProviderNumberDbException(1205, 13));

        identity.Should().Be(new CdcProviderErrorIdentity("1205", "13"));
    }

    private sealed class ProviderNumberDbException(int number, byte state) : DbException("provider failure")
    {
        public int Number { get; } = number;

        public byte State { get; } = state;
    }

    private sealed class UnusedDbConnection : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = "";

        public override string Database => "";

        public override string DataSource => "";

        public override string ServerVersion => "";

        public override ConnectionState State => ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close() { }

        public override void Open() => throw new NotSupportedException();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
