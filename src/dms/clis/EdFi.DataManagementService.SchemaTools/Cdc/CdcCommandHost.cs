// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Backend.Cdc;

namespace EdFi.DataManagementService.SchemaTools.Cdc;

public enum CdcCommandOperation
{
    Enable,
    Validate,
    Status,
    Watch,
    Start,
    Restart,
    Resume,
    Stop,
    IncreaseRecordSize,
    Retire,
}

public sealed record CdcCommandInvocation(
    CdcCommandOperation Operation,
    string SettingsPath,
    string StatePath,
    int MaximumPasses,
    long Generation,
    bool DestructiveCleanup,
    string AcknowledgementPath,
    bool ConfirmConsumerCapacity
);

/// <summary>Only controller-approved, sanitized results may be supplied as Data.</summary>
public sealed record CdcCommandResult(
    string Operation,
    bool Succeeded,
    int ExitCode,
    IReadOnlyList<CdcDeploymentDiagnostic> Diagnostics,
    object Data = null!,
    EdFi.DataManagementService.Core.DocumentCache.Cdc.CdcBinding Binding = null!,
    CdcCommandDeploymentProfile DeploymentProfile = null!
);

public sealed record CdcCommandDeploymentProfile(
    CdcKafkaAuthorizationProfile AuthorizationProfile,
    bool AclIsolationProven
);

public interface ICdcCommandRunner
{
    Task<CdcCommandResult> RunAsync(
        CdcCommandInvocation invocation,
        TextWriter progress,
        CancellationToken token
    );
}

/// <summary>Owns parsing and output, including parse failures before System.CommandLine dispatch.</summary>
public static class CdcCommandHost
{
    public static JsonSerializerOptions JsonOptions { get; } =
        new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

    public static Command Create(ICdcCommandRunner runner, TextWriter output, TextWriter error)
    {
        var group = new Command(
            "cdc",
            "Deployment-owned CDC. Requires intact managed state. Native recovery can consume before revalidation; eventual readiness cannot certify unsampled continuity. Adoption and physical-source replacement are deferred. Independent provisioning and retirement do not certify migration. See README CDC examples and design-docs/cdc/cdc-streaming.md."
        );
        foreach (var operation in Enum.GetValues<CdcCommandOperation>())
        {
            string name = Name(operation);
            var command = new Command(
                name,
                operation switch
                {
                    CdcCommandOperation.Enable =>
                        "Enable within the owned offline initial workflow; dispose projection before returning writer permission.",
                    CdcCommandOperation.Validate =>
                        "Inspect established continuity and running readiness without repair.",
                    CdcCommandOperation.Status or CdcCommandOperation.Watch =>
                        "Observe current state; latch terminal incidents and contain connectors. Watch writes passes to stderr and one final result to stdout.",
                    CdcCommandOperation.Stop =>
                        "Verify connector shutdown; retain offsets, artifacts, binding and source history.",
                    CdcCommandOperation.Retire =>
                        "Destructively retire this explicit generation while infrastructure remains reachable; preserve source history and shared worker state.",
                    CdcCommandOperation.IncreaseRecordSize =>
                        "Explicit increase with the retained previous policy and operation ID. The operator attests complete consumer inventory and owner capacity evidence, or explicitly no consumers. Renew --confirm-consumer-capacity on every invocation.",
                    _ =>
                        "Guarded lifecycle requires fresh intact provenance. Start additionally requires a verified managed stop; no initial baseline is recertified.",
                }
            );
            var settings = new Option<string>("--settings")
            {
                Required = true,
                Description =
                    "DMS JSON settings plus Cdc deployment configuration; DMS_CDC__ prefixed environment overrides. Secrets belong in environment/config providers.",
            };
            var state = new Option<string>("--state-path")
            {
                Required = true,
                Description =
                    "Original managed provisioning/controller state root; never an empty replacement directory.",
            };
            var json = new Option<bool>("--json")
            {
                Description = "One structured stdout result. Diagnostics and watch progress use stderr.",
            };
            command.Options.Add(settings);
            command.Options.Add(state);
            command.Options.Add(json);
            var passes = new Option<int>("--maximum-passes") { DefaultValueFactory = _ => 10 };
            var generation = new Option<long>("--generation") { Required = true };
            var destructive = new Option<bool>("--destructive-cleanup");
            var acknowledgement = new Option<string>("--acknowledgement")
            {
                Required = true,
                Description =
                    "Structured operation scope and consumer evidence JSON; contains no reusable invocation confirmation.",
            };
            var confirm = new Option<bool>("--confirm-consumer-capacity");
            if (operation == CdcCommandOperation.Watch)
            {
                command.Options.Add(passes);
            }
            if (operation == CdcCommandOperation.Retire)
            {
                command.Options.Add(generation);
                command.Options.Add(destructive);
            }
            if (operation == CdcCommandOperation.IncreaseRecordSize)
            {
                command.Options.Add(acknowledgement);
                command.Options.Add(confirm);
            }
            command.SetAction(
                async (parse, token) =>
                {
                    CdcCommandResult result;
                    try
                    {
                        int count = operation == CdcCommandOperation.Watch ? parse.GetValue(passes) : 1;
                        long selectedGeneration =
                            operation == CdcCommandOperation.Retire ? parse.GetValue(generation) : 0;
                        bool cleanup = operation == CdcCommandOperation.Retire && parse.GetValue(destructive);
                        bool capacity =
                            operation == CdcCommandOperation.IncreaseRecordSize && parse.GetValue(confirm);
                        if (
                            count is < 1 or > 10000
                            || operation == CdcCommandOperation.Retire
                                && (selectedGeneration <= 0 || !cleanup)
                            || operation == CdcCommandOperation.IncreaseRecordSize && !capacity
                        )
                        {
                            throw new ArgumentException("CDC command input is invalid.");
                        }
                        token.ThrowIfCancellationRequested();
                        result = await runner.RunAsync(
                            new(
                                operation,
                                parse.GetValue(settings)!,
                                parse.GetValue(state)!,
                                count,
                                selectedGeneration,
                                cleanup,
                                operation == CdcCommandOperation.IncreaseRecordSize
                                    ? parse.GetValue(acknowledgement)!
                                    : "",
                                capacity
                            ),
                            error,
                            token
                        );
                    }
                    catch (OperationCanceledException)
                    {
                        result = Failure(
                            name,
                            130,
                            CdcDeploymentComponent.Request,
                            CdcDeploymentFailure.Unavailable
                        );
                    }
                    catch (Exception exception)
                    {
                        result = Failure(
                            name,
                            exception is ArgumentException or JsonException ? 2 : 1,
                            CdcDeploymentComponent.Request,
                            exception is ArgumentException or JsonException
                                ? CdcDeploymentFailure.InvalidInput
                                : CdcDeploymentFailure.Unavailable
                        );
                    }
                    await WriteAsync(result, output, error);
                    return result.ExitCode;
                }
            );
            group.Subcommands.Add(command);
        }
        return group;
    }

