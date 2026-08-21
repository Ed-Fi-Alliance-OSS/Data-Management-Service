// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Backend;

internal interface IDocumentCacheStatusCurrentSourceObserver
{
    RelationalProviderToken ProviderToken { get; }

    Task<DocumentCacheStatusCurrentSourceObservationResult> ObserveAsync(
        DocumentCacheStatusCurrentSourceObservationRequest request,
        CancellationToken cancellationToken = default
    );
}

internal sealed record DocumentCacheStatusCurrentSourceObservationRequest
{
    public DocumentCacheStatusCurrentSourceObservationRequest(
        DocumentCacheTargetExecutionContext targetExecutionContext
    )
    {
        TargetExecutionContext =
            targetExecutionContext ?? throw new ArgumentNullException(nameof(targetExecutionContext));
    }

    public DocumentCacheTargetExecutionContext TargetExecutionContext { get; }
}

internal enum DocumentCacheStatusCurrentSourceObservationOutcome
{
    Succeeded,
    StateMissingOrInvalid,
    ProviderTimeout,
    Cancelled,
    Failed,
}

internal enum DocumentCacheStatusDurableQueuePresence
{
    Empty,
    NotEmpty,
}

internal sealed record DocumentCacheStatusCurrentSourceObservationResult
{
    private DocumentCacheStatusCurrentSourceObservationResult(
        DocumentCacheStatusCurrentSourceObservationOutcome outcome,
        DocumentCacheLifecycleState? lifecycleState,
        bool? cacheAheadRecoveryRequired,
        DocumentCacheStatusDurableQueuePresence? queuePresence,
        DateTimeOffset? oldestWorkFirstEnqueuedAt,
        double? oldestWorkAgeSeconds,
        DateTimeOffset? durableObservedAt,
        string message
    )
    {
        Outcome = DocumentCacheStatusCurrentSourceObservationGuard.RequireDefined(
            outcome,
            nameof(outcome),
            "Unsupported current-source observation outcome."
        );
        LifecycleState = lifecycleState;
        CacheAheadRecoveryRequired = cacheAheadRecoveryRequired;
        QueuePresence = queuePresence;
        OldestWorkFirstEnqueuedAt = NormalizeUtc(oldestWorkFirstEnqueuedAt);
        OldestWorkAgeSeconds = oldestWorkAgeSeconds;
        DurableObservedAt = NormalizeUtc(durableObservedAt);
        Message = DocumentCacheStatusCurrentSourceObservationText.Sanitize(message);

        Validate();
    }

    public DocumentCacheStatusCurrentSourceObservationOutcome Outcome { get; }

    public DocumentCacheLifecycleState? LifecycleState { get; }

    public bool? CacheAheadRecoveryRequired { get; }

    public DocumentCacheStatusDurableQueuePresence? QueuePresence { get; }

    public DateTimeOffset? OldestWorkFirstEnqueuedAt { get; }

    public double? OldestWorkAgeSeconds { get; }

    public DateTimeOffset? DurableObservedAt { get; }

    public string Message { get; }

    public bool Succeeded => Outcome == DocumentCacheStatusCurrentSourceObservationOutcome.Succeeded;

    public bool HasWork => QueuePresence == DocumentCacheStatusDurableQueuePresence.NotEmpty;

    public static DocumentCacheStatusCurrentSourceObservationResult Success(
        DocumentCacheLifecycleState lifecycleState,
        bool cacheAheadRecoveryRequired,
        DocumentCacheStatusDurableQueuePresence queuePresence,
        DateTimeOffset? oldestWorkFirstEnqueuedAt,
        double? oldestWorkAgeSeconds,
        DateTimeOffset durableObservedAt
    ) =>
        new(
            DocumentCacheStatusCurrentSourceObservationOutcome.Succeeded,
            lifecycleState,
            cacheAheadRecoveryRequired,
            queuePresence,
            oldestWorkFirstEnqueuedAt,
            oldestWorkAgeSeconds,
            durableObservedAt,
            "DocumentCache status current-source observation succeeded."
        );

    public static DocumentCacheStatusCurrentSourceObservationResult StateMissingOrInvalid(
        DateTimeOffset durableObservedAt,
        string message
    ) =>
        StateMissingOrInvalid(
            durableObservedAt,
            queuePresence: null,
            oldestWorkFirstEnqueuedAt: null,
            oldestWorkAgeSeconds: null,
            message
        );

    public static DocumentCacheStatusCurrentSourceObservationResult StateMissingOrInvalid(
        DateTimeOffset durableObservedAt,
        DocumentCacheStatusDurableQueuePresence? queuePresence,
        DateTimeOffset? oldestWorkFirstEnqueuedAt,
        double? oldestWorkAgeSeconds,
        string message
    ) =>
        new(
            DocumentCacheStatusCurrentSourceObservationOutcome.StateMissingOrInvalid,
            lifecycleState: null,
            cacheAheadRecoveryRequired: null,
            queuePresence,
            oldestWorkFirstEnqueuedAt,
            oldestWorkAgeSeconds,
            durableObservedAt,
            message
        );

    public static DocumentCacheStatusCurrentSourceObservationResult ProviderTimeout(string message) =>
        Failure(DocumentCacheStatusCurrentSourceObservationOutcome.ProviderTimeout, message);

    public static DocumentCacheStatusCurrentSourceObservationResult Cancelled(string message) =>
        Failure(DocumentCacheStatusCurrentSourceObservationOutcome.Cancelled, message);

