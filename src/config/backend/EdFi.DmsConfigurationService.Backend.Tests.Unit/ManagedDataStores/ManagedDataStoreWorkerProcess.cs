// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.ManagedDataStores;

/// <summary>
/// Runs only the explicitly selected integration-test worker in a new testhost. Secrets travel over
/// a current-user-only pipe, never command arguments, test output or a persisted request file.
/// Cleanup can terminate only the Process instance this helper created, never the IDE/test controller.
/// </summary>
public sealed class ManagedDataStoreWorkerProcess : IAsyncDisposable
{
    public const string PipeVariable = "DMS1439_MANAGED_WORKER_PIPE";
    public const string WorkerMethod = "It_runs_the_isolated_managed_process_worker";
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(6);
    private readonly NamedPipeServerStream _pipe;
    private readonly Process _runner;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly CancellationTokenSource _deadline = new(Deadline);

    // Deliberately not a record: its default ToString must not expose credentials.
    public sealed class Settings
    {
        public required string CatalogConnection { get; init; }
        public required string RuntimeConnection { get; init; }
        public required DmsDataStoreProvisioningOptions Provisioning { get; init; }
        public required int ManageId { get; init; }
        public required string JobId { get; init; }
        public required bool Delete { get; init; }
        public required int MaxAttempts { get; init; }
        public required StopPoint StopAt { get; init; }
        public bool ContinueAfterRetry { get; init; }

        /// <summary>Starts the child with a failing startup probe, so the real readiness verdict is NotReady.</summary>
        public bool ClosedReadiness { get; init; }
    }

    public enum StopPoint
    {
        None,
        BeforeCommit,
        AfterCommit,
    }

    public sealed record Signal(string Kind, int ProcessId, int Attempt = 0, long Fence = 0, int Claims = 0);

    private ManagedDataStoreWorkerProcess(NamedPipeServerStream pipe, Process runner)
    {
        _pipe = pipe;
        _runner = runner;
        _reader = new(pipe, Encoding.UTF8, leaveOpen: true);
        _writer = new(pipe, new UTF8Encoding(false), leaveOpen: true);
    }

    public static async Task<ManagedDataStoreWorkerProcess> StartAsync(string assemblyPath, Settings settings)
    {
        string pipeName = "dms1439-worker-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
        );
        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("test");
        start.ArgumentList.Add(assemblyPath);
        start.ArgumentList.Add("--filter");
        start.ArgumentList.Add("FullyQualifiedName~" + WorkerMethod);
        start.ArgumentList.Add("--logger");
        start.ArgumentList.Add("console;verbosity=quiet");
        start.Environment[PipeVariable] = pipeName;
        Process runner;
        try
        {
            runner =
                Process.Start(start) ?? throw new InvalidOperationException("Worker runner did not start.");
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
        // Drain both pipes without forwarding the child's potentially secret-bearing diagnostics.
        runner.OutputDataReceived += (_, _) => { };
        runner.ErrorDataReceived += (_, _) => { };
        runner.BeginOutputReadLine();
        runner.BeginErrorReadLine();
        var worker = new ManagedDataStoreWorkerProcess(pipe, runner);
        try
        {
            var connected = pipe.WaitForConnectionAsync(worker._deadline.Token);
            await Task.WhenAny(connected, runner.WaitForExitAsync(worker._deadline.Token));
            if (!connected.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException("Worker runner exited before connecting.");
            }
            await connected;
            worker._writer.AutoFlush = true;
            await worker._writer.WriteLineAsync(
                JsonSerializer.Serialize(settings).AsMemory(),
                worker._deadline.Token
            );
            return worker;
        }
        catch
        {
            await worker.DisposeAsync();
            throw;
        }
    }

    public async Task<Signal> ReadAsync()
    {
        string? line = await _reader.ReadLineAsync(_deadline.Token);
        var signal = line is null ? null : JsonSerializer.Deserialize<Signal>(line);
        if (signal is null || signal.ProcessId == Environment.ProcessId)
        {
            throw new InvalidOperationException("No independent worker signal was received.");
        }
        return signal;
    }

    public async Task TerminateAtCheckpointAsync()
    {
        await _writer.WriteLineAsync("terminate".AsMemory(), _deadline.Token);
        await _runner.WaitForExitAsync(_deadline.Token);
        if (_runner.ExitCode == 0)
        {
            throw new InvalidOperationException("The interrupted worker unexpectedly exited successfully.");
        }
    }

