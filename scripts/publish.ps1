# Assembles a portable install. The result can be copied anywhere:
#   QuantumHub.Service.exe    the hub
#   gui\QuantumHub.Gui.exe    the admin console
#   plugins\<id>\             bundled plugins
#   dotnet\                   a private .NET: the machine needs nothing installed
#   samples\                  the external test client
#   packages\*.qhp            the same plugins as installable archives
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\QuantumHub'),
    [string]$Configuration = 'Release',
    [switch]$NoGui
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')

function Publish([string]$Project, [string]$Destination) {
    dotnet publish (Join-Path $repo $Project) -c $Configuration -o $Destination --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Publishing $Project failed." }
}

# The exe files of the hub and the GUI look for .NET in the dotnet folder next to the program. The launcher is
# only rebuilt when its inputs change, and this setting is not one of them, so the old one has to go first.
function PublishApp([string]$Project, [string]$Destination) {
    Get-ChildItem (Join-Path (Split-Path (Join-Path $repo $Project)) 'obj') -Recurse -Filter 'apphost.exe' -ErrorAction SilentlyContinue | Remove-Item -Force
    dotnet publish (Join-Path $repo $Project) -c $Configuration -o $Destination --nologo -v q -p:QhPrivateRuntime=true
    if ($LASTEXITCODE -ne 0) { throw "Publishing $Project failed." }
}

# Copies the runtime this machine has: the same files the .NET installer would put on a user's computer.
function Copy-Runtime([string]$Destination) {
    $dotnet = Split-Path (Get-Command dotnet).Source
    $versions = Get-ChildItem (Join-Path $dotnet 'shared\Microsoft.WindowsDesktop.App') -Directory |
        Where-Object { $_.Name -match '^10\.\d+\.\d+$' -and (Test-Path (Join-Path $dotnet "shared\Microsoft.NETCore.App\$($_.Name)")) -and
                       (Test-Path (Join-Path $dotnet "host\fxr\$($_.Name)")) }
    $version = ($versions | Sort-Object { [version]$_.Name } | Select-Object -Last 1).Name
    if (-not $version) { throw 'Need the .NET 10 and Windows Desktop runtimes of the same version.' }

    foreach ($part in "host\fxr\$version", "shared\Microsoft.NETCore.App\$version", "shared\Microsoft.WindowsDesktop.App\$version") {
        $target = Join-Path $Destination $part
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item (Join-Path $dotnet $part) $target -Recurse
    }
    foreach ($file in 'LICENSE.txt', 'ThirdPartyNotices.txt') { Copy-Item (Join-Path $dotnet $file) $Destination }
}

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Force $Output | Out-Null
$Output = (Resolve-Path $Output).Path

PublishApp 'src\QuantumHub.Service\QuantumHub.Service.csproj' $Output
Copy-Runtime (Join-Path $Output 'dotnet')
Publish 'samples\QuantumHub.Sample.ExternalConsole\QuantumHub.Sample.ExternalConsole.csproj' (Join-Path $Output 'samples')

