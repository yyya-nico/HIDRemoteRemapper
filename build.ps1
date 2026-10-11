$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /out:HIDRemoteRemapper.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll Program.cs
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