    public async Task RequireSuccessfulExitAsync()
    {
        await _runner.WaitForExitAsync(_deadline.Token);
        if (_runner.ExitCode != 0)
        {
            throw new InvalidOperationException($"Worker runner exited with code {_runner.ExitCode}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_runner.HasExited)
            {
                // Only this newly launched runner and its children. Never find/kill processes by name.
                _runner.Kill(entireProcessTree: true);
                await _runner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
        finally
        {
            _reader.Dispose();
            await _writer.DisposeAsync();
            await _pipe.DisposeAsync();
            _runner.Dispose();
            _deadline.Dispose();
        }
    }

    /// <summary>The child endpoint. It cannot be activated without the parent-specific pipe.</summary>
    public sealed class Channel : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly CancellationTokenSource _deadline = new(Deadline);
        public Settings Configuration { get; private set; } = null!;

        private Channel(string name)
        {
            _pipe = new(
                ".",
                name,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
            );
            _reader = new(_pipe, Encoding.UTF8, leaveOpen: true);
            _writer = new(_pipe, new UTF8Encoding(false), leaveOpen: true);
        }

        public static async Task<Channel> ConnectAsync()
        {
            string? name = Environment.GetEnvironmentVariable(PipeVariable);
            if (
                string.IsNullOrWhiteSpace(name)
                || !name.StartsWith("dms1439-worker-", StringComparison.Ordinal)
            )
            {
                throw new InvalidOperationException("This worker must be launched by its process test.");
            }
            var channel = new Channel(name);
            try
            {
                await channel._pipe.ConnectAsync(channel._deadline.Token);
                channel._writer.AutoFlush = true;
                string? request = await channel._reader.ReadLineAsync(channel._deadline.Token);
                channel.Configuration =
                    JsonSerializer.Deserialize<Settings>(request ?? "")
                    ?? throw new InvalidOperationException("Worker configuration was not received.");
                return channel;
            }
            catch
            {
                await channel.DisposeAsync();
                throw;
            }
        }

        public Task SendAsync(Signal signal) =>
            _writer.WriteLineAsync(JsonSerializer.Serialize(signal).AsMemory(), _deadline.Token);

        public async Task TerminateHereAsync(ClaimedJob job)
        {
            await SendAsync(new("Checkpoint", Environment.ProcessId, job.AttemptCount, job.FencingToken));
            if (await _reader.ReadLineAsync(_deadline.Token) != "terminate")
            {
                throw new InvalidOperationException("Worker termination command was not received.");
            }
            // Abruptly terminate this explicitly launched testhost: no host stop, finally or lease release.
            // The supervising test and Rider run in different processes and remain alive.
            Environment.Exit(83);
        }

        public async ValueTask DisposeAsync()
        {
            _reader.Dispose();
            await _writer.DisposeAsync();
            await _pipe.DisposeAsync();
            _deadline.Dispose();
        }
    }

    /// <summary>Observes the production worker's completed sweep/executor writes without polling or replacing them.</summary>
    public sealed class SettlementObserver(string jobId, bool continueAfterRetry = false)
        : ILogger<JobExecutor>,
            ILogger<JobWorkerService>
    {
        private readonly TaskCompletionSource<Signal> _retried = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource<Signal> _settled = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _claims;
        public Task<Signal> Settled => _settled.Task;
        public Task<Signal> Retried => _retried.Task;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (eventId.Name is "JobsExhausted" or "ManagedJobsSettled")
            {
                var exhausted = new Signal(eventId.Name, Environment.ProcessId, Claims: _claims);
                _retried.TrySetResult(exhausted);
                _settled.TrySetResult(exhausted);
                return;
            }
            if (state is not IEnumerable<KeyValuePair<string, object?>> fields)
            {
                return;
            }
            var values = fields.ToDictionary(pair => pair.Key, pair => pair.Value);
            if (values.GetValueOrDefault("JobId") as string != jobId)
            {
                return;
            }
            if (eventId.Name is "JobClaimed" or "JobReclaimed")
            {
                Interlocked.Increment(ref _claims);
            }
            else if (
                eventId.Name
                is "JobCompleted"
                    or "JobFailed"
                    or "JobRetryScheduled"
                    or "OwnershipUncertainExit"
            )
            {
                var signal = new Signal(
                    eventId.Name,
                    Environment.ProcessId,
                    Convert.ToInt32(values.GetValueOrDefault("Attempt")),
                    Claims: _claims
                );
                if (
                    continueAfterRetry
                    && eventId.Name == "JobRetryScheduled"
                    && _retried.TrySetResult(signal)
                )
                {
                    return;
                }
                // Unexpected completion/failure before a retry must also wake the controller.
                _retried.TrySetResult(signal);
                _settled.TrySetResult(signal);
            }
        }
    }
}
