// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal static class CdcControllerCategories
{
    public const string Admission = "CdcControllerAdmission";
    public const string ManagedLifecycle = "CdcControllerManagedLifecycle";
    public const string NativeRecovery = "CdcControllerNativeRecovery";
    public const string KafkaPolicy = "CdcControllerKafkaPolicy";
    public const string RecordSize = "CdcControllerRecordSize";
}

public enum CdcControllerBoundary
{
    CreationReceipt,
    Binding,
    Activation,
    ProviderProof,
    Registration,
    Barrier,
    WriterHandoff,
    Rollout,
    Cleanup,
    Observation,
    WorkerRecovery,
    JournalWrite,
}

public enum CdcControllerEdge
{
    Before,
    After,
    Failed,
    BeforeTemporaryWrite,
    AfterTemporaryFlush,
    AfterAtomicReplacement,
}

// No method arguments, return values, exception messages, source names or payloads enter the trace.
internal sealed record CdcControllerFixtureEvent(
    long Sequence,
    DateTimeOffset At,
    CdcControllerBoundary Boundary,
    CdcControllerEdge Edge,
    int Occurrence
);

/// <summary>
/// Decorates real asynchronous interfaces without replacing their results. Before/after callbacks can
/// interrupt an invocation, lose a successful reply, or rendezvous with an external process killer.
/// A thrown exception models an interrupted boundary, not proof of an actual process crash.
/// </summary>
internal sealed class CdcControllerFixtureHooks
{
    private readonly ConcurrentQueue<CdcControllerFixtureEvent> _trace = new();
    private readonly ConcurrentQueue<object> _connectObservations = new();
    private readonly ConcurrentDictionary<(CdcControllerBoundary, CdcControllerEdge), int> _counts = new();
    private long _sequence;
    public Action<CdcControllerFixtureEvent> OnBoundary { get; set; } = _ => { };
    public IReadOnlyList<CdcControllerFixtureEvent> Trace => _trace.OrderBy(e => e.Sequence).ToArray();
    public IReadOnlyList<object> ConnectObservations => _connectObservations.ToArray();

    public void Hit(CdcControllerBoundary boundary, CdcControllerEdge edge)
    {
        var item = new CdcControllerFixtureEvent(
            Interlocked.Increment(ref _sequence),
            DateTimeOffset.UtcNow,
            boundary,
            edge,
            _counts.AddOrUpdate((boundary, edge), 1, (_, count) => count + 1)
        );
        _trace.Enqueue(item);
        OnBoundary(item);
    }

    public async Task<T> InvokeAsync<T>(
        CdcControllerBoundary boundary,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken token
    )
    {
        token.ThrowIfCancellationRequested();
        Hit(boundary, CdcControllerEdge.Before);
        T result;
        try
        {
            result = await operation(token);
            if (result is CdcTransportResult<CdcConnectStatus>.Observed status)
            {
                _connectObservations.Enqueue(
                    new
                    {
                        At = DateTimeOffset.UtcNow,
                        status.Value.IsStopped,
                        status.Value.Runtime.ObservedAt,
                        status.Value.Runtime.ConnectorState,
                        status.Value.Runtime.TaskCount,
                        Tasks = status.Value.Tasks.Select(task => new { task.Id, task.State }).ToArray(),
                        WorkerIdSha256 = Convert.ToHexStringLower(
                            SHA256.HashData(Encoding.UTF8.GetBytes(status.Value.WorkerId))
                        ),
                    }
                );
                while (_connectObservations.Count > 128)
                {
                    _connectObservations.TryDequeue(out _);
                }
            }
        }
        catch
        {
            // Never let a diagnostic callback replace the original operation's exception.
            try
            {
                Hit(boundary, CdcControllerEdge.Failed);
            }
            catch
            { /* preserve original */
            }
            throw;
        }
        Hit(boundary, CdcControllerEdge.After);
        return result;
    }

    public T Decorate<T>(T implementation, Func<string, CdcControllerBoundary> boundary)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, CdcControllerFixtureProxy>();
        var state = (CdcControllerFixtureProxy)(object)proxy;
        state.Initialize(implementation, this, boundary);
        return proxy;
    }

    public LocalCdcWorkflowJournalStore CreateJournalStore(string stateRoot) =>
        new(
            stateRoot,
            TimeProvider.System,
            boundary =>
                Hit(
                    CdcControllerBoundary.JournalWrite,
                    boundary switch
                    {
                        CdcWorkflowWriteBoundary.BeforeTemporaryWrite =>
                            CdcControllerEdge.BeforeTemporaryWrite,
                        CdcWorkflowWriteBoundary.AfterTemporaryFlush => CdcControllerEdge.AfterTemporaryFlush,
                        CdcWorkflowWriteBoundary.AfterAtomicReplacement =>
                            CdcControllerEdge.AfterAtomicReplacement,
                        _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
                    }
                )
        );

    public string SerializeTrace() => JsonSerializer.Serialize(Trace, TraceOptions);

    private static readonly JsonSerializerOptions TraceOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

// DispatchProxy requires a nonsealed accessible proxy with a public parameterless constructor.
public class CdcControllerFixtureProxy : DispatchProxy
{
    private object _implementation = null!;
    private CdcControllerFixtureHooks _hooks = null!;
    private Func<string, CdcControllerBoundary> _boundary = null!;

    internal void Initialize(
        object implementation,
        CdcControllerFixtureHooks hooks,
        Func<string, CdcControllerBoundary> boundary
    )
    {
        _implementation = implementation;
        _hooks = hooks;
        _boundary = boundary;
    }

    protected override object Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod ?? throw new InvalidOperationException("Missing fixture method.");
        var arguments = args ?? [];
        // Reject unsupported methods before invoking the real implementation.
        var type = method.ReturnType;
        if (type == typeof(Task))
        {
            return InvokeTaskAsync(method, arguments);
        }

        if (type == typeof(ValueTask))
        {
            return new ValueTask(InvokeTaskAsync(method, arguments));
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            return typeof(CdcControllerFixtureProxy)
                .GetMethod(nameof(InvokeResultAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(type.GenericTypeArguments)
                .Invoke(this, [method, arguments])!;
        }

        throw new NotSupportedException("Fixture hooks require Task, Task<T>, or ValueTask methods.");
    }

    private object InvokeInner(MethodInfo method, object?[] arguments)
    {
        try
        {
            return method.Invoke(_implementation, arguments)!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private Task<T> InvokeResultAsync<T>(MethodInfo method, object?[] arguments) =>
        _hooks.InvokeAsync(
            _boundary(method.Name),
            _ => (Task<T>)InvokeInner(method, arguments),
            arguments.OfType<CancellationToken>().LastOrDefault()
        );

    private async Task InvokeTaskAsync(MethodInfo method, object?[] arguments) =>
        await _hooks.InvokeAsync(
            _boundary(method.Name),
            async _ =>
            {
                var result = InvokeInner(method, arguments);
                if (result is ValueTask valueTask)
                {
                    await valueTask;
                }
                else
                {
                    await (Task)result;
                }

                return true;
            },
            arguments.OfType<CancellationToken>().LastOrDefault()
        );
}
