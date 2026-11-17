# Builds Part Combiner into a single PartCombiner.exe in .\publish
#
#   .\build.ps1                       # standalone exe, runs on any 64-bit Windows 10/11 PC (~70 MB)
#   .\build.ps1 -FrameworkDependent   # small exe (~1 MB), needs the .NET 10 Desktop Runtime installed
#   .\build.ps1 -SkipTests
param(
    [switch]$FrameworkDependent,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $SkipTests) {
    dotnet test tests/PartCombiner.Tests -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$publishArgs = @(
    'publish', 'src/PartCombiner.App/PartCombiner.App.csproj',
    '-c', 'Release',
    '-o', 'publish',
    '--nologo',
    "-p:SelfContained=$selfContained",
    '-p:DebugType=none'
)
if (-not $FrameworkDependent) { $publishArgs += '-p:EnableCompressionInSingleFile=true' }

dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

$exe = Get-Item publish/PartCombiner.exe
"Built $($exe.FullName) ($([math]::Round($exe.Length / 1MB, 1)) MB)"

# The installer is the same program: named "...Setup.exe" it opens the setup window instead.
Copy-Item publish/PartCombiner.exe publish/PartCombinerSetup.exe -Force
"Built $((Get-Item publish/PartCombinerSetup.exe).FullName) (installer)"
