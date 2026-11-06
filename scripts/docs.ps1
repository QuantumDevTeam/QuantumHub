# Генерирует сайт документации в artifacts/docs.
#   -Serve   показать его на http://localhost:8080
param([switch]$Serve)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')

Push-Location $repo
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Could not restore DocFX.' }

    # Сборка нужна и ради кода, который генерирует protobuf.
    dotnet build QuantumHub.sln -c Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }

    $arguments = @('docfx', 'docs/docfx.json')
    if ($Serve) { $arguments += '--serve' }
    dotnet tool run @arguments
    if ($LASTEXITCODE -ne 0) { throw 'DocFX failed.' }
}
finally {
    Pop-Location
}
