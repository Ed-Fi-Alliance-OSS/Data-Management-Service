# Setting Up Development Environment

This repository provides a script to set up the development environment for new developers. It ensures that all required .NET tools specified in the `.config/dotnet-tools.json` file are restored, along with the PowerShell resources specified in `eng/RequiredResources.psd1`.

## When to Run This Script

You should run the `setup-dev-environment.ps1` script:
1. **After cloning the repository**: To ensure all required tools and configurations are set up.
2. **Whenever the `.config/dotnet-tools.json` file or `eng/RequiredResources.psd1` is updated**: To restore any new tools or PowerShell resources added to the project.


## Steps to Set Up

1. Open a terminal (e.g., PowerShell) in the root of the repository.
2. Run the setup script:
   
   ```powershell
   ./setup-dev-environment.ps1
   ```

### What the Script Does?
- Restores .NET tools specified in the `.config/dotnet-tools.json` file.
- Restores PowerShell resources specified in `eng/RequiredResources.psd1`.
- Installs CSharpier.
- Installs Husky to manage Git hooks (e.g., pre-commit hooks).

>[!Note]
> Ensure you have the necessary permissions to execute PowerShell scripts. If you encounter an error, you may need to enable script execution by running:
  ```powershell
    Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned
  ```

### Why do we need Husky?
Husky is used to enforce consistent formatting of .cs files by running tools like CSharpier during Git pre-commit hooks. It also runs PSScriptAnalyzer against staged PowerShell files so committed scripts are checked before they enter the repository.
