// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Installer;

public class Program
{
    public static void Main(string[] args)
    {
        switch (args[0])
        {
            case "postgresql":
                new Postgresql.Deploy.DatabaseDeploy().DeployDatabase(args[1]);
                break;
            case "mssql":
                new Mssql.Deploy.DatabaseDeploy().DeployDatabase(args[1]);
                break;
            default:
                throw new NotImplementedException();
        }
    }
}

