// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Backend;

internal static class DocumentCacheProjectionTelemetryLabel
{
    public const string Unknown = "unknown";
    public const string None = "none";
    public const string Active = "Active";
    public const string Backoff = "Backoff";
    public const string Cancelled = "Cancelled";
    public const string Gated = "Gated";
    public const string Idle = "Idle";
    public const string Mutated = "Mutated";
    public const string NotMutated = "NotMutated";
    public const string Ordinary = "Ordinary";
    public const string Administrative = "Administrative";
    public const string WriterOutcome = "WriterOutcome";
    public const string MaterializerOutcome = "MaterializerOutcome";
    public const string ProviderFailure = "ProviderFailure";
    public const string TargetPaused = "TargetPaused";
    public const string SchedulerDispatch = "SchedulerDispatch";
    public const string TargetState = "TargetState";
    public const string Mutex = "Mutex";
}

internal sealed record DocumentCacheProjectionTelemetryContext
{
    private const int MaxLabelLength = 128;

    public DocumentCacheProjectionTelemetryContext(
        string provider,
        string targetKey,
        string outcome,
        string category,
        string lifecycle,
        string command,
        string phase
    )
    {
        Provider = BoundSanitizedLabel(provider, nameof(provider));
        TargetKey = BoundSanitizedLabel(targetKey, nameof(targetKey));
        Outcome = BoundSanitizedLabel(outcome, nameof(outcome));
        Category = BoundSanitizedLabel(category, nameof(category));
        Lifecycle = BoundSanitizedLabel(lifecycle, nameof(lifecycle));
        Command = BoundSanitizedLabel(command, nameof(command));
        Phase = BoundSanitizedLabel(phase, nameof(phase));
    }

    public string Provider { get; }

    public string TargetKey { get; }

    public string Outcome { get; }

    public string Category { get; }

    public string Lifecycle { get; }

    public string Command { get; }

    public string Phase { get; }

    public TagList ToTags()
    {
        return
        [
            new("provider", Provider),
            new("target_key", TargetKey),
            new("outcome", Outcome),
            new("category", Category),
            new("lifecycle", Lifecycle),
            new("command", Command),
            new("phase", Phase),
        ];
    }

    public static DocumentCacheProjectionTelemetryContext ForTargetObservation(
        DocumentCacheProjectionTargetHealthSnapshot snapshot
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new(
            ProviderLabel(snapshot.ProviderToken),
            TargetKeyLabel(snapshot.TargetKey),
            TargetExecutionOutcome(snapshot.ExecutionState),
            TargetExecutionCategory(snapshot),
            LifecycleLabel(snapshot.LifecycleFence.Lifecycle?.State),
            CommandLabel(snapshot.ActiveAdministrativeCommand),
            PhaseLabel(snapshot.ActiveAdministrativePhase)
        );
    }

    public static DocumentCacheProjectionTelemetryContext ForSchedulerDispatch(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentCacheProjectionSchedulerDispatchResult result,
        DocumentCacheProjectionDrainInvocationKind invocationKind
    )
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        ArgumentNullException.ThrowIfNull(result);

        DocumentCacheAdministrativeCommandExecutionContext? commandContext =
            targetContext.AdministrativeCommandContext;
        string category =
            result.BlockReason?.ToString()
            ?? result.DrainResult?.AdministrativeFailure?.DiagnosticCategory.ToString()
            ?? result.DrainResult?.Outcome.ToString()
            ?? DocumentCacheProjectionTelemetryLabel.SchedulerDispatch;

