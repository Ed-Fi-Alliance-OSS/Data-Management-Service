# Load credentials
$creds = Get-Content ./dataload-creds.json | ConvertFrom-Json

# Navigate to bulk load directory
Push-Location eng/bulkLoad

# Import modules
Import-Module ../Package-Management.psm1 -Force
Import-Module ./modules/Get-XSD.psm1 -Force
Import-Module ./modules/BulkLoad.psm1 -Force

# Initialize tools and set up paths
$paths = Initialize-ToolsAndDirectories
$paths.SampleDataDirectory = Resolve-Path '../.packages/southridge-xml-2023'

Write-Host 'Loading Southridge dataset (including descriptors)...'
Write-Southridge -BaseUrl 'http://localhost:8080' -Key $creds.key -Secret $creds.secret -Paths $paths

Pop-Location
