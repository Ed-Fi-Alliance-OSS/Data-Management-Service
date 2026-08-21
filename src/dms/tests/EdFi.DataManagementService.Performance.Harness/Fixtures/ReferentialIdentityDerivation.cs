// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;

namespace EdFi.DataManagementService.Performance.Harness.Fixtures;

/// <summary>
/// Independent RFC-4122 version-5 computation of the referential identity the generated
/// TF_TR_Student_ReferentialIdentity trigger derives in the database. The smoke proof gate
/// validates this formula against a control row written by the production POST path before
/// applying it to loader rows, so a drift in either implementation fails loudly.
/// </summary>
public static class ReferentialIdentityDerivation
{
    /// <summary>
    /// The fixed namespace uuid the generated dms.uuidv5 trigger calls use.
    /// </summary>
    public static readonly Guid EdFiNamespace = Guid.Parse("edf1edf1-3df1-3df1-3df1-3df1edf1edf1");

    public static Guid StudentReferentialId(string studentUniqueId) =>
        Uuidv5(
            EdFiNamespace,
            PerfFixtureDefinition.ProjectName
                + PerfFixtureDefinition.ResourceName
                + "$.studentUniqueId="
                + studentUniqueId
        );

    /// <summary>
    /// The referential identity of a descriptor document: the production write path keys
    /// descriptors by the $.descriptor identity path with the URI lowercased, which is how
    /// descriptor references are resolved during reference validation.
    /// </summary>
    public static Guid DescriptorReferentialId(string descriptorResourceName, string uri) =>
        Uuidv5(
            EdFiNamespace,
            PerfFixtureDefinition.ProjectName
                + descriptorResourceName
                + "$.descriptor="
                + uri.ToLowerInvariant()
        );

    /// <summary>
    /// RFC-4122 version 5: SHA-1 over the big-endian namespace bytes followed by the UTF-8
    /// name, truncated to 16 bytes with the version and variant bits stamped. SHA-1 is the
    /// algorithm the RFC defines for v5; nothing here is a security boundary.
    /// </summary>
    public static Guid Uuidv5(Guid namespaceId, string name)
    {
        byte[] namespaceBytes = namespaceId.ToByteArray(bigEndian: true);
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] hash = SHA1.HashData([.. namespaceBytes, .. nameBytes]);
        byte[] result = hash[..16];
        result[6] = (byte)((result[6] & 0x0F) | 0x50);
        result[8] = (byte)((result[8] & 0x3F) | 0x80);
        return new Guid(result, bigEndian: true);
    }
}
