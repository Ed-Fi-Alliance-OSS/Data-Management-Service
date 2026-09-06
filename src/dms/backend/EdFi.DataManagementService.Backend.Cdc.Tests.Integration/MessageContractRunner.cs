// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed record MessageContractRunnerScenario(
    string ScenarioId,
    JsonElement SourceRecord,
    IReadOnlyDictionary<string, string> TransformConfig,
    int PartitionCount,
    bool MeasureProducerSize = false
)
{
    public override string ToString() => ScenarioId;
}

// Do not let test framework formatting print record bodies on failure.
internal sealed record MessageContractRunnerObservation(string ScenarioId, JsonElement Data)
{
    public string Status => Data.GetProperty("status").GetString()!;

    public override string ToString() =>
        Status == "failed"
            ? $"{ScenarioId}: failed ({Data.GetProperty("failure").GetProperty("stage").GetString()}/{Data.GetProperty("failure").GetProperty("reason").GetString()})"
            : $"{ScenarioId}: {Status}";
}

internal sealed record MessageContractRunnerResult(
    string ConnectImage,
    IReadOnlyList<MessageContractRunnerObservation> Observations
)
{
    public override string ToString() => $"Message contract runner: {Observations.Count} observations";
}

public sealed class MessageContractRunnerPrerequisiteException(string reason)
    : Exception($"CDC message contract prerequisite: {reason}. Details redacted.")
{
    public string Reason { get; } = reason;
}

/// <summary>Image-only execution using the same Docker adapter and Java probe as template qualification.</summary>
internal sealed class MessageContractRunner(IDockerCli docker, string image)
{
    internal const string ImageVariable = "CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE";
    internal static readonly string[] RequiredClasses =
    [
        "org.edfi.kafka.connect.transforms.DocumentState",
        "org.apache.kafka.connect.storage.StringConverter",
        "org.edfi.kafka.connect.converters.DocumentStateJsonConverter",
        "org.edfi.kafka.connect.partitioner.KafkaMurmur2V1Partitioner",
    ];