    public static DocumentCacheStatusCurrentSourceObservationResult Failed(string message) =>
        Failure(DocumentCacheStatusCurrentSourceObservationOutcome.Failed, message);

    private static DocumentCacheStatusCurrentSourceObservationResult Failure(
        DocumentCacheStatusCurrentSourceObservationOutcome outcome,
        string message
    ) =>
        new(
            outcome,
            lifecycleState: null,
            cacheAheadRecoveryRequired: null,
            queuePresence: null,
            oldestWorkFirstEnqueuedAt: null,
            oldestWorkAgeSeconds: null,
            durableObservedAt: null,
            message
        );

    private void Validate()
    {
        if (OldestWorkAgeSeconds is < 0)
        {
            throw new ArgumentException("Oldest work age must not be negative.");
        }

        if (Outcome == DocumentCacheStatusCurrentSourceObservationOutcome.Succeeded)
        {
            if (
                LifecycleState is null
                || CacheAheadRecoveryRequired is null
                || QueuePresence is null
                || DurableObservedAt is null
            )
            {
                throw new ArgumentException(
                    "Successful current-source observations require lifecycle, cache-ahead, queue, and durable timestamp facts."
                );
            }

            ValidateQueueFacts(queuePresenceRequired: true);

            return;
        }

        if (Outcome == DocumentCacheStatusCurrentSourceObservationOutcome.StateMissingOrInvalid)
        {
            if (DurableObservedAt is null)
            {
                throw new ArgumentException(
                    "Missing or invalid state observations require the provider timestamp observed by the statement."
                );
            }

            if (LifecycleState is not null || CacheAheadRecoveryRequired is not null)
            {
                throw new ArgumentException(
                    "Missing or invalid state observations must not carry lifecycle or cache-ahead facts."
                );
            }

            ValidateQueueFacts(queuePresenceRequired: false);

            return;
        }

        if (
            LifecycleState is not null
            || CacheAheadRecoveryRequired is not null
            || QueuePresence is not null
            || OldestWorkFirstEnqueuedAt is not null
            || OldestWorkAgeSeconds is not null
            || DurableObservedAt is not null
        )
        {
            throw new ArgumentException("Failed current-source observations must not carry durable facts.");
        }
    }

    private void ValidateQueueFacts(bool queuePresenceRequired)
    {
        if (QueuePresence is null)
        {
            if (queuePresenceRequired)
            {
                throw new ArgumentException("Queue observations require queue presence.");
            }

            if (OldestWorkFirstEnqueuedAt is not null || OldestWorkAgeSeconds is not null)
            {
                throw new ArgumentException(
                    "Queue observations without queue presence must not carry oldest-work facts."
                );
            }

            return;
        }

        DocumentCacheStatusCurrentSourceObservationGuard.RequireDefined(
            QueuePresence.Value,
            nameof(QueuePresence),
            "Unsupported queue presence."
        );

        if (QueuePresence == DocumentCacheStatusDurableQueuePresence.Empty)
        {
            if (OldestWorkFirstEnqueuedAt is not null || OldestWorkAgeSeconds is not null)
            {
                throw new ArgumentException("Empty queue observations must not carry oldest-work facts.");
            }
        }
        else if (OldestWorkFirstEnqueuedAt is null || OldestWorkAgeSeconds is null)
        {
            throw new ArgumentException("Non-empty queue observations require oldest-work facts.");
        }
    }

    private static DateTimeOffset? NormalizeUtc(DateTimeOffset? value) => value?.ToUniversalTime();
}

internal static class DocumentCacheStatusCurrentSourceObservationGuard
{
    public static string RequireConnectionString(
        DocumentCacheStatusCurrentSourceObservationRequest request,
        RelationalProviderToken providerToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(providerToken);

        DocumentCacheTargetExecutionContext executionContext = request.TargetExecutionContext;
        if (executionContext.ProviderToken != providerToken)
        {
            throw new InvalidOperationException(
                "DocumentCache status current-source observer provider "
                    + $"'{providerToken}' does not match target provider "
                    + $"'{executionContext.ProviderToken}'."
            );
        }

        if (executionContext.ConnectionInput.ProviderToken != providerToken)
        {
            throw new InvalidOperationException(
                "DocumentCache status current-source observer connection provider "
                    + $"'{executionContext.ConnectionInput.ProviderToken}' does not match adapter provider "
                    + $"'{providerToken}'."
            );
        }

        return executionContext.ConnectionInput.Value;
    }

    public static TEnum RequireDefined<TEnum>(TEnum value, string parameterName, string message)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(typeof(TEnum), value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, message);
        }

        return value;
    }

    public static DateTimeOffset NormalizeUtcTimestamp(object value) =>
        value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime(),
            DateTime dateTime => new DateTimeOffset(
                dateTime.Kind == DateTimeKind.Utc
                    ? dateTime
                    : DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
            ),
            _ => DateTimeOffset
                .Parse(
                    value.ToString()
                        ?? throw new InvalidOperationException("DocumentCache status timestamp was null."),
                    System.Globalization.CultureInfo.InvariantCulture
                )
                .ToUniversalTime(),
        };
}

internal static class DocumentCacheStatusCurrentSourceObservationText
{
    private const int MaximumLength = 512;

    public static string Sanitize(string? message)
    {
        string sanitized = LoggingSanitizer.SanitizeForLogging(message);
        return sanitized.Length <= MaximumLength ? sanitized : sanitized[..MaximumLength];
    }
}
