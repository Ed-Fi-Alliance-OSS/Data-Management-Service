// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The template a managed DMS data store is created from. Only the two v3 wire values exist
/// (<c>Minimal</c> and <c>Sample</c>); they are matched ordinally and there is no other construction
/// path, so a request can never name an arbitrary package, kind, or artifact.
/// </summary>
public sealed class DmsDatabaseTemplate
{
    public const string MinimalWireValue = "Minimal";

    public const string SampleWireValue = "Sample";

    /// <summary>
    /// Wire value <c>Minimal</c>: restored from the allowlisted DMS Minimal package, whose restore
    /// manifest must declare <c>templateKind</c> <c>Minimal</c>.
    /// </summary>
    public static readonly DmsDatabaseTemplate Minimal = new(MinimalWireValue, "Minimal");

    /// <summary>
    /// Wire value <c>Sample</c>: restored from the allowlisted DMS Populated package, whose restore
    /// manifest must declare <c>templateKind</c> <c>Populated</c>.
    /// </summary>
    public static readonly DmsDatabaseTemplate Sample = new(SampleWireValue, "Populated");

    /// <summary>Every template, in wire-value order.</summary>
    public static IReadOnlyList<DmsDatabaseTemplate> All { get; } = [Minimal, Sample];

    private DmsDatabaseTemplate(string wireValue, string templateKind)
    {
        WireValue = wireValue;
        TemplateKind = templateKind;
    }

    /// <summary>The value DMS-1439 receives on the wire.</summary>
    public string WireValue { get; }

    /// <summary>The restore manifest <c>templateKind</c> the allowlisted package must declare.</summary>
    public string TemplateKind { get; }

    /// <summary>
    /// Resolves a wire value by ordinal comparison. Case variants, surrounding whitespace, the
    /// manifest kind <c>Populated</c>, and null are all rejected.
    /// </summary>
    public static bool TryFromWireValue(string? value, [NotNullWhen(true)] out DmsDatabaseTemplate? template)
    {
        template = All.FirstOrDefault(candidate =>
            string.Equals(candidate.WireValue, value, StringComparison.Ordinal)
        );
        return template is not null;
    }

    public override string ToString() => WireValue;
}
