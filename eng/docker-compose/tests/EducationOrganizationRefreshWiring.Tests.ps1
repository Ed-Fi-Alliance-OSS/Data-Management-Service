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
