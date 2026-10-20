param()
$ErrorActionPreference = 'Stop'
$rfRoot = $PSScriptRoot
$rfInstall = (Join-Path $rfRoot 'dist')
$rfFramework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$rfReferences = @('System.dll','System.Xml.dll','System.Runtime.Serialization.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll','System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $rfFramework $_) }
$rfOutput = Join-Path $rfRoot 'dist'
if (!(Test-Path -LiteralPath (Join-Path $rfInstall 'RFSWLib.dll'))) { throw 'Run tools/Collect-Dependencies.ps1 first. See README.md.' }
New-Item -ItemType Directory -Path $rfOutput -Force | Out-Null
& (Join-Path $rfFramework 'csc.exe') /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 "/out:$rfOutput\RealforceAccessible.exe" "/win32icon:$rfRoot\assets\assistant.ico" "/resource:$rfRoot\assets\assistant.ico,assistant.ico" "/resource:$rfRoot\resources\key-names.tsv,key-names.tsv" "/reference:$rfInstall\RFSWLib.dll" $rfReferences "$rfRoot\src\Program.cs" "$rfRoot\src\VersionInfo.cs" "$rfRoot\src\ConfigurationFile.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Get-ChildItem -LiteralPath (Join-Path $rfRoot 'docs') -Filter '*.txt' -File | Copy-Item -Destination $rfOutput -Force
