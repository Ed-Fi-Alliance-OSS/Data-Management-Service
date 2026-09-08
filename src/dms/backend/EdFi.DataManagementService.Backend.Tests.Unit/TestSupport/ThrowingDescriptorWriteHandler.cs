// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.TestSupport;

internal sealed class ThrowingDescriptorWriteHandler : IDescriptorWriteHandler
{
    public Task<UpsertResult> HandlePostAsync(
        DescriptorWriteRequest request,
        CancellationToken cancellationToken = default
    ) => throw new AssertionException("Descriptor POST was not expected.");

    public Task<UpdateResult> HandlePutAsync(
        DescriptorWriteRequest request,
        CancellationToken cancellationToken = default
    ) => throw new AssertionException("Descriptor PUT was not expected.");

    public Task<DeleteResult> HandleDeleteAsync(
        DescriptorDeleteRequest request,
        CancellationToken cancellationToken = default
    ) => throw new AssertionException("Descriptor DELETE was not expected.");
}
