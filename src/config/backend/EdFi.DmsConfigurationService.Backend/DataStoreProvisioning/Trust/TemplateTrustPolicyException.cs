// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;

/// <summary>
/// A trust policy violates the delivered schema (F-P5), or a policy handed to the verifier holds a key that is
/// not an importable NIST P-256 key. Both are operator configuration defects, so they are raised rather than
/// turned into a verdict about any package; readiness (7.3 step 2) and the catalog turn them into
/// <c>NotReady</c>. The message names the failed rule and the offending entry by position only; it never echoes
/// a producer name, key id, key material, or any other value from the document.
/// </summary>
public sealed class TemplateTrustPolicyException(string message) : Exception(message);