        return new(
            ProviderLabel(targetContext.TargetExecutionContext.ProviderToken),
            TargetKeyLabel(targetContext.TargetKey),
            result.Status.ToString(),
            category,
            LifecycleLabel(targetContext.TargetExecutionContext.Lifecycle.State),
            invocationKind == DocumentCacheProjectionDrainInvocationKind.Administrative
                ? CommandLabel(commandContext?.Request.Command)
                : DocumentCacheProjectionTelemetryLabel.None,
            invocationKind == DocumentCacheProjectionDrainInvocationKind.Administrative
                ? PhaseLabel(commandContext?.CurrentPhase)
                : DocumentCacheProjectionTelemetryLabel.None
        );
    }

    public static DocumentCacheProjectionTelemetryContext ForItemOutcome(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentCacheProjectionDrainInvocationKind invocationKind,
        string outcome,
        string category,
        DocumentCacheLifecycleState? lifecycle = null
    )
    {
        ArgumentNullException.ThrowIfNull(targetContext);

        DocumentCacheAdministrativeCommandExecutionContext? commandContext =
            targetContext.AdministrativeCommandContext;

        return new(
            ProviderLabel(targetContext.TargetExecutionContext.ProviderToken),
            TargetKeyLabel(targetContext.TargetKey),
            outcome,
            category,
            LifecycleLabel(lifecycle ?? targetContext.TargetExecutionContext.Lifecycle.State),
            invocationKind == DocumentCacheProjectionDrainInvocationKind.Administrative
                ? CommandLabel(commandContext?.Request.Command)
                : DocumentCacheProjectionTelemetryLabel.None,
            invocationKind == DocumentCacheProjectionDrainInvocationKind.Administrative
                ? PhaseLabel(commandContext?.CurrentPhase)
                : DocumentCacheProjectionTelemetryLabel.None
        );
    }

    public static DocumentCacheProjectionTelemetryContext ForAdministrativeObservation(
        DocumentCacheAdministrativeCommandObservationSnapshot snapshot,
        RelationalProviderToken providerToken
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new(
            ProviderLabel(providerToken),
            TargetKeyLabel(snapshot.TargetKey),
            snapshot.Mutated
                ? DocumentCacheProjectionTelemetryLabel.Mutated
                : DocumentCacheProjectionTelemetryLabel.NotMutated,
            LatestDiagnosticCategory(snapshot.PhaseDiagnostics),
            LifecycleLabel(snapshot.Lifecycle),
            CommandLabel(snapshot.Command),
            PhaseLabel(snapshot.CurrentPhase)
        );
    }

    public static DocumentCacheProjectionTelemetryContext ForAdministrativeResult(
        DocumentCacheAdministrativeCommandResult result,
        RelationalProviderToken? providerToken,
        DocumentCacheAdministrativeCommandPhase? currentPhase = null
    )
    {
        ArgumentNullException.ThrowIfNull(result);

        return new(
            ProviderLabel(providerToken),
            TargetKeyLabel(result.TargetKey.TargetKey),
            result.Status.ToString(),
            result.Classification.ToString(),
            LifecycleLabel(result.Lifecycle),
            CommandLabel(result.Command),
            PhaseLabel(currentPhase ?? LatestPhase(result.PhaseDiagnostics))
        );
    }

    public static DocumentCacheProjectionTelemetryContext ForAdministrativeMutex(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheTargetKey targetKey,
        RelationalProviderToken providerToken,
        string outcome,
        DocumentCacheAdministrativeDiagnosticCategory? category
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        return new(
            ProviderLabel(providerToken),
            TargetKeyLabel(targetKey),
            outcome,
            category?.ToString() ?? DocumentCacheProjectionTelemetryLabel.Mutex,
            DocumentCacheProjectionTelemetryLabel.Unknown,
            CommandLabel(command),
            PhaseLabel(DocumentCacheAdministrativeCommandPhase.AcquireMutex)
        );
    }

    private static string ProviderLabel(RelationalProviderToken? providerToken) =>
        providerToken?.Value ?? DocumentCacheProjectionTelemetryLabel.Unknown;

    private static string TargetKeyLabel(DocumentCacheTargetKey targetKey)
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        return targetKey.ToString();
    }

    private static string CommandLabel(DocumentCacheAdministrativeCommand? command) =>
        command?.ToString() ?? DocumentCacheProjectionTelemetryLabel.None;

    private static string PhaseLabel(DocumentCacheAdministrativeCommandPhase? phase) =>
        phase?.ToString() ?? DocumentCacheProjectionTelemetryLabel.None;

    private static string LifecycleLabel(DocumentCacheLifecycleState? lifecycle) =>
        lifecycle?.ToString() ?? DocumentCacheProjectionTelemetryLabel.Unknown;

    private static string TargetExecutionOutcome(DocumentCacheProjectionExecutionStateSnapshot execution) =>
        execution switch
        {
            { CancellationRequested: true } => DocumentCacheProjectionTelemetryLabel.Cancelled,
            { IsActivelyProcessing: true } => DocumentCacheProjectionTelemetryLabel.Active,
            { IsWaitingForWorkerGate: true } => DocumentCacheProjectionTelemetryLabel.Gated,
            { IsInBackoff: true } => DocumentCacheProjectionTelemetryLabel.Backoff,
            _ => DocumentCacheProjectionTelemetryLabel.Idle,
        };

    private static string TargetExecutionCategory(DocumentCacheProjectionTargetHealthSnapshot snapshot)
    {
        if (snapshot.FailureDiagnostics.FailureCount > 0)
        {
            return DocumentCacheProjectionDocumentDiagnosticCategory.ProviderFailure.ToString();
        }

        if (snapshot.PoisonTraversal.SuppressedDocumentCount > 0)
        {
            return DocumentCacheProjectionDocumentDiagnosticCategory.PoisonSuppressed.ToString();
        }

        if (!snapshot.TargetDiagnostics.IsEmpty)
        {
            return snapshot.TargetDiagnostics[^1].Category.ToString();
        }

        if (snapshot.LifecycleFence.State != DocumentCacheProjectionLifecycleFenceState.Eligible)
        {
            return snapshot.LifecycleFence.DiagnosticCategory?.ToString()
                ?? snapshot.LifecycleFence.State.ToString();
        }

        return DocumentCacheProjectionTelemetryLabel.TargetState;
    }

    private static string LatestDiagnosticCategory(
        ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> diagnostics
    ) =>
        diagnostics.IsDefaultOrEmpty
            ? DocumentCacheProjectionTelemetryLabel.None
            : diagnostics[^1].DiagnosticCategory.ToString();

    private static DocumentCacheAdministrativeCommandPhase? LatestPhase(
        ImmutableArray<DocumentCacheAdministrativePhaseDiagnostic> diagnostics
    ) => diagnostics.IsDefaultOrEmpty ? null : diagnostics[^1].CurrentPhase;

    private static string BoundSanitizedLabel(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Metric label must be present.", parameterName);
        }

        string sanitized = LoggingSanitizer.SanitizeForLogging(value);
        if (sanitized.Length == 0)
        {
            sanitized = DocumentCacheProjectionTelemetryLabel.Unknown;
        }

        return sanitized.Length <= MaxLabelLength ? sanitized : sanitized[..MaxLabelLength];
    }
}

