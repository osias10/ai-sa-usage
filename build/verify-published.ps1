param([string]$PublishDirectory, [switch]$Light)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $repoRoot '.artifacts\publish' }
$executable = Join-Path $PublishDirectory 'AIUsageWidget.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Run build/build.ps1 first.' }
# This tests the self-contained application without a developer PATH or .NET installation.
# --smoke-test uses synthetic data and never connects an account or writes user settings.
$start = New-Object System.Diagnostics.ProcessStartInfo
$start.FileName = $executable
$start.Arguments = if ($Light) { '--smoke-test --light' } else { '--smoke-test' }
$start.WorkingDirectory = $PublishDirectory
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
$start.EnvironmentVariables['PATH'] = Join-Path $env:SystemRoot 'System32'
foreach ($variable in @('DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_HOST_PATH','CODEX_HOME','CLAUDE_CONFIG_DIR','ANTHROPIC_API_KEY','OPENAI_API_KEY','GEMINI_API_KEY')) {
    $start.EnvironmentVariables.Remove($variable)
}
$testProcess = [System.Diagnostics.Process]::Start($start)
if (-not $testProcess.WaitForExit(20000)) { $testProcess.Kill(); throw 'Published UI smoke test timed out.' }
if ($testProcess.ExitCode -ne 0) { throw "Published UI smoke test failed: $($testProcess.ExitCode)." }
Write-Output 'PASS: self-contained UI starts and exits with developer tools absent from PATH.'
