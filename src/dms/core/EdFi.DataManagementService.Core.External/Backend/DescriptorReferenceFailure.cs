// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Backend;

public enum DescriptorReferenceFailureReason
{
    Missing,
    DescriptorTypeMismatch,
}

public sealed record DescriptorReferenceFailure(
    JsonPath Path,
    BaseResourceInfo TargetResource,
    DocumentIdentity DocumentIdentity,
    ReferentialId ReferentialId,
    DescriptorReferenceFailureReason Reason
)
{
    public static DescriptorReferenceFailure From(
        DescriptorReference descriptorReference,
        DescriptorReferenceFailureReason reason
    ) =>
        new(
            Path: descriptorReference.Path,
            TargetResource: descriptorReference.ResourceInfo,
            DocumentIdentity: descriptorReference.DocumentIdentity,
            ReferentialId: descriptorReference.ReferentialId,
            Reason: reason
        );
}