public interface IDocumentCacheProjectionTelemetry
{
    void RecordTargetObservation(DocumentCacheProjectionTargetHealthSnapshot snapshot);

    void RecordSchedulerDispatch(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentCacheProjectionSchedulerDispatchResult result,
        DocumentCacheProjectionDrainInvocationKind invocationKind
    );

    void RecordItemOutcome(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentCacheProjectionDrainInvocationKind invocationKind,
        string outcome,
        string category,
        DocumentCacheLifecycleState? lifecycle = null
    );

    void RecordAdministrativeCommandObservation(
        DocumentCacheAdministrativeCommandObservationSnapshot snapshot,
        RelationalProviderToken providerToken
    );

    void RecordAdministrativeCommandMutation(
        DocumentCacheAdministrativeCommandObservationSnapshot snapshot,
        RelationalProviderToken providerToken
    );

    void RecordAdministrativeCommandResult(
        DocumentCacheAdministrativeCommandResult result,
        RelationalProviderToken? providerToken,
        TimeSpan? effectiveWorkflowTimeout = null,
        DocumentCacheAdministrativeCommandPhase? currentPhase = null
    );

    void RecordAdministrativeMutexOutcome(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheTargetKey targetKey,
        RelationalProviderToken providerToken,
        string outcome,
        DocumentCacheAdministrativeDiagnosticCategory? category,
        TimeSpan duration
    );
}

