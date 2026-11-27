param(
    [string]$McpExecutable,
    [string]$CodexExecutable,
    [string]$CodexDirectory,
    [switch]$Check,
    [switch]$ProbeSolidWorks
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Find-CodexExecutable {
    $command = Get-Command codex.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    if (Get-Command Get-AppxPackage -ErrorAction SilentlyContinue) {
        $package = Get-AppxPackage -Name OpenAI.Codex | Sort-Object Version -Descending | Select-Object -First 1
        if ($package) {
            $bundled = Join-Path $package.InstallLocation 'app\resources\codex.exe'
            if (Test-Path -LiteralPath $bundled -PathType Leaf) { return $bundled }
        }
    }
    throw 'Install Codex desktop or the Codex CLI, or pass -CodexExecutable with the path to codex.exe.'
}

function Find-McpExecutable {
    $classId = '{b5ec0c01-12dd-4afa-88ee-42e1178ba63d}'
    foreach ($hive in @('HKEY_CURRENT_USER', 'HKEY_LOCAL_MACHINE')) {
        $key = 'Registry::' + $hive + '\Software\Classes\CLSID\' + $classId + '\InprocServer32'
        if (-not (Test-Path -LiteralPath $key)) { continue }
        $registration = (Get-ItemProperty -LiteralPath $key).PSObject.Properties['CodeBase']
        $location = $null
        if (-not $registration -or -not [Uri]::TryCreate($registration.Value, [UriKind]::Absolute, [ref]$location) -or -not $location.IsFile) {
            throw 'The SOLIDWORKS add-in registration has no local CodeBase. Run Install.cmd or pass -McpExecutable.'
        }
        return (Join-Path (Split-Path -Parent $location.LocalPath) 'SolidWorksAssetExporter.Mcp.exe')
    }
    throw 'The SOLIDWORKS add-in is not registered. Run Install.cmd or pass -McpExecutable for a custom installation.'
}

function Invoke-SetupProcess([string]$Executable, [string]$Arguments, [string]$Stage, [string]$Request) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Executable
    $info.Arguments = $Arguments
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    if ($Stage) {
        $info.WorkingDirectory = $Stage
        $info.EnvironmentVariables['CODEX_HOME'] = $Stage
    }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if ($Request) { $process.StandardInput.WriteLine($Request) }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill()
            throw "Timed out running $([IO.Path]::GetFileName($Executable))."
        }
        $null = $errors.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "$([IO.Path]::GetFileName($Executable)) $Arguments failed (exit $($process.ExitCode)). Check the executable and Codex configuration."
        }
        return $output.GetAwaiter().GetResult()
    }
    finally { $process.Dispose() }
}

