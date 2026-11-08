# Собирает релиз. Результат в artifacts\release:
#   QuantumHub-Setup-<версия>.exe   установщик: всё внутри, .NET и права администратора не нужны
#   plugins\*.qhp                   те же плагины отдельными файлами
#   sdk\*.nupkg                     SDK для авторов плагинов
#   SHA256SUMS.txt                  контрольные суммы
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\release'),
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$published = Join-Path $repo 'artifacts\QuantumHub'
$work = Join-Path $repo 'artifacts\release-work'

if (-not $SkipTests) {
    dotnet test (Join-Path $repo 'tests\QuantumHub.Tests\QuantumHub.Tests.csproj') -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

& (Join-Path $PSScriptRoot 'publish.ps1') -Output $published

foreach ($dir in $Output, $work) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Force $dir | Out-Null
}
$Output = (Resolve-Path $Output).Path

# Что поедет в установщик: всё, кроме примеров, готовых пакетов и файлов для разработчиков.
$stage = Join-Path $work 'stage'
New-Item -ItemType Directory -Force $stage | Out-Null
Get-ChildItem $published | Where-Object Name -NotIn 'samples', 'packages' | Copy-Item -Destination $stage -Recurse
Get-ChildItem $stage -Recurse -File -Include '*.pdb', 'QuantumHub.*.xml', 'Google.Protobuf.xml' | Remove-Item -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = Join-Path $work 'payload.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)

# Сам установщик собран одним файлом вместе с рантаймом, потому что запускается там, где .NET ещё нет.
$setupDir = Join-Path $work 'setup'
dotnet publish (Join-Path $repo 'src\QuantumHub.Setup\QuantumHub.Setup.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $setupDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publishing the setup failed.' }

[xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props')
$version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
$setup = Join-Path $Output "QuantumHub-Setup-$version.exe"
Copy-Item (Join-Path $setupDir 'QuantumHub.Setup.exe') $setup

# Хвост файла: архив, его длина и метка. Формат описан в src\QuantumHub.Setup\Payload.cs.
$stream = [IO.File]::Open($setup, 'Append', 'Write')
try {
    $input = [IO.File]::OpenRead($archive)
    try {
        $input.CopyTo($stream)
        $length = [BitConverter]::GetBytes([long]$input.Length)
        $stream.Write($length, 0, $length.Length)
    }
    finally { $input.Dispose() }
    $magic = [Text.Encoding]::ASCII.GetBytes('QHSETUP1')
    $stream.Write($magic, 0, $magic.Length)
}
finally { $stream.Dispose() }

$packages = Join-Path $Output 'plugins'
New-Item -ItemType Directory -Force $packages | Out-Null
Copy-Item (Join-Path $published 'packages\*.qhp') $packages

$sdk = Join-Path $Output 'sdk'
foreach ($project in 'QuantumHub.Contracts', 'QuantumHub.ProtoMemProtocol', 'QuantumHub.Client') {
    dotnet pack (Join-Path $repo "src\$project\$project.csproj") -c Release -o $sdk --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Packing $project failed." }
}

Get-ChildItem $Output -Recurse -File | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.FullName.Substring($Output.Length + 1))"
} | Set-Content (Join-Path $Output 'SHA256SUMS.txt') -Encoding ascii

Remove-Item $work -Recurse -Force
Write-Host "Release: $setup ($([math]::Round((Get-Item $setup).Length / 1MB)) MB)"