internal sealed class NoOpDocumentCacheProjectionTelemetry : IDocumentCacheProjectionTelemetry
{
    public static NoOpDocumentCacheProjectionTelemetry Instance { get; } = new();

    private NoOpDocumentCacheProjectionTelemetry() { }

    public void RecordTargetObservation(DocumentCacheProjectionTargetHealthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
    }

    public void RecordSchedulerDispatch(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentCacheProjectionSchedulerDispatchResult result,
        DocumentCacheProjectionDrainInvocationKind invocationKind
    )
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        ArgumentNullException.ThrowIfNull(result);
        DocumentCacheMaterializerGuards.RequireDefined(
            invocationKind,
            nameof(invocationKind),
            "Unsupported DocumentCache projection drain invocation kind."
        );
    }

    public void RecordItemOutcome(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentCacheProjectionDrainInvocationKind invocationKind,
        string outcome,
        string category,
        DocumentCacheLifecycleState? lifecycle = null
    )
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        DocumentCacheMaterializerGuards.RequireDefined(
            invocationKind,
            nameof(invocationKind),
            "Unsupported DocumentCache projection drain invocation kind."
        );
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        if (lifecycle is not null)
        {
            DocumentCacheMaterializerGuards.RequireDefined(
                lifecycle.Value,
                nameof(lifecycle),
                "Unsupported DocumentCache lifecycle state."
            );
        }
    }

    public void RecordAdministrativeCommandObservation(
        DocumentCacheAdministrativeCommandObservationSnapshot snapshot,
        RelationalProviderToken providerToken
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(providerToken);
    }

    public void RecordAdministrativeCommandMutation(
        DocumentCacheAdministrativeCommandObservationSnapshot snapshot,
        RelationalProviderToken providerToken
    ) => RecordAdministrativeCommandObservation(snapshot, providerToken);

    public void RecordAdministrativeCommandResult(
        DocumentCacheAdministrativeCommandResult result,
        RelationalProviderToken? providerToken,
        TimeSpan? effectiveWorkflowTimeout = null,
        DocumentCacheAdministrativeCommandPhase? currentPhase = null
    )
    {
        ArgumentNullException.ThrowIfNull(result);
        if (effectiveWorkflowTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(effectiveWorkflowTimeout),
                "Effective workflow timeout must be positive when supplied."
            );
        }

        if (currentPhase is not null)
        {
            DocumentCacheMaterializerGuards.RequireDefined(
                currentPhase.Value,
                nameof(currentPhase),
                "Unsupported administrative command phase."
            );
        }
    }

    public void RecordAdministrativeMutexOutcome(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheTargetKey targetKey,
        RelationalProviderToken providerToken,
        string outcome,
        DocumentCacheAdministrativeDiagnosticCategory? category,
        TimeSpan duration
    )
    {
        DocumentCacheMaterializerGuards.RequireDefined(
            command,
            nameof(command),
            "Unsupported administrative command."
        );
        ArgumentNullException.ThrowIfNull(targetKey);
        ArgumentNullException.ThrowIfNull(providerToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        if (category is not null)
        {
            DocumentCacheMaterializerGuards.RequireDefined(
                category.Value,
                nameof(category),
                "Unsupported administrative diagnostic category."
            );
        }
    }
}

