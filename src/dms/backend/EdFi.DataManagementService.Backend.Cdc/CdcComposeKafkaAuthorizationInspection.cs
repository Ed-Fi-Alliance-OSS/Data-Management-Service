// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>Live authority for the pinned, authorization-disabled local Apache Kafka deployment only.</summary>
public sealed class CdcComposeKafkaAuthorizationInspection : ICdcKafkaAuthorizationInspection
{
    private readonly string _project;
    private readonly string _adminBootstrapServers;
    private readonly string _workerBootstrapServers;
    private readonly ICdcWorkerDockerCommand _docker;

    public CdcComposeKafkaAuthorizationInspection(
        string project,
        string adminBootstrapServers,
        string workerBootstrapServers
    )
        : this(project, adminBootstrapServers, workerBootstrapServers, new CdcWorkerDockerCommand()) { }

    internal CdcComposeKafkaAuthorizationInspection(
        string project,
        string adminBootstrapServers,
        string workerBootstrapServers,
        ICdcWorkerDockerCommand docker
    )
    {
        _project = project;
        _adminBootstrapServers = adminBootstrapServers;
        _workerBootstrapServers = workerBootstrapServers;
        _docker = docker;
    }

    public async Task<CdcTransportResult<CdcKafkaAuthorizationDeploymentEvidence>> InspectAsync(
        IReadOnlyList<int> brokerIds,
        CancellationToken cancellationToken
    )
    {
        try
        {
            Require(brokerIds.Count == 1);
            string id = (
                await _docker.RunAsync(
                    [
                        "ps",
                        "--filter",
                        "label=com.docker.compose.project=" + _project,
                        "--filter",
                        "label=com.docker.compose.service=kafka",
                        "--format",
                        "{{.ID}}",
                        "--no-trunc",
                    ],
                    cancellationToken
                )
            ).Trim();
            Require(id.Length == 64 && id.All(char.IsAsciiHexDigit));
            string before = await _docker.RunAsync(["inspect", id], cancellationToken);
            using var document = JsonDocument.Parse(before);
            var container = document.RootElement.EnumerateArray().Single();
            Require(
                container.GetProperty("Id").GetString() == id
                    && container.GetProperty("State").GetProperty("Running").GetBoolean()
            );
            var config = container.GetProperty("Config");
            Require(
                config.GetProperty("Image").GetString() == CdcComposeBrokerSizeDeployment.BrokerImage
                    && config.GetProperty("Labels").GetProperty("com.docker.compose.project").GetString()
                        == _project
                    && config.GetProperty("Labels").GetProperty("com.docker.compose.service").GetString()
                        == "kafka"
            );
            // Read the file actually passed to Kafka's JVM, not requested Compose environment values.
            using var image = JsonDocument.Parse(
                await _docker.RunAsync(
                    ["image", "inspect", CdcComposeBrokerSizeDeployment.BrokerImage],
                    cancellationToken
                )
            );
            var imageInfo = image.RootElement.EnumerateArray().Single();
            Require(imageInfo.GetProperty("Id").GetString() == container.GetProperty("Image").GetString());
            Require(
                imageInfo
                    .GetProperty("RepoDigests")
                    .EnumerateArray()
                    .Any(d =>
                        d.GetString()!
                            .EndsWith(
                                CdcComposeBrokerSizeDeployment.BrokerImage[
                                    CdcComposeBrokerSizeDeployment.BrokerImage.IndexOf('@')..
                                ],
                                StringComparison.Ordinal
                            )
                    )
            );
            string properties = await _docker.RunAsync(
                ["exec", id, "sh", "-ec", InspectionScript],
                cancellationToken
            );
            var parsed = Parse(properties);
            Require(parsed["node.id"] == brokerIds[0].ToString(CultureInfo.InvariantCulture));
            // This adapter qualifies two explicit endpoints of one local broker, not aliases or clusters.
            string[][] listeners = parsed["advertised.listeners"]
                .Split(',')
                .Select(x => x.Split("://", StringSplitOptions.None))
                .ToArray();
            Require(Array.TrueForAll(listeners, x => x.Length == 2 && x[0].Length > 0 && x[1].Length > 0));
            string[] endpoints = listeners.Select(x => x[1]).ToArray();
            Require(
                endpoints.Contains(_adminBootstrapServers, StringComparer.Ordinal)
                    && endpoints.Contains(_workerBootstrapServers, StringComparer.Ordinal)
            );
            Require(
                !parsed.TryGetValue("authorizer.class.name", out var authorizer) || authorizer.Length == 0
            );
            Require(!parsed.TryGetValue("super.users", out var superusers) || superusers.Length == 0);
            Require(
                Array.TrueForAll(
                    parsed["listener.security.protocol.map"].Split(','),
                    x => x.EndsWith(":PLAINTEXT", StringComparison.Ordinal)
                )
            );
            using var after = JsonDocument.Parse(await _docker.RunAsync(["inspect", id], cancellationToken));
            var final = after.RootElement.EnumerateArray().Single();
            Require(
                final.GetProperty("State").GetRawText() == container.GetProperty("State").GetRawText()
                    && final.GetProperty("RestartCount").GetInt32()
                        == container.GetProperty("RestartCount").GetInt32()
            );
            Require(
                properties
                    == await _docker.RunAsync(["exec", id, "sh", "-ec", InspectionScript], cancellationToken)
            );
            return new CdcTransportResult<CdcKafkaAuthorizationDeploymentEvidence>.Observed(
                new(true, [new(brokerIds[0], false, false, [])], [])
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new CdcTransportResult<CdcKafkaAuthorizationDeploymentEvidence>.Unavailable(
                CdcDeploymentDiagnostic.FromException(CdcDeploymentComponent.Kafka, exception)
            );
        }
    }

    internal static Dictionary<string, string> Parse(string text)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            int separator = line.IndexOf('=');
            Require(separator > 0 && !line.Contains('\\'));
            Require(result.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim()));
        }
        return result;
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new InvalidDataException();
        }
    }

    // Fixed command: only the supported Kafka process and generated properties path are accepted.
    private const string InspectionScript = """
        set -- /proc/[0-9]*/cmdline
        count=0
        for process in "$@"; do
          if tr '\000' '\n' < "$process" | grep -Fx 'kafka.Kafka' >/dev/null; then
            suffix=$(tr '\000' '\n' < "$process" | sed -n '/^kafka.Kafka$/,$p')
            test "$suffix" = "$(printf 'kafka.Kafka\n/opt/kafka/config/server.properties')"
            count=$((count + 1))
          fi
        done
        test "$count" -eq 1
        cat /opt/kafka/config/server.properties
        """;
}
