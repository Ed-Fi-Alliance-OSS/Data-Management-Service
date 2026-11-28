# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Only these fixed codes may cross from controller output into operator diagnostics.
function Test-CdcControllerFailureCode {
    param($Code)
    return $Code -is [string] -and $Code -cmatch '^(Request|WorkflowState|Projection|ProviderSetup|Kafka|Connect|Worker|Metrics|WriterPublication)/(InvalidInput|Unavailable|Timeout|AuthenticationFailed|Conflict|ValidationFailed|NativeDocumentCacheWorkerRequired|IncompatibleDocumentCachePhysicalSchema)\z'
}
