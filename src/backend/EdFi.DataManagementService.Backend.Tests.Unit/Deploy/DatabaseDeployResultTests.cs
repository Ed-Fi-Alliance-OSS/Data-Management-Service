// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Deploy;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class DatabaseDeployResultTests
{
    [Test]
    public void DatabaseDeploySuccess_should_create_instance()
    {
        // Act
        var result = new DatabaseDeployResult.DatabaseDeploySuccess();

        // Assert
        Assert.That(result, Is.InstanceOf<DatabaseDeployResult>());
        Assert.That(result, Is.InstanceOf<DatabaseDeployResult.DatabaseDeploySuccess>());
    }

    [Test]
    public void DatabaseDeployFailure_should_Create_instance_with_exception()
    {
        // Arrange
        var exception = new Exception("Test exception");

        // Act
        var result = new DatabaseDeployResult.DatabaseDeployFailure(exception);

        // Assert
        Assert.That(result, Is.InstanceOf<DatabaseDeployResult>());
        Assert.That(result, Is.InstanceOf<DatabaseDeployResult.DatabaseDeployFailure>());
        Assert.That(result.Error, Is.EqualTo(exception));
    }
}
