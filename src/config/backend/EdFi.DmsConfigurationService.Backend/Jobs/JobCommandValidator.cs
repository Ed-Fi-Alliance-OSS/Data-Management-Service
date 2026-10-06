// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>The outcome of checking a job type, payload version, and payload against the registry.</summary>
public record JobPayloadCheck
{
    /// <summary>
    /// The payload passed every check. <paramref name="PayloadJson"/> is the serializer's own form of it, the only form
    /// that is ever persisted.
    /// </summary>
    public record Valid(object Payload, string PayloadJson) : JobPayloadCheck;

    public record UnsupportedType() : JobPayloadCheck;

    public record UnsupportedVersion() : JobPayloadCheck;

    /// <summary>The payload broke its contract; <paramref name="ReasonCode"/> is a fixed code, not input text.</summary>
    public record PayloadInvalid(string ReasonCode) : JobPayloadCheck;
}

/// <summary>
/// The one check that both <see cref="IJobEnqueuer"/> and <see cref="IJobScheduleService"/> apply before anything is
/// written (spec D-10, D-12, D-13): the job type must be registered, the version must be one it accepts, and the
/// payload must pass the strict serializer and the type's validator. The payload then persisted is the serializer's
/// output, not the caller's text.
/// </summary>
public sealed class JobCommandValidator(IJobHandlerRegistry registry, IServiceProvider services)
{
    public JobPayloadCheck Check(string jobType, short payloadVersion, string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);

        if (
            !JobKeySyntax.IsValid(jobType)
            || !registry.TryGet(jobType, out JobHandlerRegistration? registration)
        )
        {
            return new JobPayloadCheck.UnsupportedType();
        }

        if (!registration.PayloadVersions.Contains(payloadVersion))
        {
            return new JobPayloadCheck.UnsupportedVersion();
        }

        return registration.CheckPayload(services, payloadJson);
    }
}
