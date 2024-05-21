Example for running DB install for PostgreSQL from Linux, command line from the `src` directory:

```
dotnet run -r linux-x64 --project backend/EdFi.DataManagementService.Backend.Installer/EdFi.DataManagementService.Backend.Installer.csproj -- postgresql 'host=localhost;port=5432;username=postgres;password=abcdefgh1!;database=EdFi.DataManagementService'
```
