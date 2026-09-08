// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Startup;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

[TestFixture]
[Parallelizable]
public class ValidateResourceKeyRowReaderRegistrationTaskTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_The_Fallback_Reader_Is_Resolved : ValidateResourceKeyRowReaderRegistrationTaskTests
    {
        [Test]
        public async Task It_throws_a_configuration_error()
        {
            var task = new ValidateResourceKeyRowReaderRegistrationTask(
                new MissingResourceKeyRowReader(),
                NullLogger<ValidateResourceKeyRowReaderRegistrationTask>.Instance
            );

            Func<Task> act = async () => await task.ExecuteAsync(CancellationToken.None);

            var exception = await act.Should().ThrowAsync<InvalidOperationException>();
            exception.Which.Message.Should().Be(MissingResourceKeyRowReader.ConfigurationErrorMessage);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Concrete_Reader_Is_Resolved : ValidateResourceKeyRowReaderRegistrationTaskTests
    {
        [Test]
        public async Task It_completes_successfully()
        {
            var task = new ValidateResourceKeyRowReaderRegistrationTask(
                A.Fake<IResourceKeyRowReader>(),
                NullLogger<ValidateResourceKeyRowReaderRegistrationTask>.Instance
            );

            Func<Task> act = async () => await task.ExecuteAsync(CancellationToken.None);

            await act.Should().NotThrowAsync();
        }
    }
}
