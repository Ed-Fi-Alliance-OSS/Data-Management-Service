// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// The job types added with <see cref="JobServiceCollectionExtensions.AddJobHandler{THandler, TPayload, TValidator}"/>
/// while the services were composed. <see cref="Add"/> rejects a type registered twice, so a duplicate fails startup.
/// </summary>
internal sealed class JobHandlerRegistrations
{
    private readonly Dictionary<string, JobHandlerRegistration> _byType = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, JobHandlerRegistration> ByType => _byType;

    public void Add(JobHandlerRegistration registration)
    {
        if (!_byType.TryAdd(registration.JobType, registration))
        {
            throw new InvalidOperationException(
                $"Job type '{registration.JobType}' is registered more than once; each job type has one handler."
            );
        }
    }
}

internal sealed class JobHandlerRegistry(JobHandlerRegistrations registrations) : IJobHandlerRegistry
{
    public IReadOnlyCollection<JobHandlerRegistration> Registrations => [.. registrations.ByType.Values];

    public bool TryGet(string jobType, [NotNullWhen(true)] out JobHandlerRegistration? registration) =>
        registrations.ByType.TryGetValue(jobType, out registration);
}
