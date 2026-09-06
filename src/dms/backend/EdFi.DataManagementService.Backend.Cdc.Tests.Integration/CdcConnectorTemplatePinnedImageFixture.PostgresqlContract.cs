// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed partial class CdcConnectorTemplatePinnedImageFixture
{
    public async Task InstallSourceObserverAsync(CancellationToken token)
    {
        string source = Path.Combine(
            AppContext.BaseDirectory,
            "MessageContractRunner",
            "MessageContractSourceObserver.java"
        );
        await _docker.RunAsync(
            ["cp", source, $"{ConnectContainerName}:/tmp/MessageContractSourceObserver.java"],
            token
        );
        string script = $$"""
            set -eu
            {{CdcPinnedImageJavaRuntime.ClassPathScript}}
            java -cp "${class_path}" /tmp/MessageContractSourceObserver.java
            """;
        DockerCommandResult compile = await _docker.RunAllowingFailureAsync(
            ["exec", "--user", "0", ConnectContainerName, "sh", "-lc", script],
            token
        );
        compile
            .ExitCode.Should()
            .Be(0, "test source observer must compile in the qualified image (output redacted)");
        await _docker.RunAsync(["restart", ConnectContainerName], token);
        // Docker can allocate a new ephemeral host port when the worker container restarts.
        _httpClient.Dispose();
        _httpClient = new HttpClient { BaseAddress = await ReadMappedConnectBaseUriAsync(token) };
        await WaitForKafkaConnectAsync(token);
    }

    public async Task<IReadOnlyList<JsonElement>> ReadSourceObservationsAsync(CancellationToken token)
    {
        DockerCommandResult output = await _docker.RunAsync(
            ["exec", ConnectContainerName, "cat", "/tmp/message-contract-source-observations.jsonl"],
            token
        );
        // The observer writes only a fixed, bounded metadata allowlist. Never read Connect logs or source values here.
        return output
            .StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .ToArray();
    }

    public async Task AssertObservedConnectorConfigAsync(
        CdcConnectorTemplateResult rendered,
        CancellationToken token
    )
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(
            $"/connectors/{rendered.ConnectorName.Value}/config",
            token
        );
        response.IsSuccessStatusCode.Should().BeTrue();
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        foreach (var (key, value) in rendered.Config)
        {
            string expected = key == "transforms" ? $"contractObserver,{value}" : value;
            (json.RootElement.GetProperty(key).GetString() == expected)
                .Should()
                .BeTrue($"generated property {key} must remain effective (values redacted)");
        }
        json.RootElement.GetProperty("transforms.contractObserver.type")
            .GetString()
            .Should()
            .Be("org.edfi.contract.MessageContractSourceObserver");
        (
            json.RootElement.GetProperty("transforms.contractObserver.expected.server").GetString()
            == rendered.ConnectorName.Value
        )
            .Should()
            .BeTrue();
    }

    public async Task AssertPostgresqlCaptureInventoryAsync(CancellationToken token)
    {
        await AssertMessageContractSourceLayoutAsync(token);
        string inventory = await ReadPostgresqlScalarAsync(
            $"""
            SELECT string_agg(schemaname || '.' || tablename, ',' ORDER BY tablename)
            FROM pg_publication_tables WHERE pubname = '{PostgresqlPublicationName}';
            """,
            token
        );
        inventory.Trim().Should().Be("dms.CdcHeartbeat,dms.Document,dms.DocumentCache");
        string fullIdentity = await ReadPostgresqlScalarAsync(
            """
            SELECT string_agg(relname, ',' ORDER BY relname) FROM pg_class
            WHERE oid IN ('dms."Document"'::regclass, 'dms."DocumentCache"'::regclass) AND relreplident = 'f';
            """,
            token
        );
        fullIdentity.Trim().Should().Be("Document");
    }

    public Task UpdateCanonicalRowAsync(JsonElement row, CancellationToken token) =>
        WriteRowAsync(row, Ddl.CdcSourceTableKind.Document, true, token);

    public Task UpdateProjectionWorkAsync(long documentId, CancellationToken token) =>
        ExecuteProviderMutationAsync(
            $"""
            UPDATE "dms"."DocumentProjectionWork" SET "RequiredContentVersion" = 2,
            "LastEnqueuedAt" = clock_timestamp() WHERE "DocumentId" = @DocumentId
            """,
            documentId,
            token
        );

    public Task DeleteProjectionWorkAsync(long documentId, CancellationToken token) =>
        ExecuteProviderMutationAsync(
            "DELETE FROM \"dms\".\"DocumentProjectionWork\" WHERE \"DocumentId\" = @DocumentId",
            documentId,
            token
        );

    public async Task TruncatePostgresqlDocumentsAsync(CancellationToken token)
    {
        await ReadPostgresqlScalarAsync("TRUNCATE TABLE \"dms\".\"Document\" CASCADE; SELECT 'done';", token);
    }

    /// <summary>Capture WAL after the preceding mutations, then require committed lsn_proc at/beyond it.
    /// A heartbeat after the capture drives retained output. Neither a topic end nor heartbeat value is the fence.</summary>
    public async Task<MessageContractPostgresqlFence> FencePostgresqlSourceAsync(
        CdcConnectorTemplateRequest request,
        string phase,
        CancellationToken token
    )
    {
        string wal = await ReadPostgresqlScalarAsync("SELECT pg_current_wal_lsn()::text;", token);
        string[] parts = wal.Split('/');
        ulong barrier =
            (ulong.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture) << 32)
            | ulong.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        await AdvanceHeartbeatAsync(token);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await AssertRegisteredConnectorReachesRunningStateAsync(request, token);
            var observed = await TryReadCommittedSourceOffsetAsync(request, token);
            if (observed is not null)
            {
                observed.SourcePartitionEvidence.Properties.Count.Should().Be(1);
                (observed.SourcePartitionEvidence.Properties["server"] == request.ConnectorName.Value)
                    .Should()
                    .BeTrue();
                using JsonDocument offset = JsonDocument.Parse(observed.CanonicalOffsetJson);
                if (
                    offset.RootElement.TryGetProperty("lsn_proc", out var lsn)
                    && lsn.TryGetUInt64(out ulong processed)
                    && processed >= barrier
                )
                {
                    await TestContext.Out.WriteLineAsync(
                        $"{phase}: WAL barrier={barrier}, committed lsn_proc={processed}, matching single server partition"
                    );
                    return new(phase, barrier, processed);
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        }
        throw new AssertionException(
            $"PostgreSQL {phase} committed source fence timed out; WAL barrier={barrier}. Details redacted."
        );
    }
}

internal sealed record MessageContractPostgresqlFence(string Phase, ulong WalBarrier, ulong CommittedLsnProc);
