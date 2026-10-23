// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.TestHelpers;

/// <summary>
/// Fixtures/connection-string-encryption-vectors.json: fixed values generated once, independently of
/// any reader, that every reader of CMS connection string cipher text (CMS, DMS, PowerShell) must
/// decrypt. A reader that agrees with itself but not with these has drifted from the format.
/// </summary>
public sealed record ConnectionStringEncryptionVectors(
    string KeyText,
    string PlainText,
    string EnvelopeV1,
    string LegacyCbc,
    string BlockAlignedPlainText,
    string BlockAlignedEnvelopeV1
)
{
    public static ConnectionStringEncryptionVectors Load() =>
        JsonSerializer.Deserialize<ConnectionStringEncryptionVectors>(
            File.ReadAllText(
                Path.Combine(
                    TestContext.CurrentContext.TestDirectory,
                    "Fixtures",
                    "connection-string-encryption-vectors.json"
                )
            ),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        )!;
}
