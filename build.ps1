# Gera dist\Hertzo.exe — arquivo único, autocontido (não precisa instalar .NET no PC de destino).
#   .\build.ps1            -> autocontido (~70 MB, roda em qualquer Windows 10/11 x64)
#   .\build.ps1 -Small     -> depende do .NET 10 Desktop Runtime instalado (~2 MB)
param([switch]$Small)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'Hertzo\Hertzo.csproj'
$out  = Join-Path $root 'dist'

if (-not (Test-Path (Join-Path $root 'Hertzo\Assets\hertzo.ico'))) { & (Join-Path $root 'tools\make-icon.ps1') }

$common = @('-c', 'Release', '-r', 'win-x64', '-o', $out, '-p:PublishSingleFile=true', '-p:DebugType=none', '-nologo')
if ($Small) {
    dotnet publish $proj @common --self-contained false
} else {
    dotnet publish $proj @common --self-contained true '-p:EnableCompressionInSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true'
}
if ($LASTEXITCODE -ne 0) { throw "Falha no build" }

$exe = Join-Path $out 'Hertzo.exe'
"{0} ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB)
