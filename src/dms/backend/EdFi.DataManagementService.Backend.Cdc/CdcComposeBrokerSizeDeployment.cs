// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using static EdFi.DataManagementService.Backend.Cdc.CdcWorkflowJournalValidation;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Static size settings for the repository's isolated single-broker Apache Kafka Compose deployment.
/// Persists a size-only override before recreating just the broker with its existing volumes. Every
/// later infrastructure startup must include this same override; worker startup supports that path.
/// Other deployments supply their own broker authority. The caller retains the controller lock.
/// </summary>
public sealed class CdcComposeBrokerSizeDeployment : ICdcKafkaBrokerSizeDeployment
{
    internal const string BrokerImage =
        "apache/kafka:3.9.0@sha256:fbc7d7c428e3755cf36518d4976596002477e4c052d1f80b5b9eafd06d0fff2f";
    private readonly string[] _arguments;
    private readonly string _project;
    private readonly string _override;
    private readonly ICdcWorkerDockerCommand _docker;

    public CdcComposeBrokerSizeDeployment(
        string composeFile,
        string environmentFile,
        string project,
        string sizeOverrideFile
    )
        : this(composeFile, environmentFile, project, sizeOverrideFile, new CdcWorkerDockerCommand())
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFile);
    }

    internal CdcComposeBrokerSizeDeployment(
        string composeFile,
        string environmentFile,
        string project,
        string sizeOverrideFile,
        ICdcWorkerDockerCommand docker
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(sizeOverrideFile);
        _arguments =
        [
            "compose",
            "-f",
            Path.GetFullPath(composeFile),
            "--env-file",
            Path.GetFullPath(environmentFile),
            "-p",
            project,
        ];
        _project = project;
        _override = Path.GetFullPath(sizeOverrideFile);
        _docker = docker;
    }

    public async Task ApplyAsync(
        CdcDeploymentRequest request,
        IReadOnlyList<CdcKafkaBrokerCapacity> limits,
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timing.CallTimeout);
        var token = timeout.Token;
        Require(
            request.WorkerPolicy.DurabilityProfile == CdcKafkaDurabilityProfile.LocalSingleBroker
                && limits.Count == 1
        );
        var limit = limits.Single();
        Require(
            limit.BrokerId >= 0
                && limit.SocketRequestMaxBytes >= request.ConnectorPolicy.MaxRecordBytes
                && limit.ReplicaFetchMaxBytes >= request.ConnectorPolicy.MaxRecordBytes
                && limit.ReplicaFetchResponseMaxBytes >= request.ConnectorPolicy.MaxRecordBytes
        );
        ValidatePath();
        Dictionary<string, string> environment = new()
        {
            ["KAFKA_SOCKET_REQUEST_MAX_BYTES"] = limit.SocketRequestMaxBytes.ToString(
                CultureInfo.InvariantCulture
            ),
            ["KAFKA_REPLICA_FETCH_MAX_BYTES"] = limit.ReplicaFetchMaxBytes.ToString(
                CultureInfo.InvariantCulture
            ),
            ["KAFKA_REPLICA_FETCH_RESPONSE_MAX_BYTES"] = limit.ReplicaFetchResponseMaxBytes.ToString(
                CultureInfo.InvariantCulture
            ),
        };
        if (File.Exists(_override))
        {
            // Only this exact schema is accepted. A retained stronger override is never lowered after
            // a crash before broker recreation, even if the running broker still exposes old values.
            using var existing = JsonDocument.Parse(await File.ReadAllTextAsync(_override, token));
            var retained = existing
                .RootElement.GetProperty("services")
                .GetProperty("kafka")
                .GetProperty("environment");
            Require(
                existing.RootElement.EnumerateObject().Count() == 1
                    && existing.RootElement.GetProperty("services").EnumerateObject().Count() == 1
                    && existing
                        .RootElement.GetProperty("services")
                        .GetProperty("kafka")
                        .EnumerateObject()
                        .Count() == 1
                    && retained.EnumerateObject().Count() == environment.Count
            );
            foreach (var property in retained.EnumerateObject())
            {
                Require(environment.ContainsKey(property.Name));
                environment[property.Name] = Math.Max(
                        long.Parse(environment[property.Name], CultureInfo.InvariantCulture),
                        CdcRecordSizeRollout.Number(property.Value.GetString()!)
                    )
                    .ToString(CultureInfo.InvariantCulture);
            }
        }
        var args = Arguments();
        using var compose = JsonDocument.Parse(
            await _docker.RunAsync([.. args, "config", "--format", "json"], token)
        );
        var broker = compose.RootElement.GetProperty("services").GetProperty("kafka");
        Require(
            broker.GetProperty("image").GetString() == BrokerImage
                && !broker.TryGetProperty("depends_on", out _)
        );
        var config = broker.GetProperty("environment");
        Require(
            config.GetProperty("KAFKA_NODE_ID").GetString()
                == limit.BrokerId.ToString(CultureInfo.InvariantCulture)
        );
        var advertised = config
            .GetProperty("KAFKA_ADVERTISED_LISTENERS")
            .GetString()!
            .Split(',')
            .Select(listener => listener[(listener.IndexOf("://", StringComparison.Ordinal) + 3)..])
            .ToArray();
        Require(advertised.Contains(request.ConnectorPolicy.KafkaBootstrapServers, StringComparer.Ordinal));
        string id = (await _docker.RunAsync([.. args, "ps", "--all", "--quiet", "kafka"], token)).Trim();
        Require(id.Length == 64 && id.All(char.IsAsciiHexDigit));
        using var inspected = JsonDocument.Parse(await _docker.RunAsync(["inspect", id], token));
        var actual = inspected.RootElement.EnumerateArray().Single();
        Require(
            actual.GetProperty("Id").GetString() == id
                && actual.GetProperty("State").GetProperty("Running").GetBoolean()
        );
        var actualConfig = actual.GetProperty("Config");
        Require(
            actualConfig.GetProperty("Image").GetString() == BrokerImage
                && actualConfig.GetProperty("Labels").GetProperty("com.docker.compose.project").GetString()
                    == _project
                && actualConfig.GetProperty("Labels").GetProperty("com.docker.compose.service").GetString()
                    == "kafka"
                && actualConfig
                    .GetProperty("Env")
                    .EnumerateArray()
                    .Any(e =>
                        e.GetString()
                        == "KAFKA_NODE_ID=" + limit.BrokerId.ToString(CultureInfo.InvariantCulture)
                    )
        );
        var actualEnvironment = actualConfig
            .GetProperty("Env")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToDictionary(e => e[..e.IndexOf('=')], e => e[(e.IndexOf('=') + 1)..], StringComparer.Ordinal);
        foreach (var property in config.EnumerateObject().Where(p => !environment.ContainsKey(p.Name)))
        {
            Require(
                actualEnvironment.TryGetValue(property.Name, out var value)
                    && value == property.Value.GetString()
            );
        }
        // Preserve stronger configured values even when they have not become live yet.
        foreach (string key in environment.Keys.ToArray())
        {
            if (config.TryGetProperty(key, out var configured))
            {
                environment[key] = Math.Max(
                        long.Parse(environment[key], CultureInfo.InvariantCulture),
                        CdcRecordSizeRollout.Number(configured.GetString()!)
                    )
                    .ToString(CultureInfo.InvariantCulture);
            }
        }
        var payload = JsonSerializer.Serialize(new { services = new { kafka = new { environment } } });
        string temporary = _override + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (
                var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            )
            {
                await file.WriteAsync(System.Text.Encoding.UTF8.GetBytes(payload), token);
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            ValidatePath();
            File.Move(temporary, _override, overwrite: true);
            LocalCdcWorkflowJournalStore.FlushDirectory(Path.GetDirectoryName(_override)!);
        }
        finally
        {
            File.Delete(temporary);
        }
        await _docker.RunAsync(
            [
                .. Arguments(),
                "up",
                "--detach",
                "--no-deps",
                "--wait",
                "--wait-timeout",
                Math.Max(1, (int)request.Timing.CallTimeout.TotalSeconds)
                    .ToString(CultureInfo.InvariantCulture),
                "kafka",
            ],
            token
        );
    }

    private string[] Arguments() => File.Exists(_override) ? [.. _arguments, "-f", _override] : _arguments;

    private void ValidatePath()
    {
        Require(Directory.Exists(Path.GetDirectoryName(_override)));
        FileSystemInfo entry = new FileInfo(_override);
        while (true)
        {
            Require(entry.LinkTarget is null);
            if (Path.GetDirectoryName(entry.FullName) is not { } parent)
            {
                break;
            }
            entry = new DirectoryInfo(parent);
        }
    }
}
