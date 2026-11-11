// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// D-9: the live server's major version must reach the major of the manifest's
/// <c>engineVersion</c>, because neither engine restores a template produced by a newer one. One rule serves the
/// per-operation engine check and readiness, so startup refuses exactly the deployments every create
/// would refuse.
/// </summary>
public static class DmsEngineCompatibility
{
    /// <summary>The readiness probe name for one template: <c>EngineCompatibility.&lt;wire value&gt;</c>.</summary>
    public static string ProbeFor(DmsDatabaseTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return "EngineCompatibility." + template.WireValue;
    }

    /// <summary>The fixed check name under the probe: the live server is older than the manifest requires.</summary>
    public const string ServerBelowManifestCheck = "ServerBelowManifest";

    /// <summary>
    /// The leading integer of <paramref name="engineVersion"/> (<c>17.0.900.7</c> ⇒ 17, <c>18.1</c> ⇒ 18). An
    /// unparsable major is <see cref="int.MaxValue"/>, which no server reaches: the manifest reader accepts only
    /// well-formed versions, so this refuses rather than guesses.
    /// </summary>
    public static int RequiredMajorOf(string engineVersion)
    {
        ArgumentNullException.ThrowIfNull(engineVersion);
        ReadOnlySpan<char> version = engineVersion;
        int dot = version.IndexOf('.');
        if (dot >= 0)
        {
            version = version[..dot];
        }

        return int.TryParse(version, out int major) ? major : int.MaxValue;
    }

    /// <summary>Whether a server of <paramref name="serverMajor"/> can restore a template whose manifest names <paramref name="engineVersion"/>.</summary>
    public static bool Accepts(int serverMajor, string engineVersion) =>
        serverMajor >= RequiredMajorOf(engineVersion);

    /// <summary>
    /// Readiness: one <c>EngineIncompatible</c> reason per authenticated template whose manifest requires a newer
    /// engine than <paramref name="serverMajor"/>, in the order given.
    /// </summary>
    public static IReadOnlyList<DmsReadinessReason> Evaluate(
        int serverMajor,
        IReadOnlyList<TemplateEngineRequirement> requirements
    )
    {
        ArgumentNullException.ThrowIfNull(requirements);
        return
        [
            .. requirements
                .Where(requirement => !Accepts(serverMajor, requirement.EngineVersion))
                .Select(requirement =>
                {
                    string probe = ProbeFor(requirement.Template);
                    return new DmsReadinessReason(
                        DmsProvisioningFailureCode.EngineIncompatible,
                        probe,
                        DmsProvisioningDiagnostic.ForOperation(probe + "." + ServerBelowManifestCheck)
                    );
                }),
        ];
    }
}