internal sealed class DocumentCacheProjectionTelemetry : IDocumentCacheProjectionTelemetry
{
    internal const string MeterName = "EdFi.DataManagementService.DocumentCacheProjection";
    internal const string TargetStateCounterName = "edfi.dms.document_cache.projection.target_state";
    internal const string DispatchCounterName = "edfi.dms.document_cache.projection.dispatches";
    internal const string DispatchDurationName = "edfi.dms.document_cache.projection.dispatch.duration";
    internal const string DispatchItemCountName = "edfi.dms.document_cache.projection.dispatch.items";
    internal const string PoisonSuppressedCountName =
        "edfi.dms.document_cache.projection.poison_suppressed.documents";
    internal const string FailureBackoffCountName =
        "edfi.dms.document_cache.projection.failure_backoff.documents";
    internal const string ItemOutcomeCounterName = "edfi.dms.document_cache.projection.item.outcomes";
    internal const string AdministrativePhaseCounterName =
        "edfi.dms.document_cache.administration.phase.observations";
    internal const string AdministrativeMutationCounterName =
        "edfi.dms.document_cache.administration.phase.mutations";
    internal const string AdministrativeCommandResultCounterName =
        "edfi.dms.document_cache.administration.command.results";
    internal const string AdministrativeCommandDurationName =
        "edfi.dms.document_cache.administration.command.duration";
    internal const string AdministrativeWorkflowTimeoutName =
        "edfi.dms.document_cache.administration.workflow_timeout.duration";
    internal const string AdministrativeMutexDurationName =
        "edfi.dms.document_cache.administration.mutex.duration";

    private static readonly Meter SharedMeter = new(MeterName);

    private readonly Counter<long> _targetStateCounter;
    private readonly Counter<long> _dispatchCounter;
    private readonly Histogram<double> _dispatchDuration;
    private readonly Histogram<int> _dispatchItemCount;
    private readonly Histogram<int> _poisonSuppressedCount;
    private readonly Histogram<int> _failureBackoffCount;
    private readonly Counter<long> _itemOutcomeCounter;
    private readonly Counter<long> _administrativePhaseCounter;
    private readonly Counter<long> _administrativeMutationCounter;
    private readonly Counter<long> _administrativeCommandResultCounter;
    private readonly Histogram<double> _administrativeCommandDuration;
    private readonly Histogram<double> _administrativeWorkflowTimeout;
    private readonly Histogram<double> _administrativeMutexDuration;
    private readonly ILogger<DocumentCacheProjectionTelemetry> _logger;

    public DocumentCacheProjectionTelemetry(ILogger<DocumentCacheProjectionTelemetry>? logger = null)
        : this(SharedMeter, logger) { }

