// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Inspects one explicitly selected local Compose worker. Docker authority is required; a REST
/// address or requested environment is not process evidence. Unsupported deployments stay unknown.
/// The caller owns Docker access and must exclude unmanaged changes during controller mutations.
/// </summary>
public sealed class CdcWorkerDeployment : ICdcWorkerInspectionTransport
{
    private readonly string _project;
    private readonly string _service;
    private readonly ICdcWorkerDockerCommand _docker;
    private readonly IReadOnlySet<string> _qualifiedDigests;

    public CdcWorkerDeployment(
        string composeProject,
        string composeService,
        IReadOnlySet<string> qualifiedDigests
    )
        : this(composeProject, composeService, qualifiedDigests, new CdcWorkerDockerCommand()) { }

    internal CdcWorkerDeployment(
        string composeProject,
        string composeService,
        IReadOnlySet<string> qualifiedDigests,
        ICdcWorkerDockerCommand docker
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeProject);
        ArgumentException.ThrowIfNullOrWhiteSpace(composeService);
        ArgumentNullException.ThrowIfNull(qualifiedDigests);
        ArgumentNullException.ThrowIfNull(docker);
        _project = composeProject;
        _service = composeService;
        // Deployment composition supplies the published qualification inventory, not request input.
        _qualifiedDigests = qualifiedDigests.ToHashSet(StringComparer.Ordinal);
        _docker = docker;
    }

    public async Task<CdcTransportResult<CdcWorkerInspection>> InspectAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timing.CallTimeout);
        try
        {
            Require(_qualifiedDigests.Contains(request.WorkerPolicy.QualifiedImageDigest));
            string id = await FindWorkerAsync(timeout.Token);
            await VerifyWorkerInventoryAsync(request, id, timeout.Token);
            string before = await _docker.RunAsync(["inspect", id], timeout.Token);
            using var document = JsonDocument.Parse(before);
            JsonElement container = document.RootElement.EnumerateArray().Single();
            Require(container.GetProperty("Id").GetString() == id);
            JsonElement state = container.GetProperty("State");
            Require(
                state.GetProperty("Running").GetBoolean()
                    && !state.GetProperty("Restarting").GetBoolean()
                    && !state.GetProperty("Paused").GetBoolean()
            );
            Require(state.GetProperty("Pid").GetInt64() > 0);
            string started = state.GetProperty("StartedAt").GetString()!;
            Require(
                DateTimeOffset.TryParse(
                    started,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var startedAt
                )
                    && startedAt > DateTimeOffset.UnixEpoch
            );
            JsonElement config = container.GetProperty("Config");
            string image = config.GetProperty("Image").GetString()!;
            Require(
                image.EndsWith("@" + request.WorkerPolicy.QualifiedImageDigest, StringComparison.Ordinal)
            );
            using var imageDocument = JsonDocument.Parse(
                await _docker.RunAsync(["image", "inspect", image], timeout.Token)
            );
            JsonElement imageInfo = imageDocument.RootElement.EnumerateArray().Single();
            Require(imageInfo.GetProperty("Id").GetString() == container.GetProperty("Image").GetString());
            Require(imageInfo.GetProperty("RepoDigests").EnumerateArray().Any(x => x.GetString() == image));
            Require(
                config.GetProperty("Labels").GetProperty("com.docker.compose.project").GetString() == _project
            );
            Require(
                config.GetProperty("Labels").GetProperty("com.docker.compose.service").GetString() == _service
            );
            Require(
                config.GetProperty("Labels").GetProperty("org.edfi.cdc.worker").GetString()
                    == request.WorkerPolicy.WorkerKey.Value
            );
            Require(
                container.GetProperty("HostConfig").GetProperty("NetworkMode").GetString()
                    is not ("host" or "none")
            );
            VerifyPort(container, request.ConnectEndpoint, "8083/tcp", "/");
            VerifyPort(container, request.WorkerMetricsEndpoint, "9404/tcp", "/metrics");

            // Read the process and the generated properties, never substitute desired env values.
            string runtime = await _docker.RunAsync(
                ["exec", id, "sh", "-ec", RuntimeInspectionScript],
                timeout.Token
            );
            string[] sections = runtime.Split('\n', 3);
            Require(sections.Length == 3 && long.TryParse(sections[0], out long ticks) && ticks > 0);
            Require(long.TryParse(sections[1], out long heap) && heap > 0);
            var properties = ParseProperties(sections[2]);
            Require(properties["offset.storage.topic"] == request.WorkerPolicy.OffsetStorageTopic.Value);
            Require(
                properties["connector.client.config.override.policy"] == "All"
                    && request.WorkerPolicy.ClientConfigurationOverridePolicy == "All"
            );
            Require(
                properties["group.id"] == request.WorkerPolicy.WorkerKey.Value
                    && heap == request.WorkerPolicy.HeapBytes
            );
            string advertisedHost = properties["rest.advertised.host.name"];
            Require(properties["rest.advertised.port"] == "8083");
            Require(
                advertisedHost == container.GetProperty("Name").GetString()!.TrimStart('/')
                    || advertisedHost == config.GetProperty("Hostname").GetString()
                    || container
                        .GetProperty("NetworkSettings")
                        .GetProperty("Networks")
                        .EnumerateObject()
                        .Any(network =>
                            (
                                network.Value.TryGetProperty("IPAddress", out var address)
                                && address.GetString() == advertisedHost
                            )
                            || network.Value.TryGetProperty("Aliases", out var aliases)
                                && aliases.ValueKind == JsonValueKind.Array
                                && aliases.EnumerateArray().Any(alias => alias.GetString() == advertisedHost)
                        )
            );
            string after = await _docker.RunAsync(["inspect", id], timeout.Token);
            using var afterDocument = JsonDocument.Parse(after);
            JsonElement final = afterDocument.RootElement.EnumerateArray().Single();
            Require(final.GetProperty("State").GetRawText() == state.GetRawText());
            Require(
                final.GetProperty("RestartCount").GetInt32()
                    == container.GetProperty("RestartCount").GetInt32()
            );
            Require(await FindWorkerAsync(timeout.Token) == id);
            await VerifyWorkerInventoryAsync(request, id, timeout.Token);
            string finalRuntime = await _docker.RunAsync(
                ["exec", id, "sh", "-ec", RuntimeInspectionScript],
                timeout.Token
            );
            Require(finalRuntime == runtime);
            string identity =
                "sha256:"
                + Convert.ToHexStringLower(
                    SHA256.HashData(Encoding.UTF8.GetBytes($"{id}\n{started}\n{sections[0]}"))
                );
            return new CdcTransportResult<CdcWorkerInspection>.Observed(
                new(
                    identity,
                    request.WorkerMetricsEndpoint,
                    properties,
                    request.WorkerPolicy.QualifiedImageDigest,
                    heap,
                    advertisedHost + ":8083"
                )
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unknown(CdcDeploymentFailure.Timeout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return Unknown(CdcDeploymentFailure.ValidationFailed);
        }
        catch (Exception)
        {
            return Unknown(CdcDeploymentFailure.Unavailable);
        }
    }

    private async Task VerifyWorkerInventoryAsync(
        CdcDeploymentRequest request,
        string id,
        CancellationToken token
    )
    {
        string output = await _docker.RunAsync(
            [
                "ps",
                "-a",
                "--no-trunc",
                "--filter",
                $"label=org.edfi.cdc.worker={request.WorkerPolicy.WorkerKey.Value}",
                "--format",
                "{{.ID}}",
            ],
            token
        );
        Require(output.Trim() == id);
    }

    private async Task<string> FindWorkerAsync(CancellationToken token)
    {
        string output = await _docker.RunAsync(
            [
                "ps",
                "-a",
                "--no-trunc",
                "--filter",
                $"label=com.docker.compose.project={_project}",
                "--filter",
                $"label=com.docker.compose.service={_service}",
                "--format",
                "{{.ID}}",
            ],
            token
        );
        string[] ids = output.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        Require(ids.Length == 1 && ids[0].Length == 64 && ids[0].All(char.IsAsciiHexDigit));
        return ids[0];
    }

    private static void VerifyPort(JsonElement container, Uri endpoint, string port, string path)
    {
        Require(
            endpoint.Scheme == "http"
                && endpoint.Host == "127.0.0.1"
                && endpoint.AbsolutePath == path
                && endpoint.Query.Length == 0
        );
        JsonElement binding = container
            .GetProperty("NetworkSettings")
            .GetProperty("Ports")
            .GetProperty(port)
            .EnumerateArray()
            .Single();
        Require(
            binding.GetProperty("HostIp").GetString() == "127.0.0.1"
                && binding.GetProperty("HostPort").GetString()
                    == endpoint.Port.ToString(CultureInfo.InvariantCulture)
        );
    }

    internal static IReadOnlyDictionary<string, string> ParseProperties(string text)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=');
            Require(separator > 0 && !line.Contains('\\') && !line.Contains('\r'));
            string key = line[..separator];
            Require(
                key
                    is "offset.storage.topic"
                        or "connector.client.config.override.policy"
                        or "group.id"
                        or "config.storage.topic"
                        or "status.storage.topic"
                        or "rest.advertised.host.name"
                        or "rest.advertised.port"
                        or "bootstrap.servers"
            );
            Require(result.TryAdd(key, line[(separator + 1)..]) && result[key].Length > 0);
        }
        Require(result.Count == 8);
        return result;
    }

    private static void Require(bool value)
    {
        if (!value)
        {
            throw new InvalidDataException("Worker deployment evidence is unsupported or contradictory.");
        }
    }

    private static CdcTransportResult<CdcWorkerInspection> Unknown(CdcDeploymentFailure failure) =>
        new CdcTransportResult<CdcWorkerInspection>.Unavailable(new(CdcDeploymentComponent.Worker, failure));

    // Fixed script and fixed paths owned by the qualified image. No request text enters shell code.
    // A single JVM at PID 1 must load the qualified agent and generated worker properties.
    // Reject alternate properties syntax rather than guessing Java escaping/continuation semantics.
    internal const string RuntimeInspectionScript = """
        test "$(find /proc/[0-9]*/exe -lname '*/java' 2>/dev/null | wc -l)" -eq 1
        tr '\000' '\n' < /proc/1/cmdline | grep -Fx -- 'org.apache.kafka.connect.cli.ConnectDistributed' >/dev/null
        tr '\000' '\n' < /proc/1/cmdline | grep -Fx -- '/kafka/config/connect-distributed.properties' >/dev/null
        test "$(tr '\000' '\n' < /proc/1/cmdline | grep -c -- '^-javaagent:')" -eq 1
        tr '\000' '\n' < /proc/1/cmdline | grep -Fx -- '-javaagent:/opt/edfi-cdc/jmx_prometheus_javaagent-1.5.0.jar=9404:/opt/edfi-cdc/cdc.yaml' >/dev/null
        awk '{print $22}' /proc/1/stat
        jcmd 1 VM.flags | tr ' ' '\n' | sed -n 's/^-XX:MaxHeapSize=//p'
        awk '/^[[:space:]]*(offset.storage.topic|connector.client.config.override.policy|group.id|config.storage.topic|status.storage.topic|bootstrap.servers|rest.advertised.host.name|rest.advertised.port)([[:space:]]|=|:)/ {print}' /kafka/config/connect-distributed.properties
        """;
}

internal interface ICdcWorkerDockerCommand
{
    Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken token);
}

internal sealed class CdcWorkerDockerCommand : ICdcWorkerDockerCommand
{
    public async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.Start();
        using var registration = token.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            { /* Process exited before cancellation. */
            }
        });
        Task<string> output = ReadBoundedAsync(process.StandardOutput, token);
        Task<string> error = ReadBoundedAsync(process.StandardError, token);
        try
        {
            await Task.WhenAll(output, error, process.WaitForExitAsync(token));
            if (process.ExitCode != 0)
            {
                throw new IOException("Docker worker inspection failed.");
            }
            return await output;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        StringBuilder text = new();
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (text.Length + count > 262_144)
            {
                throw new IOException("Docker inspection output exceeded its bound.");
            }
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
