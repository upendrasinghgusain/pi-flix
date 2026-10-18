# Runs indexers.py on the Pi over SSH: nothing to deploy, it always runs the copy in this folder.
#   .\pi\arr\indexers.ps1 list
#   .\pi\arr\indexers.ps1 test "Internet Archive"
#   .\pi\arr\indexers.ps1 search "Internet Archive" "Nosferatu 1922"
# Run with no arguments for the full list of commands.
$ssh = 'C:\Program Files\Git\usr\bin\ssh.exe'
$script = Get-Content (Join-Path $PSScriptRoot 'indexers.py') -Raw
# Single-quote each argument for the Pi's shell.
$quoted = ($args | ForEach-Object { "'" + ($_ -replace "'", "'\''") + "'" }) -join ' '
$script | & $ssh -o BatchMode=yes -o HostKeyAlias=pieasypieces upendra@192.168.1.40 "python3 - $quoted"
