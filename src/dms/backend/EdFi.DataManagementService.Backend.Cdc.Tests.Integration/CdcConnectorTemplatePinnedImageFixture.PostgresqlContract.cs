// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using FluentAssertions;

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
            if (_isolateSourceProducer && key == "producer.override.bootstrap.servers")
            {
                expected = $"{ConnectContainerName}:19094";
            }
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
        await (await CreateProviderObserverAsync(token)).AssertCaptureInventoryAsync(token);
    }

    public Task UpdateCanonicalRowAsync(JsonElement row, CancellationToken token) =>
        WriteRowAsync(row, Ddl.CdcSourceTableKind.Document, true, token);

    public Task UpdateProjectionWorkAsync(long documentId, CancellationToken token) =>
        ExecuteProviderMutationAsync(
            $"UPDATE {Quote("dms")}.{Quote("DocumentProjectionWork")} SET {Quote("RequiredContentVersion")} = 2, "
                + $"{Quote("LastEnqueuedAt")} = {CurrentTimestamp} WHERE {Quote("DocumentId")} = @DocumentId",
            documentId,
            token
        );

    public Task DeleteProjectionWorkAsync(long documentId, CancellationToken token) =>
        ExecuteProviderMutationAsync(
            $"DELETE FROM {Quote("dms")}.{Quote("DocumentProjectionWork")} WHERE {Quote("DocumentId")} = @DocumentId",
            documentId,
            token
        );

    public async Task TruncatePostgresqlDocumentsAsync(CancellationToken token)
    {
        await ReadPostgresqlScalarAsync("TRUNCATE TABLE \"dms\".\"Document\" CASCADE; SELECT 'done';", token);
    }

    public async Task<MessageContractPostgresqlFence> FencePostgresqlSourceAsync(
        CdcConnectorTemplateRequest request,
        string phase,
        CancellationToken token
    ) => await (await CreateProviderFencesAsync(request, token)).FencePostgresqlSourceAsync(phase, token);
}
