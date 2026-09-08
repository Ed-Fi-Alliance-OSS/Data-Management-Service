// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using FakeItEasy;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

/// <summary>
/// Builds an <see cref="IApplicationContextProvider"/> stub that returns the same
/// stable <see cref="ApplicationContext"/> for every lookup, including forced reloads.
/// The context references the single stable DMS instance id used by the rest of the
/// external-doubles stack.
/// </summary>
internal static class FakeApplicationContextProvider
{
    public static IApplicationContextProvider Stable()
    {
        var fake = A.Fake<IApplicationContextProvider>();
        var context = new ApplicationContext(
            ExternalDoublesConstants.StableApplicationContextId,
            ExternalDoublesConstants.StableApplicationId,
            ExternalDoublesConstants.SmokeClientId,
            ExternalDoublesConstants.StableClientUuid,
            [ExternalDoublesConstants.StableDataStoreId]
        );

        A.CallTo(() => fake.GetApplicationByClientIdAsync(A<string>._))
            .Returns(Task.FromResult<ApplicationContext?>(context));
        A.CallTo(() => fake.ReloadApplicationByClientIdAsync(A<string>._))
            .Returns(Task.FromResult<ApplicationContext?>(context));

        return fake;
    }
}
