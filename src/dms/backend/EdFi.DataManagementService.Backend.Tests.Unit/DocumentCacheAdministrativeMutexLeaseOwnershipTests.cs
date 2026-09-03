// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The administrative mutex session outlives the method that opened it, so whatever keeps its data
/// source alive travels with the connection. This pins the two properties that makes safe: the claim
/// is released after the connection, and it is released even when the connection's own disposal
/// throws.
/// </summary>
[TestFixture]
public class Given_A_DocumentCacheAdministrativeMutexLease_Owning_A_Resource
{
    private sealed class RecordingOwnedResource(List<string> events) : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            events.Add("owned-resource");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingConnection(List<string> events, bool throwOnDispose) : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => string.Empty;

        public override string DataSource => string.Empty;

        public override string ServerVersion => string.Empty;

        public override ConnectionState State => ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close() { }

        public override void Open() => throw new NotSupportedException();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();

        public override ValueTask DisposeAsync()
        {
            events.Add("connection");

            if (throwOnDispose)
            {
                throw new InvalidOperationException("Simulated connection disposal failure.");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestMutexLease(DbConnection connection, IAsyncDisposable ownedResource)
        : DocumentCacheAdministrativeMutexLease(RelationalProviderToken.Postgresql, connection, ownedResource)
    {
        protected override Task ReleaseAsync(DbConnection connection, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    [Test]
    public async Task It_should_release_the_owned_resource_after_the_connection()
    {
        List<string> events = [];
        RecordingOwnedResource owned = new(events);
        TestMutexLease lease = new(new RecordingConnection(events, throwOnDispose: false), owned);

        await lease.DisposeAsync();

        events.Should().Equal("connection", "owned-resource");
        owned.DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task It_should_release_the_owned_resource_when_the_connection_disposal_throws()
    {
        List<string> events = [];
        RecordingOwnedResource owned = new(events);
        TestMutexLease lease = new(new RecordingConnection(events, throwOnDispose: true), owned);

        Func<Task> act = async () => await lease.DisposeAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        owned.DisposeCount.Should().Be(1, "a failed connection disposal must not strand the claim");
    }

    [Test]
    public async Task It_should_release_the_owned_resource_only_once()
    {
        List<string> events = [];
        RecordingOwnedResource owned = new(events);
        TestMutexLease lease = new(new RecordingConnection(events, throwOnDispose: false), owned);

        await DoubleDisposal.OfAsync(lease);

        owned.DisposeCount.Should().Be(1);
    }
}
