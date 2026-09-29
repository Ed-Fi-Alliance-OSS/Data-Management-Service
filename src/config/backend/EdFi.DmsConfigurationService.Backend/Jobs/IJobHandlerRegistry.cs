// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>The registered job types (spec D-12, D-13). Lookup is ordinal and case-sensitive.</summary>
public interface IJobHandlerRegistry
{
    bool TryGet(string jobType, [NotNullWhen(true)] out JobHandlerRegistration? registration);

    IReadOnlyCollection<JobHandlerRegistration> Registrations { get; }
}
