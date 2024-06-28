# Local Bulk Load of partial Grand Bend

1. Ensure `AppSettings.AuthenticationService` in the frontend `appsettings.json` file is set to
   `http://localhost:5198/oauth/token`
1. Start DMS on http://localhost:5198/
1. Run `.\Invoke-LoadPartialGrandBend.ps1 -BaseUrl 'http://localhost:5198/'`