    public static async Task<int> InvokeAsync(
        string[] args,
        ICdcCommandRunner runner,
        TextWriter output,
        TextWriter error,
        CancellationToken token = default
    )
    {
        var root = new RootCommand();
        root.Options.Add(new Option<bool>("--verbose", "-v") { Recursive = true });
        root.Subcommands.Add(Create(runner, output, error));
        var parse = root.Parse(args);
        if (parse.Errors.Count > 0 || args.Length < 2)
        {
            // Parser diagnostics quote unknown arguments and values. Never print them on this surface.
            var failure = Failure(
                "cdc",
                2,
                CdcDeploymentComponent.Request,
                CdcDeploymentFailure.InvalidInput
            );
            await WriteAsync(failure, output, error);
            return failure.ExitCode;
        }
        return await parse.InvokeAsync(
            new InvocationConfiguration
            {
                Output = output,
                Error = error,
                EnableDefaultExceptionHandler = false,
                ProcessTerminationTimeout = TimeSpan.FromMinutes(5),
            },
            token
        );
    }

    public static string Name(CdcCommandOperation operation) =>
        operation == CdcCommandOperation.IncreaseRecordSize
            ? "increase-record-size"
            : operation.ToString().ToLowerInvariant();

    public static CdcCommandResult Failure(
        string operation,
        int exitCode,
        CdcDeploymentComponent component,
        CdcDeploymentFailure failure
    ) => new(operation, false, exitCode, [new(component, failure)]);

    private static async Task WriteAsync(CdcCommandResult result, TextWriter output, TextWriter error)
    {
        // Serialize before writing so serialization failure cannot leave partial JSON on stdout.
        string json = JsonSerializer.Serialize(result, JsonOptions);
        foreach (var diagnostic in result.Diagnostics)
        {
            await error.WriteLineAsync($"{diagnostic.Component}: {diagnostic.Message}");
        }
        if (result.ExitCode == 130)
        {
            await error.WriteLineAsync("CDC command cancelled; reconcile retained state before retrying.");
        }
        await output.WriteLineAsync(json);
    }
}
