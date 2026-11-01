# Runs falcon-load for the Azure Pipelines template (falcon-load.yml). The parameters arrive as INPUT_* environment
# variables. The script exits 0 once falcon-load has run and hands its exit code to the later steps as a variable, so
# the results are published and the pull request comment is posted before the job fails.
$ErrorActionPreference = 'Stop'

$results = [IO.Path]::GetFullPath($env:INPUT_RESULTS, (Get-Location).Path)
New-Item -ItemType Directory -Force -Path $results | Out-Null
$summary = Join-Path $results 'summary.md'
$junit = Join-Path $results 'junit.xml'
Remove-Item -Force -ErrorAction SilentlyContinue $summary, $junit

# The trailing slashes make --out and --html directories with a file per test, however many tests the file holds.
$arguments = [Collections.Generic.List[string]]@(
    'run', $env:INPUT_FILE,
    '--out', "$results/json/",
    '--html', "$results/html/",
    '--junit', $junit,
    '--markdown', $summary)

function Add-Option([string] $name, [string] $value) {
    if ($value) { $arguments.Add($name); $arguments.Add($value) }
}

Add-Option '--test' $env:INPUT_TEST
Add-Option '--env-file' $env:INPUT_ENV_FILE
Add-Option '--env' $env:INPUT_ENV
Add-Option '--concurrency' $env:INPUT_CONCURRENCY
Add-Option '--duration' $env:INPUT_DURATION
Add-Option '--requests' $env:INPUT_REQUESTS
Add-Option '--max-error-rate' $env:INPUT_MAX_ERROR_RATE

# One name=value per line; blank lines and surrounding spaces are ignored.
foreach ($line in "$env:INPUT_VARS" -split '\r?\n') { Add-Option '--var' $line.Trim() }
foreach ($line in "$env:INPUT_DATA" -split '\r?\n') { Add-Option '--data' $line.Trim() }
foreach ($line in "$env:INPUT_ARGS" -split '\r?\n') {
    foreach ($word in $line.Trim() -split '\s+') { if ($word) { $arguments.Add($word) } }
}

& dotnet $env:FALCON_LOAD_TOOL @arguments
$code = $LASTEXITCODE

Write-Host "##vso[task.setvariable variable=FalconLoadExitCode]$code"
Write-Host "##vso[task.setvariable variable=FalconLoadPassed]$(($code -eq 0).ToString().ToLowerInvariant())"
Write-Host "##vso[task.setvariable variable=FalconLoadResults]$results"
if (Test-Path $junit) { Write-Host "##vso[task.setvariable variable=FalconLoadJunit]$junit" }
if (Test-Path $summary) {
    Write-Host "##vso[task.setvariable variable=FalconLoadSummary]$summary"
    Write-Host "##vso[task.uploadsummary]$summary"
}

exit 0
