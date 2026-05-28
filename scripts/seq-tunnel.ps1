<#
.SYNOPSIS
    Open an SSH local-port-forward to the Lightsail box's Seq instance and launch the UI.

.DESCRIPTION
    Seq runs as a Windows service on the Lightsail host, listening on
    http://localhost:5341 (loopback only — not exposed publicly). This script
    forwards a local port through SSH to that listener and opens a browser tab
    pointed at it. See docs/deploy.md#seq for the rationale (SSH tunnel vs.
    public reverse proxy) and docs/logging.md#seq for query recipes.

    The tunnel runs in the background (ssh -fN). Stop it with -Stop or by
    killing the printed PID.

.PARAMETER SshHost
    The Lightsail host — IP or DNS name. Required unless an SSH config alias
    is passed via -SshAlias.

.PARAMETER SshAlias
    Name of an entry in ~/.ssh/config to use instead of -SshHost / -User /
    -IdentityFile. Recommended for routine use:

        Host gh-sync-seq
            HostName <lightsail-public-ip>
            User administrator
            IdentityFile ~/.ssh/lightsail.pem
            LocalForward 5341 localhost:5341

    Then: scripts/seq-tunnel.ps1 -SshAlias gh-sync-seq

.PARAMETER User
    SSH user on the box. Defaults to "administrator".

.PARAMETER IdentityFile
    Optional path to the private key.

.PARAMETER LocalPort
    Port to open on this workstation. Defaults to 5341. Override if 5341 is
    already taken locally (e.g. another Seq instance for a different project).

.PARAMETER RemotePort
    Port on the Lightsail box that Seq listens on. Defaults to 5341. Only
    change if Seq has been reconfigured on the host.

.PARAMETER NoBrowser
    Skip opening the browser. Useful when scripting or when the tunnel is for
    something other than the UI (e.g. a CLI hitting the Seq ingestion port).

.PARAMETER Stop
    Tear down a tunnel previously started by this script. Reads the PID file
    written under $env:LOCALAPPDATA\github-sync\seq-tunnel.pid.

.EXAMPLE
    scripts/seq-tunnel.ps1 -SshAlias gh-sync-seq
    # Opens the tunnel and browses to http://localhost:5341.

.EXAMPLE
    scripts/seq-tunnel.ps1 -SshHost 1.2.3.4 -IdentityFile ~/.ssh/lightsail.pem
    # Same, but without an SSH config alias.

.EXAMPLE
    scripts/seq-tunnel.ps1 -Stop
    # Kills the background ssh process.
#>
[CmdletBinding(DefaultParameterSetName = 'Open')]
param(
    [Parameter(ParameterSetName = 'Open')]
    [string]$SshHost,

    [Parameter(ParameterSetName = 'Open')]
    [string]$SshAlias,

    [Parameter(ParameterSetName = 'Open')]
    [string]$User = 'administrator',

    [Parameter(ParameterSetName = 'Open')]
    [string]$IdentityFile,

    [Parameter(ParameterSetName = 'Open')]
    [int]$LocalPort = 5341,

    [Parameter(ParameterSetName = 'Open')]
    [int]$RemotePort = 5341,

    [Parameter(ParameterSetName = 'Open')]
    [switch]$NoBrowser,

    [Parameter(ParameterSetName = 'Stop', Mandatory = $true)]
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'

$pidDir = Join-Path $env:LOCALAPPDATA 'github-sync'
$pidFile = Join-Path $pidDir 'seq-tunnel.pid'

function Stop-Tunnel {
    if (-not (Test-Path $pidFile)) {
        Write-Host "No tunnel PID file at $pidFile — nothing to stop."
        return
    }
    $tunnelPid = Get-Content $pidFile | Select-Object -First 1
    # Guard against PID reuse: only kill if the process still looks like ssh.
    # Otherwise a recycled PID could belong to an editor / shell / anything.
    $proc = Get-Process -Id $tunnelPid -ErrorAction SilentlyContinue
    if (-not $proc) {
        Write-Host "PID $tunnelPid is no longer running — clearing PID file."
    } elseif ($proc.ProcessName -ne 'ssh') {
        Write-Warning "PID $tunnelPid is '$($proc.ProcessName)', not ssh — refusing to kill. Clearing stale PID file."
    } else {
        try {
            Stop-Process -Id $tunnelPid -Force -ErrorAction Stop
            Write-Host "Stopped tunnel (PID $tunnelPid)."
        } catch {
            Write-Warning "Could not stop PID $tunnelPid ($_)."
        }
    }
    Remove-Item $pidFile -Force
}

if ($Stop) {
    Stop-Tunnel
    return
}

if (-not $SshAlias -and -not $SshHost) {
    throw "Specify either -SshAlias (preferred — see help) or -SshHost."
}

$sshArgs = @('-f', '-N', '-L', "${LocalPort}:localhost:${RemotePort}")
if ($IdentityFile) { $sshArgs += @('-i', $IdentityFile) }

$target = if ($SshAlias) { $SshAlias } else { "$User@$SshHost" }
$sshArgs += $target

# Refuse to double-open. If the local port is already taken (by an earlier
# tunnel or anything else), tell the operator instead of silently spawning a
# second ssh that will exit immediately.
$inUse = Get-NetTCPConnection -LocalPort $LocalPort -State Listen -ErrorAction SilentlyContinue
if ($inUse) {
    Write-Host "Local port $LocalPort is already in use — assuming a tunnel is up."
    if (-not $NoBrowser) { Start-Process "http://localhost:$LocalPort" }
    return
}

New-Item -ItemType Directory -Force -Path $pidDir | Out-Null

# -f backgrounds ssh after auth. The foreground process exits immediately;
# we re-discover the daemonised child by which process is now listening on
# the local port (below).
Start-Process -FilePath 'ssh' -ArgumentList $sshArgs -WindowStyle Hidden
Start-Sleep -Seconds 1

# After -f, the foreground ssh exits and the daemonised child does the work.
# Re-discover by local port: whichever ssh is now listening on $LocalPort is
# the one we want to remember.
$listening = Get-NetTCPConnection -LocalPort $LocalPort -State Listen -ErrorAction SilentlyContinue
if (-not $listening) {
    throw "ssh did not start listening on $LocalPort. Run the command manually to see the auth/host-key prompt: ssh $($sshArgs -join ' ')"
}

$tunnelPid = $listening.OwningProcess | Select-Object -First 1
Set-Content -Path $pidFile -Value $tunnelPid
Write-Host "Tunnel up: http://localhost:$LocalPort -> $target:$RemotePort (PID $tunnelPid)."
Write-Host "Stop with: scripts/seq-tunnel.ps1 -Stop"

if (-not $NoBrowser) {
    Start-Process "http://localhost:$LocalPort"
}
