// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The claim the provisioner writes on a database it created (D-13): the PostgreSQL database comment
/// <c>edfi-dms-provision:v1:&lt;op&gt;:&lt;expected source identity&gt;</c>, stored in the cluster catalog
/// keyed by <c>oid</c> so it survives the rename into the target name. It is detection and operator
/// evidence (7.6), never destroy authority (D-14).
/// </summary>
public sealed record DmsProvisionClaim
{
    public const string Prefix = "edfi-dms-provision:v1:";

    public DmsProvisionClaim(DmsOperationId operation, Guid expectedSourceIdentity)
    {
        if (expectedSourceIdentity == Guid.Empty)
        {
            throw new ArgumentException(
                "The expected source identity must not be empty.",
                nameof(expectedSourceIdentity)
            );
        }

        Operation = operation;
        ExpectedSourceIdentity = expectedSourceIdentity;
    }

    public DmsOperationId Operation { get; }

    public Guid ExpectedSourceIdentity { get; }

    /// <summary>The exact comment text.</summary>
    public string Text => $"{Prefix}{Operation.Value}:{ExpectedSourceIdentity:D}";

    /// <summary>Parses a comment read back from the catalog; anything but the exact format is rejected.</summary>
    public static bool TryParse(string? text, out DmsProvisionClaim? claim)
    {
        claim = null;
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = text[Prefix.Length..].Split(':');
        if (
            parts.Length != 2
            || !DmsOperationId.TryParse(parts[0], out DmsOperationId operation)
            || !Guid.TryParseExact(parts[1], "D", out Guid expectedSourceIdentity)
            || expectedSourceIdentity == Guid.Empty
        )
        {
            return false;
        }

        claim = new DmsProvisionClaim(operation, expectedSourceIdentity);
        return true;
    }
}
