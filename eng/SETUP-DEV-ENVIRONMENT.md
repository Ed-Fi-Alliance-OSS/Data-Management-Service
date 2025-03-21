# Setting Up Development Environment

This repository includes a script to set up the development environment for new developers. The script ensures that all required .NET tools are restored and Husky is installed for managing Git hooks.


## Steps to Set Up

1. Open a terminal (e.g., PowerShell) in the root of the repository.
2. Navigate to the `eng` folder:
   ```powershell
   cd eng
3. Run the setup script:
   ```shell
   ./setup-dev-environment.ps1
   ```

What the Script Does
- Restores .NET tools specified in the dotnet-tools.json file.
- Installs CSharpier.
- Installs Husky to manage Git hooks (e.g., pre-commit hooks).

Notes:

Ensure you have the necessary permissions to execute PowerShell scripts. If you encounter an error, you may need to enable script execution by running:
```shell
Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned
```