    internal DocumentCacheProjectionTelemetry(
        Meter meter,
        ILogger<DocumentCacheProjectionTelemetry>? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(meter);

        _logger = logger ?? NullLogger<DocumentCacheProjectionTelemetry>.Instance;
        _targetStateCounter = meter.CreateCounter<long>(
            TargetStateCounterName,
            unit: "{observation}",
            description: "DocumentCache projection target state observations."
        );
        _dispatchCounter = meter.CreateCounter<long>(
            DispatchCounterName,
            unit: "{dispatch}",
            description: "DocumentCache projection scheduler dispatch outcomes."
        );
        _dispatchDuration = meter.CreateHistogram<double>(
            DispatchDurationName,
            unit: "ms",
            description: "DocumentCache projection scheduler dispatch duration."
        );
        _dispatchItemCount = meter.CreateHistogram<int>(
            DispatchItemCountName,
            unit: "{item}",
            description: "DocumentCache projection dispatch processed item count."
        );
        _poisonSuppressedCount = meter.CreateHistogram<int>(
            PoisonSuppressedCountName,
            unit: "{document}",
            description: "DocumentCache projection locally suppressed poison document count."
        );
        _failureBackoffCount = meter.CreateHistogram<int>(
            FailureBackoffCountName,
            unit: "{document}",
            description: "DocumentCache projection local failure-backoff document count."
        );
        _itemOutcomeCounter = meter.CreateCounter<long>(
            ItemOutcomeCounterName,
            unit: "{outcome}",
            description: "DocumentCache projection item processor outcomes."
        );
        _administrativePhaseCounter = meter.CreateCounter<long>(
            AdministrativePhaseCounterName,
            unit: "{observation}",
            description: "DocumentCache administrative command phase observations."
        );
        _administrativeMutationCounter = meter.CreateCounter<long>(
            AdministrativeMutationCounterName,
            unit: "{mutation}",
            description: "DocumentCache administrative command durable mutation observations."
        );
        _administrativeCommandResultCounter = meter.CreateCounter<long>(
            AdministrativeCommandResultCounterName,
            unit: "{result}",
            description: "DocumentCache administrative command result outcomes."
        );
        _administrativeCommandDuration = meter.CreateHistogram<double>(
            AdministrativeCommandDurationName,
            unit: "ms",
            description: "DocumentCache administrative command elapsed time after mutex acquisition."
        );
        _administrativeWorkflowTimeout = meter.CreateHistogram<double>(
            AdministrativeWorkflowTimeoutName,
            unit: "ms",
            description: "DocumentCache administrative command effective workflow timeout."
        );
        _administrativeMutexDuration = meter.CreateHistogram<double>(
            AdministrativeMutexDurationName,
            unit: "ms",
            description: "DocumentCache administrative mutex acquisition duration."
        );
    }

    public void RecordTargetObservation(DocumentCacheProjectionTargetHealthSnapshot snapshot)
    {
        DocumentCacheProjectionTelemetryContext context =
            DocumentCacheProjectionTelemetryContext.ForTargetObservation(snapshot);
        TagList tags = context.ToTags();

        _targetStateCounter.Add(1, tags);
        _poisonSuppressedCount.Record(snapshot.PoisonTraversal.SuppressedDocumentCount, tags);
        _failureBackoffCount.Record(snapshot.FailureDiagnostics.FailureCount, tags);
        LogDebug(context);
    }

    public void RecordSchedulerDispatch(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentCacheProjectionSchedulerDispatchResult result,
        DocumentCacheProjectionDrainInvocationKind invocationKind
    )
    {
        DocumentCacheProjectionTelemetryContext context =
            DocumentCacheProjectionTelemetryContext.ForSchedulerDispatch(
                targetContext,
                result,
                invocationKind
            );
        TagList tags = context.ToTags();

        _dispatchCounter.Add(1, tags);
        if (result.CompletedAt is not null)
        {
            _dispatchDuration.Record(
                ClampToNonNegativeMilliseconds(result.CompletedAt.Value - result.ObservedAt),
                tags
            );
        }

        if (result.DrainResult is not null)
        {
            _dispatchItemCount.Record(result.DrainResult.ProcessedItemCount, tags);
        }

        LogDebug(context);
    }

    public void RecordItemOutcome(
        DocumentCacheProjectionTargetRuntimeContext targetContext,
        DocumentCacheProjectionDrainInvocationKind invocationKind,
        string outcome,
        string category,
        DocumentCacheLifecycleState? lifecycle = null
    )
    {
        DocumentCacheProjectionTelemetryContext context =
            DocumentCacheProjectionTelemetryContext.ForItemOutcome(
                targetContext,
                invocationKind,
                outcome,
                category,
                lifecycle
            );
        _itemOutcomeCounter.Add(1, context.ToTags());
        LogDebug(context);
    }

    public void RecordAdministrativeCommandObservation(
        DocumentCacheAdministrativeCommandObservationSnapshot snapshot,
        RelationalProviderToken providerToken
    )
    {
        DocumentCacheProjectionTelemetryContext context =
            DocumentCacheProjectionTelemetryContext.ForAdministrativeObservation(snapshot, providerToken);
        TagList tags = context.ToTags();

        _administrativePhaseCounter.Add(1, tags);
        _administrativeWorkflowTimeout.Record(
            ClampToNonNegativeMilliseconds(snapshot.EffectiveWorkflowTimeout),
            tags
        );
        LogDebug(context);
    }