function Update-ServerCommand([string]$Config, [string]$Executable, [bool]$AddTimeout) {
    $section = '(?m)^[ \t]*\[[ \t]*(?:mcp_servers|"mcp_servers"|''mcp_servers'')[ \t]*\.[ \t]*(?:solidworks-assets|"solidworks-assets"|''solidworks-assets'')[ \t]*\][ \t]*(?:#.*)?\r?\n(?<body>[\s\S]*?)(?=^[ \t]*\[|\z)'
    $selected = [regex]::Matches($Config, $section)
    if ($selected.Count -ne 1) { throw 'Use a [mcp_servers.solidworks-assets] table with a separate command entry so setup can preserve the other settings.' }
    $body = $selected[0].Groups['body']
    $assignment = '(?m)^[ \t]*(?:command|"command"|''command'')[ \t]*=[ \t]*(?<value>"(?:[^"\\\r\n]|\\.)*"|''[^''\r\n]*'')[ \t]*(?:#[^\r\n]*)?\r?$'
    $commands = [regex]::Matches($body.Value, $assignment)
    if ($commands.Count -ne 1) { throw 'The existing solidworks-assets command must be a single-line TOML string.' }
    $commandValue = $commands[0].Groups['value']
    $literal = '"' + $Executable.Replace('\', '\\') + '"'
    $updatedBody = $body.Value.Remove($commandValue.Index, $commandValue.Length).Insert($commandValue.Index, $literal)
    if ($AddTimeout) {
        $newline = if ($Config.Contains("`r`n")) { "`r`n" } else { "`n" }
        $updatedBody = 'tool_timeout_sec = 1800' + $newline + $updatedBody
    }
    return $Config.Remove($body.Index, $body.Length).Insert($body.Index, $updatedBody)
}

function Test-SolidWorksConnection([string]$Executable, $ServerTransport) {
    $serverArguments = @($ServerTransport.args)
    $argumentLine = ''
    if ($serverArguments.Count -ne 0) {
        if ($serverArguments.Count -ne 2 -or $serverArguments[0] -cne '--pid' -or $serverArguments[1] -notmatch '^[1-9][0-9]*$') {
            throw 'The configured MCP arguments must be empty or --pid followed by the SOLIDWORKS process ID.'
        }
        $argumentLine = '--pid ' + $serverArguments[1]
    }
    $request = '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"assembly_context","arguments":{}}}'
    $response = Invoke-SetupProcess $Executable $argumentLine '' $request | ConvertFrom-Json
    if (-not $response.PSObject.Properties['result']) { throw 'MCP did not return assembly_context. Start SOLIDWORKS and load a saved assembly.' }
    $content = @($response.result.content)
    if ($content.Count -eq 0 -or $response.result.isError) {
        $detail = if ($content.Count) { ' ' + $content[0].text } else { '' }
        throw ('MCP did not return assembly_context. Load the add-in and open a saved assembly in the same Windows session.' + $detail)
    }
    $context = $content[0].text | ConvertFrom-Json
    Write-Output "assembly_context: $($context.document_path) [$($context.configuration)]"
}

if (-not $CodexExecutable) { $CodexExecutable = Find-CodexExecutable }
if (-not $McpExecutable) { $McpExecutable = Find-McpExecutable }
foreach ($path in @($CodexExecutable, $McpExecutable)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Executable not found: $path" }
}
$CodexExecutable = (Resolve-Path -LiteralPath $CodexExecutable).Path
$McpExecutable = (Resolve-Path -LiteralPath $McpExecutable).Path
if (-not $CodexDirectory) {
    $configuredDirectory = [Environment]::GetEnvironmentVariable('CODEX_HOME')
    $CodexDirectory = if ($configuredDirectory) { $configuredDirectory } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex' }
}
$CodexDirectory = [IO.Path]::GetFullPath($CodexDirectory)
$configFile = Join-Path $CodexDirectory 'config.toml'
$skillSource = Join-Path $PSScriptRoot 'skills\solidworks-asset-exporter'
if (-not (Test-Path -LiteralPath $skillSource -PathType Container)) {
    $skillSource = Join-Path (Split-Path -Parent $PSScriptRoot) 'skills\solidworks-asset-exporter'
}
if (-not (Test-Path -LiteralPath (Join-Path $skillSource 'SKILL.md') -PathType Leaf)) { throw 'The setup package is missing the solidworks-asset-exporter skill.' }
$skillDestination = Join-Path $CodexDirectory 'skills\solidworks-asset-exporter'
$skillFiles = @(Get-ChildItem -LiteralPath $skillSource -File -Recurse)
$stage = Join-Path ([IO.Path]::GetTempPath()) ('solidworks-codex-setup-' + [Guid]::NewGuid().ToString('N'))
$textEncoding = New-Object Text.UTF8Encoding($false, $true)
$backup = Join-Path $CodexDirectory ('solidworks-asset-exporter-backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    $sourceHash = $null
    $stageFile = Join-Path $stage 'config.toml'
    if (Test-Path -LiteralPath $configFile -PathType Leaf) {
        $sourceHash = (Get-FileHash -LiteralPath $configFile).Hash
        Copy-Item -LiteralPath $configFile -Destination $stageFile
        if ((Get-FileHash -LiteralPath $stageFile).Hash -cne $sourceHash) { throw 'Codex configuration changed while reading it. Run setup again.' }
    }

    $servers = Invoke-SetupProcess $CodexExecutable 'mcp list --json' $stage '' | ConvertFrom-Json
    $existing = $servers | Where-Object name -CEQ 'solidworks-assets' | Select-Object -First 1
    if ($existing) { $existing = Invoke-SetupProcess $CodexExecutable 'mcp get solidworks-assets --json' $stage '' | ConvertFrom-Json }
    if ($existing -and $existing.transport.type -cne 'stdio') { throw 'solidworks-assets already uses a remote transport. Choose another MCP name before replacing it with this local add-in.' }
    if ($Check -and (-not $existing -or $existing.transport.command -ine $McpExecutable)) { throw 'Codex does not point to the installed MCP executable. Run Install-Codex.cmd first.' }

    if (-not $Check) {
        if (-not $existing) {
            $null = Invoke-SetupProcess $CodexExecutable ('mcp add solidworks-assets -- "' + $McpExecutable + '"') $stage ''
        }
        if (-not $existing -or $existing.transport.command -ine $McpExecutable) {
            $configText = [IO.File]::ReadAllText($stageFile, $textEncoding)
            $updatedText = Update-ServerCommand $configText $McpExecutable (-not $existing)
            [IO.File]::WriteAllText($stageFile, $updatedText, $textEncoding)
        }
    }
    $current = Invoke-SetupProcess $CodexExecutable 'mcp get solidworks-assets --json' $stage '' | ConvertFrom-Json
    if ($current.transport.type -cne 'stdio' -or $current.transport.command -ine $McpExecutable) { throw 'Codex could not verify the local MCP command.' }
    if ($existing) {
        $expected = $existing | ConvertTo-Json -Depth 25 | ConvertFrom-Json
        $expected.transport.command = $current.transport.command
        if (($expected | ConvertTo-Json -Depth 25 -Compress) -cne ($current | ConvertTo-Json -Depth 25 -Compress)) {
            throw 'Codex returned different server settings beyond those setup intended to update.'
        }
    } elseif (-not $Check -and $current.tool_timeout_sec -ne 1800) { throw 'Codex did not accept the new tool timeout.' }

    if (-not $Check -and (Get-FileHash -LiteralPath $stageFile).Hash -cne $sourceHash) {
        $currentHash = if (Test-Path -LiteralPath $configFile -PathType Leaf) { (Get-FileHash -LiteralPath $configFile).Hash } else { $null }
        if ($sourceHash -cne $currentHash) { throw 'Codex configuration changed during setup. Run setup again.' }
        New-Item -ItemType Directory -Path $CodexDirectory -Force | Out-Null
        $newConfig = Join-Path $CodexDirectory ('.solidworks-config-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        Copy-Item -LiteralPath $stageFile -Destination $newConfig
        if ($sourceHash) {
            New-Item -ItemType Directory -Path $backup -Force | Out-Null
            [IO.File]::Replace($newConfig, $configFile, (Join-Path $backup 'config.toml'))
        } else { [IO.File]::Move($newConfig, $configFile) }
    }

    foreach ($file in $skillFiles) {
        $relative = $file.FullName.Substring($skillSource.Length).TrimStart('\', '/')
        $target = Join-Path $skillDestination $relative
        $hasTarget = Test-Path -LiteralPath $target -PathType Leaf
        if ($hasTarget -and (Get-FileHash -LiteralPath $file.FullName).Hash -ceq (Get-FileHash -LiteralPath $target).Hash) { continue }
        if ($Check) { throw "The installed skill differs from this package: $relative. Run Install-Codex.cmd first." }
        if ($hasTarget) {
            $backupFile = Join-Path (Join-Path $backup 'skill') $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $backupFile) -Force | Out-Null
            Copy-Item -LiteralPath $target -Destination $backupFile
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
    if (-not $current.enabled) { Write-Warning 'The existing MCP server is disabled in Codex. Enable solidworks-assets before using it.' }
    Write-Output "Codex MCP: solidworks-assets -> $McpExecutable"
    Write-Output "Codex skill: $skillDestination"
    if (Test-Path -LiteralPath $backup -PathType Container) { Write-Output "Setup backup: $backup" }
    if ($ProbeSolidWorks) { Test-SolidWorksConnection $McpExecutable $current.transport }
    elseif (-not $Check) { Write-Output 'Restart Codex, open a saved assembly in SOLIDWORKS, and ask Codex to run solidworks-assets assembly_context.' }
}
finally {
    # Delete only the unique workspace created by this invocation under the OS temp directory.
    $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + '\'
    $cleanupTarget = [IO.Path]::GetFullPath($stage)
    if ($cleanupTarget.StartsWith($tempBase + 'solidworks-codex-setup-', [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $cleanupTarget -Recurse -Force
    }
}
