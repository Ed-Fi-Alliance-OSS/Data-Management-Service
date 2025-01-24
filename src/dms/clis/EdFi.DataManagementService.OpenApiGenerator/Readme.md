# Execution Options:

### From Powershell:

1. On .\dms\clis\EdFi.DataManagementService.OpenApiGenerator  folder Run this command to generate and exe
 ```pwsh
    dotnet publish -c Release -r win-x64 --self-contained true -o ./publish
 ```
2. Go to publish folder `cd .\publish`
3. Core on scream (standard output):
```pwsh
.\EdFi.DataManagementService.OpenApiGenerator.exe C:\DMS\core.json
```
4. Core and Extension on scream (standard output):
```pwsh
.\EdFi.DataManagementService.OpenApiGenerator.exe C:\DMS\core.json C:\DMS\extension.json
```
5. Core and Generate a .json file
```pwsh
.\EdFi.DataManagementService.OpenApiGenerator.exe C:\DMS\core.json > C:\DMS\output.json
```
6. Core and Extension Generate a .json file
```pwsh
.\EdFi.DataManagementService.OpenApiGenerator.exe C:\DMS\core.json C:\DMS\extension.json > C:\DMS\output.json
```

### From VS Code:

1. Core on scream (standard output):
```pwsh
dotnet run C:\DMS\core.json
```
2. Core and Extension on scream (standard output):
```pwsh
dotnet run C:\DMS\core.json C:\DMS\extension.json
```
3. Core and Generate a .json file
```pwsh
dotnet run C:\DMS\core.json > C:\DMS\output.json
```
4. Core and Extension Generate a .json file
```pwsh
dotnet run C:\DMS\core.json C:\DMS\extension.json > C:\DMS\output.json
```
