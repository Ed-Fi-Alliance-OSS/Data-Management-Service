// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The verdict of <see cref="IDmsTargetEngine.ValidateEnvironmentAsync"/> (7.3 probes 4 to 7), with the server's
/// major version when the engine read it, so readiness can compare it with the templates' manifests
/// (<see cref="DmsEngineCompatibility"/>).
/// </summary>
public abstract record DmsEngineEnvironment
{
    /// <summary>The live server's major version, as <see cref="DmsServerFacts.MajorVersion"/> reports it; null when the server was not read.</summary>
    public int? ServerMajorVersion { get; init; }

    public sealed record Ready() : DmsEngineEnvironment;

    public sealed record NotReady : DmsEngineEnvironment
    {
        public NotReady(IReadOnlyList<DmsReadinessReason> reasons)
        {
            Reasons = DmsReadinessReason.RequireAtLeastOne(reasons);
        }

        public IReadOnlyList<DmsReadinessReason> Reasons { get; }
    }
}