    internal TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public static MessageContractRunner FromEnvironment() =>
        new(new DockerCli(), Environment.GetEnvironmentVariable(ImageVariable) ?? string.Empty);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Major Code Smell",
        "S1854",
        Justification = "Stage assignments are consumed by the exception boundary when an awaited operation fails."
    )]
    public async Task<MessageContractRunnerResult> RunAsync(
        IReadOnlyList<MessageContractRunnerScenario> scenarios,
        CancellationToken cancellationToken,
        IReadOnlyList<string> probeClasses
    )
    {
        ValidateImage(image);
        if (probeClasses.Any(name => !Regex.IsMatch(name, @"\A[A-Za-z_$][A-Za-z0-9_.$]*\z")))
        {
            throw new ArgumentException("Invalid Java class name.", nameof(probeClasses));
        }
        if (
            scenarios.Any(s => !Regex.IsMatch(s.ScenarioId, @"\AMC-[A-Z0-9-]{1,156}\z"))
            || scenarios.Select(s => s.ScenarioId).Distinct(StringComparer.Ordinal).Count() != scenarios.Count
        )
        {
            throw new ArgumentException("Invalid or duplicate scenario identifiers.", nameof(scenarios));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ExecutionTimeout);
        CancellationToken token = timeout.Token;
        string container = $"cdc-message-contract-{Guid.NewGuid():N}";
        string directory = Path.Combine(Path.GetTempPath(), container);
        string stage = "docker-unavailable";
        bool containerAttempted = false;
        try
        {
            await docker.RequireDockerAsync(token);
            stage = "connect-image-unavailable";
            await docker.RunAsync(["image", "inspect", image], token);
            stage = "runner-resource-missing";
            Directory.CreateDirectory(directory);
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "MessageContractRunner", "MessageContractRunner.java"),
                Path.Combine(directory, "MessageContractRunner.java")
            );
            await File.WriteAllTextAsync(
                Path.Combine(directory, "CdcTemplateClassProbe.java"),
                CdcPinnedImageJavaRuntime.ClassProbeSource,
                token
            );
            await File.WriteAllTextAsync(
                Path.Combine(directory, "input.json"),
                JsonSerializer.Serialize(
                    new { scenarios },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                ),
                token
            );
            string script = $$"""
                set -eu
                {{CdcPinnedImageJavaRuntime.ClassPathScript}}
                java -cp "${class_path}" /tmp/contract/CdcTemplateClassProbe.java {{string.Join(
                    " ",
                    probeClasses
                )}} >/dev/null 2>&1
                exec java -cp "${class_path}" /tmp/contract/MessageContractRunner.java /tmp/contract/input.json /tmp/contract-output.json >/dev/null 2>&1
                """;
            stage = "runner-container-startup";
            containerAttempted = true;
            await docker.RunAsync(
                [
                    "create",
                    "--name",
                    container,
                    "--network",
                    "none",
                    "--hostname",
                    "localhost",
                    "--entrypoint",
                    "sh",
                    image,
                    "-lc",
                    script,
                ],
                token
            );
            await docker.RunAsync(["cp", directory, $"{container}:/tmp/contract"], token);
            stage = "runner-classpath-incompatible";
            DockerCommandResult execution = await docker.RunAllowingFailureAsync(
                ["start", "-a", container],
                token
            );
            if (execution.ExitCode != 0)
            {
                throw new MessageContractRunnerPrerequisiteException(
                    execution.ExitCode == 41 ? "required-class-missing" : "runner-classpath-incompatible"
                );
            }
            stage = "runner-output-invalid";
            string outputPath = Path.Combine(directory, "output.json");
            await docker.RunAsync(["cp", $"{container}:/tmp/contract-output.json", outputPath], token);
            using JsonDocument output = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath, token));
            if (output.RootElement.GetProperty("formatVersion").GetInt32() != 1)
            {
                throw new MessageContractRunnerPrerequisiteException(stage);
            }
            MessageContractRunnerObservation[] observations = output
                .RootElement.GetProperty("observations")
                .EnumerateArray()
                .Select(item => new MessageContractRunnerObservation(
                    item.GetProperty("scenarioId").GetString()!,
                    item.Clone()
                ))
                .ToArray();
            if (
                !observations.Select(o => o.ScenarioId).SequenceEqual(scenarios.Select(s => s.ScenarioId))
                || Array.Exists(observations, o => o.Status is not ("retained" or "dropped" or "failed"))
            )
            {
                throw new MessageContractRunnerPrerequisiteException(stage);
            }
            return new(image, observations);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MessageContractRunnerPrerequisiteException("runner-timeout");
        }
        catch (Exception failure)
            when (failure is not MessageContractRunnerPrerequisiteException and not OperationCanceledException
            )
        {
            // Deliberately discard Docker output, file content, paths, and inner exception prose.
            throw new MessageContractRunnerPrerequisiteException(stage);
        }
        finally
        {
            try
            {
                if (containerAttempted)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    DockerCommandResult removed = await docker.RunAllowingFailureAsync(
                        ["rm", "-f", container],
                        cleanup.Token
                    );
                    if (removed.ExitCode != 0)
                    {
                        throw new MessageContractRunnerPrerequisiteException("runner-cleanup-failed");
                    }
                }
            }
            catch (Exception failure) when (failure is not MessageContractRunnerPrerequisiteException)
            {
                throw new MessageContractRunnerPrerequisiteException("runner-cleanup-failed");
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
    }

    public Task<MessageContractRunnerResult> RunAsync(
        IReadOnlyList<MessageContractRunnerScenario> scenarios,
        CancellationToken cancellationToken = default
    ) => RunAsync(scenarios, cancellationToken, RequiredClasses);

    internal static void ValidateImage(string image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            throw new MessageContractRunnerPrerequisiteException($"{ImageVariable}-missing");
        }
        if (!Regex.IsMatch(image, @"\A[a-zA-Z0-9][a-zA-Z0-9._/:-]*@sha256:[0-9a-f]{64}\z"))
        {
            throw new MessageContractRunnerPrerequisiteException(
                $"{ImageVariable}-immutable-digest-required"
            );
        }
    }
}
