// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static EdFi.DataManagementService.Tests.Integration.Scenarios.AuthoritativeResponseMetadataScenario;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static class AuthoritativeTimestampReadConsistencyScenario
{
    // A watchdog bounds a broken test; ordering comes exclusively from Arrived/Release.
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(90);

    public static void Configure(IServiceCollection services, string connectionString, bool postgresql)
    {
        ServiceDescriptor hydrator = services.Last(service =>
            service.ServiceType == typeof(IDocumentHydrator)
        );
        RecordHydrationCommands(services, connectionString, postgresql);
        services.AddSingleton(new ReadBoundary(connectionString, postgresql));
        services.Replace(
            ServiceDescriptor.Scoped<IDocumentHydrator>(provider => new BoundaryHydrator(
                (IDocumentHydrator)(
                    hydrator.ImplementationInstance
                    ?? hydrator.ImplementationFactory?.Invoke(provider)
                    ?? ActivatorUtilities.CreateInstance(provider, hydrator.ImplementationType!)
                ),
                provider.GetRequiredService<ReadBoundary>()
            ))
        );
        services.Replace(
            ServiceDescriptor.Scoped<
                IDocumentCacheReadAccelerationCoordinator,
                RelationalSelectionCoordinator
            >()
        );
        ServiceDescriptor descriptor = services.Last(service =>
            service.ServiceType == typeof(IRelationalCommandExecutor)
        );
        services.Remove(descriptor);
        services.AddScoped<IRelationalCommandExecutor>(provider => new BoundaryCommandExecutor(
            (IRelationalCommandExecutor)(
                descriptor.ImplementationInstance
                ?? descriptor.ImplementationFactory?.Invoke(provider)
                ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!)
            ),
            provider.GetRequiredService<ReadBoundary>()
        ));
    }

    public static async Task RunAsync(
        ApiIntegrationHarness harness,
        bool descriptor,
        bool page,
        bool restamp,
        bool betweenMetadataAndContent,
        bool selectedDescriptorFallback = false
    )
    {
        Resource resource = await CreateAsync(harness, descriptor);
        Resource neighbor = await CreateAsync(harness, descriptor);
        await BackdateAsync(harness, resource);
        await BackdateAsync(harness, neighbor, 1);
        Stamp beforeStamp = await ReadStampAsync(harness, resource);
        Stamp neighborStamp = await ReadStampAsync(harness, neighbor);
        JsonObject before = await ReadAsync(harness, resource.Path);
        JsonObject neighborBefore = await ReadAsync(harness, neighbor.Path);
        AssertStamp(before, beforeStamp);
        AssertStamp(neighborBefore, neighborStamp);

        ReadBoundary boundary = harness.Services.GetRequiredService<ReadBoundary>();
        boundary.Descriptor = descriptor;
        boundary.SelectedDescriptorFallback = selectedDescriptorFallback;
        boundary.BetweenMetadataAndContent = betweenMetadataAndContent;
        boundary.Armed = true;
        boundary.Gate.Arm();
        using CancellationTokenSource cancellation = new(Watchdog);
        Task<HttpResponseMessage> pending = harness.HttpClient.GetAsync(
            page ? resource.Endpoint : resource.Path,
            cancellation.Token
        );
        JsonObject after = null!;
        try
        {
            Task reached = await Task.WhenAny(boundary.Gate.Arrived, pending).WaitAsync(cancellation.Token);
            reached
                .Should()
                .BeSameAs(
                    boundary.Gate.Arrived,
                    $"the request must reach the arranged boundary; {boundary.Failure}"
                );
            await TestContext.Out.WriteLineAsync(
                $"Arrived: {boundary.Checkpoint}; descriptor={descriptor}; page={page}; restamp={restamp}; before={beforeStamp.Version}/{beforeStamp.ModifiedAt:O}"
            );
            if (restamp)
            {
                await RestampAsync(harness, resource, beforeStamp);
            }
            else
            {
                resource.Body[descriptor ? "shortDescription" : "displayName"] = "Committed during read";
                using HttpResponseMessage changed = await PutAsync(
                        harness,
                        resource,
                        before["_etag"]!.GetValue<string>()
                    )
                    .WaitAsync(cancellation.Token);
                changed
                    .StatusCode.Should()
                    .Be(HttpStatusCode.NoContent, await changed.Content.ReadAsStringAsync());
            }
            Stamp afterStamp = await ReadStampAsync(harness, resource);
            afterStamp.Version.Should().BeGreaterThan(beforeStamp.Version);
            afterStamp.ModifiedAt.Should().BeAfter(beforeStamp.ModifiedAt);
            after = await ReadAsync(harness, resource.Path);
            AssertStamp(after, afterStamp);
            after["_lastModifiedDate"]!
                .GetValue<string>()
                .Should()
                .NotBe(before["_lastModifiedDate"]!.GetValue<string>());
            after["_etag"]!.GetValue<string>().Should().NotBe(before["_etag"]!.GetValue<string>());
            string field = descriptor ? "shortDescription" : "displayName";
            after[field]!
                .GetValue<string>()
                .Should()
                .Be(restamp ? before[field]!.GetValue<string>() : "Committed during read");
            (await ReadStampAsync(harness, neighbor)).Should().Be(neighborStamp);
            await TestContext.Out.WriteLineAsync(
                $"Committed: {afterStamp.Version}/{afterStamp.ModifiedAt:O}; releasing {boundary.Checkpoint}"
            );
        }
        finally
        {
            boundary.Gate.Release();
            // Always drain the held HTTP request before the database lease is returned, including on assertion failure.
            try
            {
                await pending.WaitAsync(Watchdog);
            }
            catch
            {
                await cancellation.CancelAsync();
            }
        }

        using HttpResponseMessage response = await pending;
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        JsonNode result = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        JsonObject returned;
        if (page)
        {
            JsonObject[] items = result.AsArray().Select(item => item!.AsObject()).ToArray();
            items.Should().HaveCount(2);
            returned = items.Single(item => item["id"]!.GetValue<string>() == resource.Uuid.ToString());
            JsonObject returnedNeighbor = items.Single(item =>
                item["id"]!.GetValue<string>() == neighbor.Uuid.ToString()
            );
            JsonNode
                .DeepEquals(returnedNeighbor, neighborBefore)
                .Should()
                .BeTrue("a distinct neighboring owner must retain its own body and stamps");
        }
        else
        {
            returned = result.AsObject();
            response.TryReadRawEtag(out string etag).Should().BeTrue();
            etag.Should().Be(returned["_etag"]!.GetValue<string>());
        }
        // The expected side is fixed by the boundary, never inferred from post-race database contents.
        JsonObject expected = descriptor && !selectedDescriptorFallback ? before : after;
        JsonNode
            .DeepEquals(returned, expected)
            .Should()
            .BeTrue(
                $"{boundary.Checkpoint} must return the complete {(ReferenceEquals(expected, before) ? "before" : "after")} state; expected {expected}; actual {returned}"
            );
        boundary.Hits.Should().Be(1);
    }

    private static async Task<JsonObject> ReadAsync(ApiIntegrationHarness harness, string path)
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private static void AssertStamp(JsonObject body, Stamp stamp)
    {
        body["_etag"]!.GetValue<string>().Should().StartWith($"{stamp.Version}-");
        body["_lastModifiedDate"]!
            .GetValue<string>()
            .Should()
            .Be(LastModifiedDateFormatter.Format(stamp.ModifiedAt));
    }

    private sealed class ReadBoundary(string connectionString, bool postgresql)
    {
        public string ConnectionString { get; } = connectionString;
        public bool Postgresql { get; } = postgresql;
        public HydrationGate Gate { get; } = new();
        public bool Descriptor { get; set; }
        public bool SelectedDescriptorFallback { get; set; }
        public bool BetweenMetadataAndContent { get; set; }
        public string Checkpoint { get; private set; } = "";
        public string Failure { get; set; } = "";
        public int Hits { get; private set; }
        private int _claimed;

        public bool Claim(string checkpoint)
        {
            // Gate is armed only after setup reads. An explicit flag is necessary because HydrationGate
            // deliberately exposes only signals, not implementation state.
            if (!Armed || Interlocked.Exchange(ref _claimed, 1) != 0)
            {
                return false;
            }
            Checkpoint = checkpoint;
            Hits++;
            return true;
        }

        public bool Armed { get; set; }
    }

    private sealed class BoundaryHydrator(IDocumentHydrator inner, ReadBoundary boundary) : IDocumentHydrator
    {
        public async Task<HydratedPage> HydrateAsync(
            ResourceReadPlan plan,
            PageKeysetSpec keyset,
            HydrationExecutionOptions executionOptions,
            CancellationToken ct
        )
        {
            if (
                !boundary.Descriptor
                && !boundary.BetweenMetadataAndContent
                && boundary.Claim("UUID lookup or page request before hydration")
            )
            {
                await boundary.Gate.WaitIfArmedAsync();
            }
            if (!boundary.BetweenMetadataAndContent)
            {
                return await inner.HydrateAsync(plan, keyset, executionOptions, ct);
            }
            await using DbConnection connection = boundary.Postgresql
                ? new NpgsqlConnection(boundary.ConnectionString)
                : new SqlConnection(boundary.ConnectionString);
            await connection.OpenAsync(ct);
            DbConnection decorated = A.Fake<DbConnection>(options => options.Wrapping(connection));
            A.CallTo(decorated)
                .WithReturnType<DbCommand>()
                .Where(call => call.Method.Name == "CreateDbCommand")
                .ReturnsLazily(() => WrapCommand(connection.CreateCommand(), boundary));
            return await ConsistentHydrationExecutor.ExecuteAsync(
                decorated,
                plan,
                keyset,
                boundary.Postgresql ? SqlDialect.Pgsql : SqlDialect.Mssql,
                executionOptions,
                ct
            );
        }

        private static DbCommand WrapCommand(DbCommand command, ReadBoundary boundary)
        {
            DbCommand decorated = A.Fake<DbCommand>(options => options.Wrapping(command));
            A.CallTo(decorated)
                .WithReturnType<Task<DbDataReader>>()
                .Where(call => call.Method.Name == "ExecuteDbDataReaderAsync")
                .ReturnsLazily(async call =>
                {
                    try
                    {
                        CancellationToken ct = call.GetArgument<CancellationToken>(1);
                        if (
                            !boundary.BetweenMetadataAndContent
                            || !boundary.Claim("metadata acquired before content statements")
                        )
                        {
                            return await command.ExecuteReaderAsync(ct);
                        }

                        // Split only at the metadata SELECT's terminator. Execute the unmodified generated
                        // statements on the same real connection/transaction, buffering their actual result
                        // sets. This prevents provider/server read-ahead from crossing the arranged boundary.
                        // PostgreSQL's batch has an implicit READ COMMITTED transaction: keep its
                        // ON COMMIT DROP keyset alive across the split without adding snapshot isolation.
                        await using DbTransaction transaction =
                            await command.Connection!.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                        command.Transaction = transaction;
                        string sql = command.CommandText;
                        int metadata = sql.IndexOf("ContentLastModifiedAt", StringComparison.Ordinal);
                        metadata.Should().BeGreaterThanOrEqualTo(0);
                        int split = sql.IndexOf(';', metadata) + 1;
                        split.Should().BeGreaterThan(0).And.BeLessThan(sql.Length);
                        command.CommandText = sql[..split];
                        if (
                            !boundary.Postgresql
                            && command.CommandText.Contains("CREATE TABLE", StringComparison.Ordinal)
                        )
                        {
                            // A parameterized SqlCommand runs in sp_executesql's scope. Create the temp
                            // keyset in the session scope so it survives the two commands of this test.
                            int createTable = command.CommandText.IndexOf(
                                "CREATE TABLE",
                                StringComparison.Ordinal
                            );
                            int preludeEnd = command.CommandText.IndexOf(';', createTable) + 1;
                            preludeEnd.Should().BeGreaterThan(0);
                            await using DbCommand prelude = command.Connection!.CreateCommand();
                            prelude.Transaction = command.Transaction;
                            prelude.CommandText = command.CommandText[..preludeEnd];
                            await prelude.ExecuteNonQueryAsync(ct);
                            command.CommandText = command.CommandText[preludeEnd..];
                        }
                        List<DataTable> tables = await BufferAsync(command, ct);
                        await boundary.Gate.WaitIfArmedAsync();
                        command.CommandText = sql[split..];
                        tables.AddRange(await BufferAsync(command, ct));
                        await transaction.CommitAsync(ct);
                        return new DataTableReader(tables.ToArray());
                    }
                    catch (Exception exception)
                    {
                        boundary.Failure = exception.ToString();
                        throw;
                    }
                });
            return decorated;
        }

        private static async Task<List<DataTable>> BufferAsync(DbCommand command, CancellationToken ct)
        {
            List<DataTable> tables = [];
            await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
            do
            {
                if (reader.FieldCount == 0)
                {
                    continue;
                }
                DataTable table = new();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    table.Columns.Add(reader.GetName(i), reader.GetFieldType(i));
                }
                while (await reader.ReadAsync(ct))
                {
                    object[] values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    table.Rows.Add(values);
                }
                tables.Add(table);
            } while (await reader.NextResultAsync(ct));
            return tables;
        }
    }

    // The disabled-cache HTTP path selects full descriptor rows atomically. Supplemental cases
    // invoke the handler's real selection/fallback callbacks directly to cover its separate candidate
    // boundary, still without cache lookup, publication, or materialization. Ordinary cases use the
    // exact same fallback callback as the production disabled coordinator.
    private sealed class RelationalSelectionCoordinator(ReadBoundary boundary)
        : IDocumentCacheReadAccelerationCoordinator
    {
        public async Task<GetResult> GetByIdAsync(
            DocumentCacheReadAccelerationGetByIdRequest request,
            CancellationToken cancellationToken = default
        )
        {
            if (!boundary.SelectedDescriptorFallback || !boundary.Armed)
            {
                return await request.RelationalFallback(cancellationToken);
            }
            var selection = await request.SelectAuthorizedCandidate(cancellationToken);
            var selected = selection
                .Should()
                .BeOfType<DocumentCacheReadAccelerationGetByIdSelectionResult.Candidate>()
                .Subject;
            if (boundary.Claim("descriptor candidate before relational rehydration"))
            {
                await boundary.Gate.WaitIfArmedAsync();
            }
            return await selected.RelationalFallback(cancellationToken);
        }

        public async Task<QueryResult> QueryAsync(
            DocumentCacheReadAccelerationQueryRequest request,
            CancellationToken cancellationToken = default
        )
        {
            if (!boundary.SelectedDescriptorFallback || !boundary.Armed)
            {
                return await request.RelationalFallback(cancellationToken);
            }
            var selection = await request.SelectAuthorizedCandidatePage(cancellationToken);
            var selected = selection
                .Should()
                .BeOfType<DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage>()
                .Subject;
            if (boundary.Claim("descriptor candidate page before relational rehydration"))
            {
                await boundary.Gate.WaitIfArmedAsync();
            }
            return await selected.RelationalFallback(cancellationToken);
        }
    }

    private sealed class BoundaryCommandExecutor(IRelationalCommandExecutor inner, ReadBoundary boundary)
        : IRelationalCommandExecutor
    {
        public SqlDialect Dialect => inner.Dialect;

        public async Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            TResult result = await inner.ExecuteReaderAsync(command, readAsync, cancellationToken);
            // Uncached descriptors select their body and metadata together. Hold the captured row(s),
            // then commit the competing write before authorization/materialization continues.
            if (
                boundary.Descriptor
                && command.CommandText.Contains("ContentLastModifiedAt", StringComparison.Ordinal)
                && command.CommandText.Contains("ShortDescription", StringComparison.Ordinal)
                && boundary.Claim("descriptor full row selection before materialization")
            )
            {
                await boundary.Gate.WaitIfArmedAsync();
            }
            return result;
        }
    }
}