$plugins = @{
    'qh.sample.uuid'         = 'plugins\QuantumHub.Plugin.UuidBackend\QuantumHub.Plugin.UuidBackend.csproj'
    'qh.system'      = 'plugins\QuantumHub.Plugin.SystemSpecs\QuantumHub.Plugin.SystemSpecs.csproj'
    'qh.activity' = 'plugins\QuantumHub.Plugin.Activity\QuantumHub.Plugin.Activity.csproj'
    'qh.audio'               = 'plugins\QuantumHub.Plugin.AudioDevices\QuantumHub.Plugin.AudioDevices.csproj'
    'qh.vr.switcher'         = 'plugins\QuantumHub.Plugin.VrSwitcher\QuantumHub.Plugin.VrSwitcher.csproj'
    'qh.vr.pico.antisleep'   = 'plugins\QuantumHub.Plugin.PicoAntiSleep\QuantumHub.Plugin.PicoAntiSleep.csproj'
    'qh.ui.notify'           = 'plugins\QuantumHub.Plugin.Notifications\QuantumHub.Plugin.Notifications.csproj'
    'qh.music'               = 'plugins\QuantumHub.Plugin.Music\QuantumHub.Plugin.Music.csproj'
    'qh.discord'             = 'plugins\QuantumHub.Plugin.Discord\QuantumHub.Plugin.Discord.csproj'
    'qh.vrchat.osc'          = 'plugins\QuantumHub.Plugin.VrChatOsc\QuantumHub.Plugin.VrChatOsc.csproj'
    'qh.vrchat.chatbox'      = 'plugins\QuantumHub.Plugin.Chatbox\QuantumHub.Plugin.Chatbox.csproj'
}
$companions = @{
    'qh.ui.notify'      = 'companions\QuantumHub.Notifier\QuantumHub.Notifier.csproj'
    'qh.vrchat.chatbox' = 'companions\QuantumHub.VrChatPanel\QuantumHub.VrChatPanel.csproj'
}
if (-not $NoGui) { PublishApp 'src\QuantumHub.Gui\QuantumHub.Gui.csproj' (Join-Path $Output 'gui') }

$packages = Join-Path $Output 'packages'
New-Item -ItemType Directory -Force $packages | Out-Null
foreach ($id in $plugins.Keys) {
    $destination = Join-Path $Output "plugins\$id"
    Publish $plugins[$id] $destination

    # The hub provides these, and a private copy would only cost disk space.
    Get-ChildItem (Join-Path $destination '*') -Include 'QuantumHub.Contracts.*', 'Google.Protobuf.*' -File | Remove-Item -Force

    # Внешняя часть плагина: у неё свои копии сборок, это отдельное приложение.
    if ($companions.ContainsKey($id)) { Publish $companions[$id] (Join-Path $destination 'companion') }

    if ($id -eq 'qh.activity' -and -not $NoGui) {
        # The GUI half: only its own DLL, since WinUI and the contracts come from the GUI.
        $ui = 'plugins\QuantumHub.Plugin.Activity.Ui\QuantumHub.Plugin.Activity.Ui.csproj'
        dotnet build (Join-Path $repo $ui) -c $Configuration --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "Building $ui failed." }
        Copy-Item (Get-ChildItem (Join-Path $repo 'plugins/QuantumHub.Plugin.Activity.Ui/bin') -Recurse -Filter 'QuantumHub.Plugin.Activity.Ui.dll' |
            Where-Object FullName -Like "*\$Configuration\*" | Select-Object -First 1).FullName $destination
    }

    # Anyone who gets a copy must also get the license and its Required Notice.
    Copy-Item (Join-Path $repo 'LICENSE.md'), (Join-Path $repo 'NOTICE') $destination

    Compress-Archive -Path (Join-Path $destination '*') -DestinationPath (Join-Path $packages "$id.zip") -Force
    Move-Item (Join-Path $packages "$id.zip") (Join-Path $packages "$id.qhp") -Force
}

foreach ($file in 'LICENSE.md', 'NOTICE', 'THIRD-PARTY-NOTICES.md') { Copy-Item (Join-Path $repo $file) $Output }

if (-not $NoGui) {
    # The GUI ships Windows App SDK files, so its terms and notices ship with them.
    $sdk = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windowsappsdk'
    $sdk = (Get-ChildItem $sdk | Sort-Object { [version]$_.Name } | Select-Object -Last 1).FullName
    $licenses = Join-Path $Output 'licenses'
    New-Item -ItemType Directory -Force $licenses | Out-Null
    Copy-Item (Join-Path $sdk 'license.txt') (Join-Path $licenses 'Microsoft.WindowsAppSDK.license.txt')
    Copy-Item (Join-Path $sdk 'NOTICE.txt') (Join-Path $licenses 'Microsoft.WindowsAppSDK.NOTICE.txt')
}

Write-Host "Published to $Output"
