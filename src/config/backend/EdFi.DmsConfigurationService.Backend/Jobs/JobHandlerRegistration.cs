// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// One registered job type (spec D-12, D-13): its key, the payload versions it accepts, and the payload, handler, and
/// validator types. It holds typed delegates so that code which knows only the job type can check a payload and run
/// the handler.
/// </summary>
public sealed class JobHandlerRegistration
{
    private JobHandlerRegistration(
        string jobType,
        IReadOnlySet<short> payloadVersions,
        Type payloadType,
        Type handlerType,
        Type validatorType,
        Func<IServiceProvider, string, JobPayloadCheck> checkPayload,
        Func<IServiceProvider, JobExecutionContext, object, CancellationToken, Task> execute
    )
    {
        JobType = jobType;
        PayloadVersions = payloadVersions;
        PayloadType = payloadType;
        HandlerType = handlerType;
        ValidatorType = validatorType;
        CheckPayload = checkPayload;
        Execute = execute;
    }

    public string JobType { get; }

    public IReadOnlySet<short> PayloadVersions { get; }

    public Type PayloadType { get; }

    public Type HandlerType { get; }

    public Type ValidatorType { get; }

    /// <summary>
    /// Reads <c>payloadJson</c> with the strict serializer, applies the validator resolved from the provider, and
    /// writes the payload back in the serializer's form.
    /// </summary>
    internal Func<IServiceProvider, string, JobPayloadCheck> CheckPayload { get; }

    /// <summary>Resolves the handler from the provider and runs it with a payload <see cref="CheckPayload"/> returned.</summary>
    internal Func<IServiceProvider, JobExecutionContext, object, CancellationToken, Task> Execute { get; }

    /// <summary>
    /// Builds a registration, or throws when the job type, a version, or the payload type is invalid, so that a bad
    /// registration fails startup.
    /// </summary>
    internal static JobHandlerRegistration Create<THandler, TPayload, TValidator>(
        string jobType,
        IReadOnlyCollection<short> payloadVersions
    )
        where THandler : class, IJobHandler<TPayload>
        where TPayload : class
        where TValidator : class, IJobPayloadValidator<TPayload>
    {
        if (!JobKeySyntax.IsValid(jobType))
        {
            throw new ArgumentException(
                $"The job type must be an ASCII letter followed by up to {JobKeySyntax.MaxLength - 1} ASCII letters, "
                    + "digits, '_', '.', or '-'.",
                nameof(jobType)
            );
        }

        if (payloadVersions.Count == 0)
        {
            throw new ArgumentException(
                $"Job type '{jobType}' must accept at least one payload version.",
                nameof(payloadVersions)
            );
        }

        if (payloadVersions.Any(version => version < 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(payloadVersions),
                $"Job type '{jobType}' has a payload version below 1."
            );
        }

        HashSet<short> versions = [.. payloadVersions];
        if (versions.Count != payloadVersions.Count)
        {
            throw new ArgumentException(
                $"Job type '{jobType}' lists a payload version more than once.",
                nameof(payloadVersions)
            );
        }

        JobPayloadContract.EnsureValid(typeof(TPayload));

        return new JobHandlerRegistration(
            jobType,
            versions,
            typeof(TPayload),
            typeof(THandler),
            typeof(TValidator),
            ReadAndValidate<TPayload, TValidator>,
            (services, context, payload, cancellationToken) =>
                services
                    .GetRequiredService<THandler>()
                    .ExecuteAsync(context, (TPayload)payload, cancellationToken)
        );
    }

    private static JobPayloadCheck ReadAndValidate<TPayload, TValidator>(
        IServiceProvider services,
        string payloadJson
    )
        where TPayload : class
        where TValidator : class, IJobPayloadValidator<TPayload>
    {
        TPayload payload;
        switch (JobPayloadSerializer.Deserialize<TPayload>(payloadJson))
        {
            case JobPayloadReadResult<TPayload>.Success read:
                payload = read.Payload;
                break;
            case JobPayloadReadResult<TPayload>.Failure unreadable:
                return new JobPayloadCheck.PayloadInvalid(unreadable.ReasonCode);
            default:
                throw new InvalidOperationException("The payload serializer returned an unknown result.");
        }

        if (services.GetRequiredService<TValidator>().Validate(payload) is [string firstFailure, ..])
        {
            return new JobPayloadCheck.PayloadInvalid(firstFailure);
        }

        return JobPayloadSerializer.Serialize(payload) switch
        {
            JobPayloadWriteResult.Success written => new JobPayloadCheck.Valid(payload, written.Json),
            JobPayloadWriteResult.Failure unwritable => new JobPayloadCheck.PayloadInvalid(
                unwritable.ReasonCode
            ),
            _ => throw new InvalidOperationException("The payload serializer returned an unknown result."),
        };
    }
}
