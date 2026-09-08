// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

public interface IConnectionStringDecryptionService
{
    /// <summary>
    /// Decrypts a Base64-encoded AES-encrypted connection string produced by the CMS.
    /// Returns null when input is null or empty.
    /// </summary>
    string? DecryptFromBase64(string? base64EncodedCipherText);
}
