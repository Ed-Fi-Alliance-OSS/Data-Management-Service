[CmdletBinding()]
param (
    [Parameter(Mandatory=$true)]
    [String]
    $Version,

    [Switch]
    $Publish
)

&dotnet build -c release -p:Version=$Version
&dotnet pack -c release -p:PackageVersion=$Version -o .

if ($Publish) {
    # Must have https://github.com/microsoft/artifacts-credprovider#azure-artifacts-credential-provider

    dotnet nuget push --source "EdFi" --api-key az "EdFi.ApiSchema.$($Version).nupkg" --interactive
}
