// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// D-9: the live server's major version must reach the major of the manifest's
/// <c>engineVersion</c>, because neither engine restores a template produced by a newer one. One rule serves the
/// per-operation engine check, readiness, and target validation, so startup refuses exactly the deployments every
/// create would refuse, and a target is never judged by a different reading of the same manifest.
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
    /// The major of a manifest's <c>engineVersion</c> as the delivered restore core reads it
    /// (<c>Template-RestoreCore.psm1</c>, pattern <c>^\s*(\d+)</c>): the digits after optional leading white space,
    /// whatever follows them (<c>17.0.900.7</c> ⇒ 17, <c>18.1</c> ⇒ 18, <c>18beta1</c> ⇒ 18). False when no ASCII digit
    /// follows the white space (a sign, a letter, or nothing) or the digits overflow <see cref="int"/>; the delivered
    /// core cannot convert either to a major. The manifest reader requires only a non-blank string, so every caller
    /// treats false as a refusal.
    /// </summary>
    public static bool TryParseRequiredMajor(string engineVersion, out int major)
    {
        ArgumentNullException.ThrowIfNull(engineVersion);
        ReadOnlySpan<char> text = engineVersion.AsSpan().TrimStart();
        int length = 0;
        while (length < text.Length && char.IsAsciiDigit(text[length]))
        {
            length++;
        }

        if (length == 0)
        {
            major = 0;
            return false;
        }

        return int.TryParse(text[..length], NumberStyles.None, CultureInfo.InvariantCulture, out major);
    }

    /// <summary>
    /// Whether a server of <paramref name="serverMajor"/> can restore a template whose manifest names
    /// <paramref name="engineVersion"/>; never for a version whose major cannot be read.
    /// </summary>
    public static bool Accepts(int serverMajor, string engineVersion) =>
        TryParseRequiredMajor(engineVersion, out int major) && serverMajor >= major;

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