    public void RecordAdministrativeCommandMutation(
        DocumentCacheAdministrativeCommandObservationSnapshot snapshot,
        RelationalProviderToken providerToken
    )
    {
        DocumentCacheProjectionTelemetryContext context =
            DocumentCacheProjectionTelemetryContext.ForAdministrativeObservation(snapshot, providerToken);
        _administrativeMutationCounter.Add(1, context.ToTags());
        LogDebug(context);
    }

    public void RecordAdministrativeCommandResult(
        DocumentCacheAdministrativeCommandResult result,
        RelationalProviderToken? providerToken,
        TimeSpan? effectiveWorkflowTimeout = null,
        DocumentCacheAdministrativeCommandPhase? currentPhase = null
    )
    {
        DocumentCacheProjectionTelemetryContext context =
            DocumentCacheProjectionTelemetryContext.ForAdministrativeResult(
                result,
                providerToken,
                currentPhase
            );
        TagList tags = context.ToTags();

        _administrativeCommandResultCounter.Add(1, tags);
        if (result.ElapsedCommandTime is not null)
        {
            _administrativeCommandDuration.Record(
                ClampToNonNegativeMilliseconds(result.ElapsedCommandTime.Value),
                tags
            );
        }

        if (effectiveWorkflowTimeout is not null)
        {
            _administrativeWorkflowTimeout.Record(
                ClampToNonNegativeMilliseconds(effectiveWorkflowTimeout.Value),
                tags
            );
        }

        LogAdministrativeResult(context, result.Status);
    }

    public void RecordAdministrativeMutexOutcome(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheTargetKey targetKey,
        RelationalProviderToken providerToken,
        string outcome,
        DocumentCacheAdministrativeDiagnosticCategory? category,
        TimeSpan duration
    )
    {
        DocumentCacheProjectionTelemetryContext context =
            DocumentCacheProjectionTelemetryContext.ForAdministrativeMutex(
                command,
                targetKey,
                providerToken,
                outcome,
                category
            );
        _administrativeMutexDuration.Record(ClampToNonNegativeMilliseconds(duration), context.ToTags());
        LogDebug(context);
    }

    internal static TimeSpan GetElapsedTime(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp);

    private void LogDebug(DocumentCacheProjectionTelemetryContext context)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        _logger.LogDebug(
            "DocumentCache telemetry recorded event. Provider {Provider}; target {TargetKey}; outcome {Outcome}; category {Category}; lifecycle {Lifecycle}; command {Command}; phase {Phase}.",
            context.Provider,
            context.TargetKey,
            context.Outcome,
            context.Category,
            context.Lifecycle,
            context.Command,
            context.Phase
        );
    }

    private void LogAdministrativeResult(
        DocumentCacheProjectionTelemetryContext context,
        DocumentCacheAdministrativeCommandStatus status
    )
    {
        LogLevel level =
            status == DocumentCacheAdministrativeCommandStatus.Completed
                ? LogLevel.Information
                : LogLevel.Warning;
        if (!_logger.IsEnabled(level))
        {
            return;
        }

        _logger.Log(
            level,
            "DocumentCache administrative telemetry recorded command result. Provider {Provider}; target {TargetKey}; outcome {Outcome}; category {Category}; lifecycle {Lifecycle}; command {Command}; phase {Phase}.",
            context.Provider,
            context.TargetKey,
            context.Outcome,
            context.Category,
            context.Lifecycle,
            context.Command,
            context.Phase
        );
    }

    private static double ClampToNonNegativeMilliseconds(TimeSpan duration) =>
        duration < TimeSpan.Zero ? 0 : duration.TotalMilliseconds;
}
