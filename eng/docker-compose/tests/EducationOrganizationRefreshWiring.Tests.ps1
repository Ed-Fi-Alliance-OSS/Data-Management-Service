# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Client-side Compose rendering only. No daemon, containers or HTTP requests are used here.
#
# The compose half of the configuration-preservation matrix for the Configuration Service's job
# workers and DMS education organization reader (docs/CONFIGURATION.md, "Bootstrap and operational
# modes"). An instance with workers on refuses to start without reader settings, so the rendered
# worker switch decides whether an upgrade starts, fails loudly, or runs in bootstrap. No upgrade
# case may render workers off unless the variable is explicitly false; only fresh-install templates
# and test environments set it. The startup half runs the same rendered values through the host
# (RefreshReaderStartupMatrixTests in the Configuration Service frontend unit tests).
Describe 'Configuration Service worker and reader wiring' {
    BeforeAll {
        $script:composeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
        $script:azureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' '..' 'azure-vm' 'compose'))
        $script:readerVariables = @(
            'CONFIGURATION_SERVICE_DMS_EDORG_BASE_URL',
            'CONFIGURATION_SERVICE_DMS_EDORG_CLIENT_ID',
            'CONFIGURATION_SERVICE_DMS_EDORG_CLIENT_SECRET'
        )
        $script:upgradeVariables = @('CONFIGURATION_SERVICE_JOBS_WORKER_ENABLED') + $script:readerVariables

        function Get-EnvironmentLines {
            param([string] $Root, [string] $Name)
            return @(Get-Content -LiteralPath (Join-Path $Root $Name))
        }

        function Remove-Declaration {
            param([string[]] $Lines, [string] $Key)
            return @($Lines | Where-Object { $_ -notmatch "^\s*(export\s+)?$([regex]::Escape($Key))=" })
        }

        function Set-Declaration {
            param([string[]] $Lines, [string] $Key, [string] $Value)
            return @(Remove-Declaration -Lines $Lines -Key $Key) + "$Key=$Value"
        }

        # An existing deployment's .env, written before this release: the fresh-install template
        # without the variables this release added to it.
        function Get-UpgradeLines {
            $lines = Get-EnvironmentLines -Root $script:composeRoot -Name '.env.example'
            foreach ($variable in $script:upgradeVariables) {
                $lines = Remove-Declaration -Lines $lines -Key $variable
            }
            return $lines
        }

        function Invoke-Render {
            param([string] $Root, [string[]] $Files, [string[]] $Lines)

            $environmentFile = Join-Path $TestDrive 'render.env'
            [IO.File]::WriteAllText($environmentFile, (($Lines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))

            # Compose prioritizes the process environment, so an inherited value could decide a case.
            # Clear every name the rendered files or the env file can read, and restore each one
            # afterwards, even when rendering fails.
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($file in $Files) {
                $text = Get-Content -Raw -LiteralPath (Join-Path $Root $file)
                foreach ($match in [regex]::Matches($text, '\$\{([A-Za-z_][A-Za-z0-9_]*)')) {
                    [void]$names.Add($match.Groups[1].Value)
                }
            }
            foreach ($line in $Lines) {
                if ($line -match '^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)=') { [void]$names.Add($Matches[1]) }
            }
            foreach ($name in 'COMPOSE_PROJECT_NAME', 'COMPOSE_FILE', 'COMPOSE_PROFILES', 'COMPOSE_ENV_FILES') {
                [void]$names.Add($name)
            }

            $saved = @{}
            foreach ($name in $names) {
                $saved[$name] = [Environment]::GetEnvironmentVariable($name)
                Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
            }

            $errorFile = Join-Path $TestDrive 'render.stderr'
            $arguments = @('compose', '-p', 'edorgrefreshrender', '--project-directory', $Root)
            foreach ($file in $Files) { $arguments += @('-f', $file) }
            $arguments += @('--env-file', $environmentFile, 'config', '--format', 'json')

            Push-Location $Root
            try {
                $rendered = & docker @arguments 2> $errorFile
                $exitCode = $LASTEXITCODE
            }
            finally {
                Pop-Location
                foreach ($name in $names) {
                    if ($null -eq $saved[$name]) {
                        Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
                    }
                    else {
                        [Environment]::SetEnvironmentVariable($name, $saved[$name])
                    }
                }
            }

            # Rendered output can hold secrets, so only the exit code is reported on failure.
            $exitCode | Should -Be 0 -Because 'the compose files must render'
            return ($rendered | Out-String) | ConvertFrom-Json -AsHashtable
        }

        function Get-ConfigEnvironment {
            param([string] $Files, [string[]] $Lines)
            $config = Invoke-Render -Root $script:composeRoot -Files @('postgresql.compose.yml', $Files) -Lines $Lines
            return $config.services.config.environment
        }

        # `docker compose config` writes each literal '$' of a resolved value as '$$', so its output can be read
        # back as a Compose file; the container receives the single '$'. Test literals never contain '$$', so this
        # also holds for a Compose release that prints the resolved value unescaped.
        function ConvertFrom-ComposeConfigValue {
            param([string] $Value)
            return $Value.Replace('$$', '$')
        }

        # A synthetic secret drawn from the Configuration Service generator's alphabet, with the dollar patterns
        # Compose would interpolate: references to a variable the same .env defines, a positional '$1' and a
        # trailing '$'.
        $script:generatedSecret = 'Aa1!@#%^&*()-_=+[]{}:;,.?$CONFIGURATION_SERVICE_DMS_EDORG_CLIENT_ID${CONFIGURATION_SERVICE_DMS_EDORG_CLIENT_ID}$1$'

        $script:validReader = @{
            CONFIGURATION_SERVICE_DMS_EDORG_BASE_URL = 'http://dms-local:8080'
            CONFIGURATION_SERVICE_DMS_EDORG_CLIENT_ID = 'projection-client'
            CONFIGURATION_SERVICE_DMS_EDORG_CLIENT_SECRET = 'projection-secret'
        }
    }

    Context '<_>' -ForEach @('local-config.compose.yml', 'published-config.compose.yml') {
        BeforeAll { $script:files = $_ }

        It 'maps each reader variable onto its setting with an empty default' {
            $environment = Get-ConfigEnvironment -Files $script:files -Lines (Get-UpgradeLines)
            $environment['DmsEducationOrganizationProjectionSettings__DmsBaseUrl'] | Should -BeExactly ''
            $environment['DmsEducationOrganizationProjectionSettings__Credentials__ClientId'] | Should -BeExactly ''
            $environment['DmsEducationOrganizationProjectionSettings__Credentials__ClientSecret'] | Should -BeExactly ''

            $lines = Get-UpgradeLines
            foreach ($pair in $script:validReader.GetEnumerator()) {
                $lines = Set-Declaration -Lines $lines -Key $pair.Key -Value $pair.Value
            }
            $environment = Get-ConfigEnvironment -Files $script:files -Lines $lines
            $environment['DmsEducationOrganizationProjectionSettings__DmsBaseUrl'] | Should -BeExactly 'http://dms-local:8080'
            $environment['DmsEducationOrganizationProjectionSettings__Credentials__ClientId'] | Should -BeExactly 'projection-client'
            $environment['DmsEducationOrganizationProjectionSettings__Credentials__ClientSecret'] | Should -BeExactly 'projection-secret'
        }

        It 'renders management <Management>, worker variable <Worker>, reader <Reader> as worker <Rendered>' -TestCases @(
            @{ Management = 'true'; Worker = 'absent'; Reader = 'missing'; Rendered = 'true'; RenderedManagement = 'true' }
            @{ Management = 'true'; Worker = 'absent'; Reader = 'valid'; Rendered = 'true'; RenderedManagement = 'true' }
            @{ Management = 'true'; Worker = 'true'; Reader = 'missing'; Rendered = 'true'; RenderedManagement = 'true' }
            @{ Management = 'true'; Worker = 'false'; Reader = 'missing'; Rendered = 'false'; RenderedManagement = 'true' }
            @{ Management = 'absent'; Worker = 'absent'; Reader = 'missing'; Rendered = 'true'; RenderedManagement = 'false' }
        ) {
            param($Management, $Worker, $Reader, $Rendered, $RenderedManagement)
            $lines = Get-UpgradeLines
            if ($Management -ne 'absent') {
                $lines = Set-Declaration -Lines $lines -Key 'CONFIGURATION_SERVICE_DATA_STORE_MANAGEMENT_ENABLED' -Value $Management
            }
            if ($Worker -ne 'absent') {
                $lines = Set-Declaration -Lines $lines -Key 'CONFIGURATION_SERVICE_JOBS_WORKER_ENABLED' -Value $Worker
            }
            if ($Reader -eq 'valid') {
                foreach ($pair in $script:validReader.GetEnumerator()) {
                    $lines = Set-Declaration -Lines $lines -Key $pair.Key -Value $pair.Value
                }
            }

            $environment = Get-ConfigEnvironment -Files $script:files -Lines $lines

            $environment['JobSettings__WorkerEnabled'] | Should -BeExactly $Rendered
            $environment['AppSettings__EnableDataStoreManagement'] | Should -BeExactly $RenderedManagement
            $expectedBaseUrl = if ($Reader -eq 'valid') { 'http://dms-local:8080' } else { '' }
            $environment['DmsEducationOrganizationProjectionSettings__DmsBaseUrl'] | Should -BeExactly $expectedBaseUrl
        }

        It 'renders a fresh copy of .env.example in bootstrap, with workers off and no reader' {
            $environment = Get-ConfigEnvironment -Files $script:files -Lines (Get-EnvironmentLines -Root $script:composeRoot -Name '.env.example')
            $environment['JobSettings__WorkerEnabled'] | Should -BeExactly 'false'
            $environment['AppSettings__EnableDataStoreManagement'] | Should -BeExactly 'false'
            $environment['DmsEducationOrganizationProjectionSettings__DmsBaseUrl'] | Should -BeExactly ''
        }

        It 'passes a single-quoted generated secret literally, where an unquoted one is interpolated' {
            $lines = Get-UpgradeLines
            foreach ($name in $script:validReader.Keys) {
                $lines = Set-Declaration -Lines $lines -Key $name -Value $script:validReader[$name]
            }
            $secretKey = 'DmsEducationOrganizationProjectionSettings__Credentials__ClientSecret'

            $quoted = Set-Declaration -Lines $lines -Key 'CONFIGURATION_SERVICE_DMS_EDORG_CLIENT_SECRET' -Value "'$script:generatedSecret'"
            $environment = Get-ConfigEnvironment -Files $script:files -Lines $quoted
            ConvertFrom-ComposeConfigValue $environment[$secretKey] | Should -BeExactly $script:generatedSecret
            $environment['DmsEducationOrganizationProjectionSettings__Credentials__ClientId'] | Should -BeExactly 'projection-client'

            $unquoted = Set-Declaration -Lines $lines -Key 'CONFIGURATION_SERVICE_DMS_EDORG_CLIENT_SECRET' -Value 'Ab1!$CONFIGURATION_SERVICE_DMS_EDORG_CLIENT_ID-x'
            $environment = Get-ConfigEnvironment -Files $script:files -Lines $unquoted
            ConvertFrom-ComposeConfigValue $environment[$secretKey] | Should -BeExactly 'Ab1!projection-client-x'
        }
    }

    It 'keeps the unset worker default true in <_>' -ForEach @('local-config.compose.yml', 'published-config.compose.yml') {
        Get-Content -Raw -LiteralPath (Join-Path $script:composeRoot $_) |
            Should -Match '(?m)^\s*JobSettings__WorkerEnabled:\s*\$\{CONFIGURATION_SERVICE_JOBS_WORKER_ENABLED:-true\}\r?$'
    }

    # Every tracked environment file that composes the Configuration Service, and the reason it
    # runs no workers. The Instance end-to-end environment is listed here until it is wired as
    # operational; a file that becomes operational moves out of this list.
    It 'renders <Name> in explicit bootstrap' -TestCases @(
        @{ Name = '.env.multitenancy' }
        @{ Name = '.env.e2e' }
        @{ Name = '.env.routeContext.e2e' }
        @{ Name = '.env.cursorpartitions.e2e' }
        @{ Name = '.env.smoke' }
        @{ Name = '.env.smoke.ds61' }
        @{ Name = '.env.template' }
        @{ Name = '.env.template.ds61' }
        @{ Name = '.env.config.e2e' }
        @{ Name = '.env.config.mssql.e2e' }
        @{ Name = '.env.config.mssql.multitenant.e2e' }
    ) {
        param($Name)
        $lines = Get-EnvironmentLines -Root $script:composeRoot -Name $Name
        @($lines | Where-Object { $_ -match '^CONFIGURATION_SERVICE_JOBS_WORKER_ENABLED=' }) |
            Should -Be @('CONFIGURATION_SERVICE_JOBS_WORKER_ENABLED=false')
        $environment = Get-ConfigEnvironment -Files 'local-config.compose.yml' -Lines $lines
        $environment['JobSettings__WorkerEnabled'] | Should -BeExactly 'false'
    }

    It 'lists every tracked environment file that sets the Configuration Service identity provider' {
        # A new full environment file must take a side: explicit bootstrap above, or operational.
        $listed = @(
            '.env.example', '.env.multitenancy', '.env.e2e', '.env.routeContext.e2e', '.env.cursorpartitions.e2e',
            '.env.smoke', '.env.smoke.ds61', '.env.template', '.env.template.ds61', '.env.config.e2e',
            '.env.config.mssql.e2e', '.env.config.mssql.multitenant.e2e'
        )
        $full = @(
            Get-ChildItem -LiteralPath $script:composeRoot -Force -File -Filter '.env*' |
                Where-Object { Select-String -LiteralPath $_.FullName -Pattern '^CONFIGURATION_SERVICE_IDENTITY_PROVIDER=' -Quiet } |
                ForEach-Object Name
        )
        $tracked = @(git -C $script:composeRoot ls-files -- '.env*' | ForEach-Object { Split-Path -Leaf $_ })
        @($full | Where-Object { $tracked -contains $_ } | Sort-Object) | Should -Be @($listed | Sort-Object)
    }

    Context 'eng/azure-vm/compose/docker-compose.yml' {
        BeforeAll {
            function Get-AzureEnvironment {
                param([string[]] $Lines, [string] $Service)
                $config = Invoke-Render -Root $script:azureRoot -Files @('docker-compose.yml') -Lines $Lines
                return $config.services[$Service].environment
            }

            # An existing deployment's .env, written before this release.
            function Get-AzureUpgradeLines {
                $lines = Get-EnvironmentLines -Root $script:azureRoot -Name '.env.example'
                foreach ($variable in @(
                        'CONFIGURATION_SERVICE_JOBS_WORKER_ENABLED',
                        'CONFIGURATION_SERVICE_ST_DMS_EDORG_BASE_URL', 'CONFIGURATION_SERVICE_ST_DMS_EDORG_CLIENT_ID',
                        'CONFIGURATION_SERVICE_ST_DMS_EDORG_CLIENT_SECRET', 'CONFIGURATION_SERVICE_MT_DMS_EDORG_BASE_URL',
                        'CONFIGURATION_SERVICE_MT_DMS_EDORG_CLIENT_ID', 'CONFIGURATION_SERVICE_MT_DMS_EDORG_CLIENT_SECRET'
                    )) {
                    $lines = Remove-Declaration -Lines $lines -Key $variable
                }
                return $lines
            }
        }

        It 'keeps workers on for an existing <_> deployment whose .env lacks the variable' -ForEach @('st-config', 'mt-config') {
            (Get-AzureEnvironment -Lines (Get-AzureUpgradeLines) -Service $_)['JobSettings__WorkerEnabled'] |
                Should -BeExactly 'true'
        }

        It 'renders a fresh copy of .env.example in bootstrap for <_>' -ForEach @('st-config', 'mt-config') {
            $lines = Get-EnvironmentLines -Root $script:azureRoot -Name '.env.example'
            (Get-AzureEnvironment -Lines $lines -Service $_)['JobSettings__WorkerEnabled'] | Should -BeExactly 'false'
        }

        It 'maps each stack''s own reader variables for <Service>' -TestCases @(
            @{ Service = 'st-config'; Prefix = 'CONFIGURATION_SERVICE_ST_DMS_EDORG' }
            @{ Service = 'mt-config'; Prefix = 'CONFIGURATION_SERVICE_MT_DMS_EDORG' }
        ) {
            param($Service, $Prefix)
            $lines = Get-AzureUpgradeLines
            $lines = Set-Declaration -Lines $lines -Key "$($Prefix)_BASE_URL" -Value "http://$Service-dms-root"
            $lines = Set-Declaration -Lines $lines -Key "$($Prefix)_CLIENT_ID" -Value "$Service-client"
            $lines = Set-Declaration -Lines $lines -Key "$($Prefix)_CLIENT_SECRET" -Value "$Service-secret"
            $environment = Get-AzureEnvironment -Lines $lines -Service $Service
            $environment['DmsEducationOrganizationProjectionSettings__DmsBaseUrl'] | Should -BeExactly "http://$Service-dms-root"
            $environment['DmsEducationOrganizationProjectionSettings__Credentials__ClientId'] | Should -BeExactly "$Service-client"
            $environment['DmsEducationOrganizationProjectionSettings__Credentials__ClientSecret'] | Should -BeExactly "$Service-secret"
        }

        Context 'reader credential override' {
            BeforeAll {
                # Renders the Compose files that compose-files.sh selects, as up.sh, update.sh and reset.sh pass
                # them, from a copy of the deployment folder, so a test override never lands in the repository.
                # GuardExitCode is what refuse_reader_override_placeholders returns there, which those scripts run first.
                function Get-AzureSelectionRender {
                    param([string] $Override, [string[]] $Lines)
                    $root = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
                    New-Item -ItemType Directory -Path $root | Out-Null
                    foreach ($file in 'docker-compose.yml', 'keycloak.compose.yml', 'compose-files.sh') {
                        Copy-Item -LiteralPath (Join-Path $script:azureRoot $file) -Destination $root
                    }
                    if ($Override) {
                        [IO.File]::WriteAllText((Join-Path $root 'reader.compose.override.yml'), $Override, [Text.UTF8Encoding]::new($false))
                    }
                    $selection = @(& bash -c 'cd "$1" && source ./compose-files.sh && printf "%s\n" "${compose_files[@]}"' bash $root)
                    $LASTEXITCODE | Should -Be 0
                    $files = @($selection | Where-Object { $_ -ne '-f' })
                    $null = & bash -c 'cd "$1" && source ./compose-files.sh && refuse_reader_override_placeholders 2>/dev/null' bash $root
                    $guardExitCode = $LASTEXITCODE
                    return @{ Files = $files; Config = Invoke-Render -Root $root -Files $files -Lines $Lines; GuardExitCode = $guardExitCode }
                }

                $lines = Get-EnvironmentLines -Root $script:azureRoot -Name '.env.example'
                $lines = Set-Declaration -Lines $lines -Key 'CONFIGURATION_SERVICE_JOBS_WORKER_ENABLED' -Value 'true'
                $lines = Set-Declaration -Lines $lines -Key 'CONFIGURATION_SERVICE_ST_DMS_EDORG_BASE_URL' -Value 'http://st-dms:8080/st-dms'
                $script:azureLines = Set-Declaration -Lines $lines -Key 'CONFIGURATION_SERVICE_MT_DMS_EDORG_BASE_URL' -Value 'http://mt-dms:8080/mt-dms'
                $script:credentials = 'DmsEducationOrganizationProjectionSettings__Credentials'
                $script:tenantCredentials = 'DmsEducationOrganizationProjectionSettings__TenantCredentials'
            }

            It 'renders the single-tenant credential and two distinct tenant credentials literally from the override' {
                # Synthetic credentials; every literal '$' is written '$$', as the example file says. The control
                # value shows that YAML quoting alone does not stop interpolation.
                $override = @'
services:
  st-config:
    environment:
      DmsEducationOrganizationProjectionSettings__Credentials__ClientId: "st-reader"
      DmsEducationOrganizationProjectionSettings__Credentials__ClientSecret: "St1!$$NAME-$${NAME}@#"
      EscapingControl: "Ab$MT_TENANT_1"
  mt-config:
    environment:
      DmsEducationOrganizationProjectionSettings__TenantCredentials__tenant1__ClientId: "tenant1-reader"
      DmsEducationOrganizationProjectionSettings__TenantCredentials__tenant1__ClientSecret: "Tenant1!$$1$${NAME}"
      DmsEducationOrganizationProjectionSettings__TenantCredentials__tenant2__ClientId: "tenant2-reader"
      DmsEducationOrganizationProjectionSettings__TenantCredentials__tenant2__ClientSecret: "Tenant2![]{}:;,.?$$"
'@
                $render = Get-AzureSelectionRender -Override $override -Lines $script:azureLines

                $render.Files | Should -Be @('docker-compose.yml', 'keycloak.compose.yml', 'reader.compose.override.yml')
                $st = $render.Config.services['st-config'].environment
                $st["$($script:credentials)__ClientId"] | Should -BeExactly 'st-reader'
                ConvertFrom-ComposeConfigValue $st["$($script:credentials)__ClientSecret"] | Should -BeExactly 'St1!$NAME-${NAME}@#'
                $st['DmsEducationOrganizationProjectionSettings__DmsBaseUrl'] | Should -BeExactly 'http://st-dms:8080/st-dms'
                $st['JobSettings__WorkerEnabled'] | Should -BeExactly 'true'
                $st['EscapingControl'] | Should -BeExactly 'Abtenant1'
                @($st.Keys | Where-Object { $_ -like "$($script:tenantCredentials)__*" }) | Should -BeNullOrEmpty

                $mt = $render.Config.services['mt-config'].environment
                $mt["$($script:tenantCredentials)__tenant1__ClientId"] | Should -BeExactly 'tenant1-reader'
                ConvertFrom-ComposeConfigValue $mt["$($script:tenantCredentials)__tenant1__ClientSecret"] | Should -BeExactly 'Tenant1!$1${NAME}'
                $mt["$($script:tenantCredentials)__tenant2__ClientId"] | Should -BeExactly 'tenant2-reader'
                ConvertFrom-ComposeConfigValue $mt["$($script:tenantCredentials)__tenant2__ClientSecret"] | Should -BeExactly 'Tenant2![]{}:;,.?$'
                $mt['DmsEducationOrganizationProjectionSettings__DmsBaseUrl'] | Should -BeExactly 'http://mt-dms:8080/mt-dms'
                $mt["$($script:credentials)__ClientId"] | Should -BeExactly ''
                $mt["$($script:credentials)__ClientSecret"] | Should -BeExactly ''
            }

            It 'selects only the two stack files and keeps the .env credentials when no override exists' {
                $lines = Set-Declaration -Lines $script:azureLines -Key 'CONFIGURATION_SERVICE_ST_DMS_EDORG_CLIENT_ID' -Value 'st-env-reader'
                $lines = Set-Declaration -Lines $lines -Key 'CONFIGURATION_SERVICE_ST_DMS_EDORG_CLIENT_SECRET' -Value 'St-env-secret1!'

                $render = Get-AzureSelectionRender -Lines $lines

                $render.Files | Should -Be @('docker-compose.yml', 'keycloak.compose.yml')
                $render.GuardExitCode | Should -Be 0
                $st = $render.Config.services['st-config'].environment
                $st["$($script:credentials)__ClientId"] | Should -BeExactly 'st-env-reader'
                $st["$($script:credentials)__ClientSecret"] | Should -BeExactly 'St-env-secret1!'
                @($render.Config.services['mt-config'].environment.Keys | Where-Object { $_ -like "$($script:tenantCredentials)__*" }) |
                    Should -BeNullOrEmpty
            }

            It 'keeps the .env single-tenant credential under a tenant-only override, which the placeholder guard accepts' {
                $lines = Set-Declaration -Lines $script:azureLines -Key 'CONFIGURATION_SERVICE_ST_DMS_EDORG_CLIENT_ID' -Value 'st-env-reader'
                $lines = Set-Declaration -Lines $lines -Key 'CONFIGURATION_SERVICE_ST_DMS_EDORG_CLIENT_SECRET' -Value 'St-env-secret1!'
                # Synthetic tenant credentials; the example's st-config block (CHANGEME_st_reader_key) was deleted.
                $override = @'
# The single-tenant credential stays in .env, so the st-config block with its CHANGEME_ values was deleted.
services:
#  st-config:
#    environment:
#      DmsEducationOrganizationProjectionSettings__Credentials__ClientId: "CHANGEME_st_reader_key"
  mt-config:
    environment:
      DmsEducationOrganizationProjectionSettings__TenantCredentials__tenant1__ClientId: "tenant1-reader"
      DmsEducationOrganizationProjectionSettings__TenantCredentials__tenant1__ClientSecret: "Tenant1-secret1!"
      DmsEducationOrganizationProjectionSettings__TenantCredentials__tenant2__ClientId: "tenant2-reader"
      DmsEducationOrganizationProjectionSettings__TenantCredentials__tenant2__ClientSecret: "Tenant2-secret1!"
'@
                $render = Get-AzureSelectionRender -Override $override -Lines $lines

                $render.GuardExitCode | Should -Be 0
                $render.Files | Should -Be @('docker-compose.yml', 'keycloak.compose.yml', 'reader.compose.override.yml')
                $st = $render.Config.services['st-config'].environment
                $st["$($script:credentials)__ClientId"] | Should -BeExactly 'st-env-reader'
                $st["$($script:credentials)__ClientSecret"] | Should -BeExactly 'St-env-secret1!'
                @($st.Keys | Where-Object { $_ -like "$($script:tenantCredentials)__*" }) | Should -BeNullOrEmpty
                $mt = $render.Config.services['mt-config'].environment
                $mt["$($script:tenantCredentials)__tenant1__ClientId"] | Should -BeExactly 'tenant1-reader'
                $mt["$($script:tenantCredentials)__tenant1__ClientSecret"] | Should -BeExactly 'Tenant1-secret1!'
                $mt["$($script:tenantCredentials)__tenant2__ClientId"] | Should -BeExactly 'tenant2-reader'
                $mt["$($script:tenantCredentials)__tenant2__ClientSecret"] | Should -BeExactly 'Tenant2-secret1!'
            }

            It 'maps the tracked example onto the single-tenant credential and each .env.example tenant' {
                $example = Get-Content -Raw -LiteralPath (Join-Path $script:azureRoot 'reader.compose.override.example.yml')

                $render = Get-AzureSelectionRender -Override $example -Lines $script:azureLines

                # The lifecycle scripts refuse the untouched copy before any Compose call; rendering it directly still
                # shows where each entry lands.
                $render.GuardExitCode | Should -Be 1
                $st = $render.Config.services['st-config'].environment
                $st.Keys | Should -Contain "$($script:credentials)__ClientId"
                $st.Keys | Should -Contain "$($script:credentials)__ClientSecret"
                $mt = $render.Config.services['mt-config'].environment
                foreach ($tenantKey in 'MT_TENANT_1', 'MT_TENANT_2') {
                    $tenant = (@($script:azureLines | Where-Object { $_ -like "$tenantKey=*" })[-1] -split '=', 2)[1]
                    $mt.Keys | Should -Contain "$($script:tenantCredentials)__$($tenant)__ClientId"
                    $mt.Keys | Should -Contain "$($script:tenantCredentials)__$($tenant)__ClientSecret"
                }
            }
        }
    }
}

# The operational Configuration Service that build-dms.ps1 InstanceE2ETest recreates after provisioning the readers.
# Client-side only: the plan is rendered with docker compose config, never started.
Describe 'Instance E2E operational Configuration Service' {
    BeforeAll {
        function Get-BuildScriptFunctionText {
            param([Parameter(Mandatory)] [string] $FunctionName)
            $parseErrors = $null
            $tokens = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:buildScript, [ref]$tokens, [ref]$parseErrors)
            $functionAst = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName }, $true) | Select-Object -First 1
            if ($null -eq $functionAst) { throw "Function '$FunctionName' was not found in '$script:buildScript'." }
            return $functionAst.Extent.Text
        }

        $script:buildScript = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '..', '..', '..', 'build-dms.ps1'))
        $script:composeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
        foreach ($name in 'Get-InstanceE2EOperationalConfigServicePlan', 'Assert-ConfigServiceEnvironmentChange') {
            . ([scriptblock]::Create((Get-BuildScriptFunctionText -FunctionName $name)))
        }

        $script:readers = @(
            [pscustomobject]@{ TenantName = 'Tenant_255901'; ClientId = 'reader-key-1'; ClientSecret = 'reader-secret-1' },
            [pscustomobject]@{ TenantName = 'Tenant_255902'; ClientId = 'reader-key-2'; ClientSecret = 'reader-secret-2' }
        )
        $script:plan = Get-InstanceE2EOperationalConfigServicePlan -Readers $script:readers -DmsBaseUrl 'http://ed-fi-api:8080/'
    }

    It 'plans workers, scheduler, reader base URL, one-minute interval and each tenant''s credential' {
        $script:plan.ExpectedEnvironment['JobSettings__WorkerEnabled'] | Should -BeExactly 'true'
        $script:plan.ExpectedEnvironment['JobSettings__SchedulerEnabled'] | Should -BeExactly 'true'
        $script:plan.ExpectedEnvironment['DmsEducationOrganizationProjectionSettings__DmsBaseUrl'] | Should -BeExactly 'http://ed-fi-api:8080/'
        $script:plan.ExpectedEnvironment['AppSettings__EdOrgsRefreshIntervalInMins'] | Should -BeExactly '1'
        $script:plan.ExpectedEnvironment['DmsEducationOrganizationProjectionSettings__TenantCredentials__Tenant_255901__ClientId'] | Should -BeExactly 'reader-key-1'
        $script:plan.ExpectedEnvironment['DmsEducationOrganizationProjectionSettings__TenantCredentials__Tenant_255902__ClientSecret'] | Should -BeExactly 'reader-secret-2'
        $script:plan.ExpectedEnvironment.Count | Should -Be 8
    }

    It 'writes no credential into the override file' {
        $script:plan.OverrideYaml | Should -Not -Match 'reader-key|reader-secret'
        $script:plan.OverrideYaml | Should -Match ([regex]::Escape('${INSTANCE_E2E_EDORG_TENANT_2_CLIENT_SECRET:?'))
    }

    It 'refuses a tenant name that cannot form a configuration key' {
        { Get-InstanceE2EOperationalConfigServicePlan -Readers @([pscustomobject]@{ TenantName = 'Tenant:1'; ClientId = 'k'; ClientSecret = 's' }) -DmsBaseUrl 'http://dms/' } |
            Should -Throw '*letters, digits and underscores*'
    }

    It 'renders every planned setting over the bootstrap route-context environment, through the shipped worker and reader variables' {
        $overrideFile = Join-Path $TestDrive 'operational.yml'
        [IO.File]::WriteAllText($overrideFile, $script:plan.OverrideYaml, [Text.UTF8Encoding]::new($false))

        # Compose prefers the process environment over --env-file, which is what lets the plan turn workers on over
        # the environment file's bootstrap value. Restore every variable afterwards.
        $saved = @{}
        foreach ($name in $script:plan.ProcessVariables.Keys) {
            $saved[$name] = [Environment]::GetEnvironmentVariable($name)
            [Environment]::SetEnvironmentVariable($name, [string]$script:plan.ProcessVariables[$name])
        }
        $arguments = @(
            'compose', '-p', 'edorgrefreshoperational', '--project-directory', $script:composeRoot,
            '-f', (Join-Path $script:composeRoot 'postgresql.compose.yml'),
            '-f', (Join-Path $script:composeRoot 'local-config.compose.yml'),
            '-f', $overrideFile,
            '--env-file', (Join-Path $script:composeRoot '.env.routeContext.e2e'),
            'config', '--format', 'json'
        )
        try {
            $rendered = & docker @arguments 2> (Join-Path $TestDrive 'operational.stderr')
            $exitCode = $LASTEXITCODE
        }
        finally {
            foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
        }

        # Rendered output holds secrets, so only the exit code and setting names are reported.
        $exitCode | Should -Be 0 -Because 'the compose files and the override must render'
        $environment = (($rendered | Out-String) | ConvertFrom-Json -AsHashtable).services.config.environment
        $mismatched = @($script:plan.ExpectedEnvironment.Keys | Where-Object { [string]$environment[$_] -cne [string]$script:plan.ExpectedEnvironment[$_] })
        $mismatched | Should -BeNullOrEmpty
    }

    Context 'the environment-change guard' {
        BeforeAll {
            $script:before = @{ PATH = '/usr/bin'; JobSettings__WorkerEnabled = 'false'; DatabaseSettings__DatabaseConnection = 'db-secret' }
            $script:expected = @{ JobSettings__WorkerEnabled = 'true'; AppSettings__EdOrgsRefreshIntervalInMins = '1' }
        }

        It 'accepts a container that changed exactly the planned settings' {
            $after = @{ PATH = '/usr/bin'; JobSettings__WorkerEnabled = 'true'; AppSettings__EdOrgsRefreshIntervalInMins = '1'; DatabaseSettings__DatabaseConnection = 'db-secret' }
            { Assert-ConfigServiceEnvironmentChange -Before $script:before -After $after -Expected $script:expected } | Should -Not -Throw
        }

        It 'compares the generic dictionaries the container inspection returns against the plan''s ordered map' {
            $before = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)
            $before['PATH'] = '/usr/bin'
            $before['JobSettings__WorkerEnabled'] = 'false'
            $after = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)
            $after['PATH'] = '/usr/bin'
            $after['JobSettings__WorkerEnabled'] = 'true'
            $after['path'] = 'added'
            $expected = [ordered]@{ JobSettings__WorkerEnabled = 'true' }

            { Assert-ConfigServiceEnvironmentChange -Before $before -After $after -Expected $expected } |
                Should -Throw '*changed unexpectedly: path.'
            $after.Remove('path') | Out-Null
            { Assert-ConfigServiceEnvironmentChange -Before $before -After $after -Expected $expected } | Should -Not -Throw
        }

        It 'names an unplanned change, an unplanned removal and a missed setting, without any value' {
            $after = @{ PATH = '/usr/local/bin'; JobSettings__WorkerEnabled = 'false'; AppSettings__EdOrgsRefreshIntervalInMins = '1' }
            $failure = { Assert-ConfigServiceEnvironmentChange -Before $script:before -After $after -Expected $script:expected } |
                Should -Throw -PassThru
            $message = $failure.Exception.Message
            $message | Should -Match 'not set as expected: JobSettings__WorkerEnabled'
            $message | Should -Match 'changed unexpectedly: DatabaseSettings__DatabaseConnection, PATH'
            $message | Should -Not -Match 'db-secret|/usr'
        }
    }
}
