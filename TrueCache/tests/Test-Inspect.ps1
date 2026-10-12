param(
    [Parameter(Mandatory = $true)]
    [string] $ProbePath
)
$ErrorActionPreference = 'Stop'
$probeFile = (Resolve-Path -LiteralPath $ProbePath).Path

function Invoke-Probe([string[]] $ProbeArguments) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $probeFile
    # Test inputs below are fixed tokens without quoting or whitespace.
    $startInfo.Arguments = $ProbeArguments -join ' '
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $startInfo.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw 'Could not start the probe.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            throw "Probe exceeded 30 seconds; process $($process.Id) was left running."
        }
        [pscustomobject]@{
            Code = $process.ExitCode
            Out = $stdout.GetAwaiter().GetResult()
            Error = $stderr.GetAwaiter().GetResult()
        }
    } finally {
        $process.Dispose()
    }
}

function Get-DriverState {
    $driver = Get-CimInstance Win32_SystemDriver -Filter "Name='Ramdisk'"
    if ($null -eq $driver) { return 'absent' }
    return "$($driver.State)|$($driver.StartMode)|$($driver.PathName)"
}

function Get-VolumeInventory {
    # DeviceID includes volume GUID paths for volumes without drive letters.
    # Exclude free-space counters and other values normal background I/O changes.
    $volumes = @(Get-CimInstance Win32_Volume |
        Sort-Object DeviceID |
        Select-Object DeviceID, DriveLetter, FileSystem, Capacity)
    return (ConvertTo-Json -InputObject $volumes -Compress)
}

$volumesBefore = Get-VolumeInventory
$before = Get-DriverState
$help = Invoke-Probe -ProbeArguments @('--help')
if ($help.Code -ne 0 -or $help.Out -notmatch 'feasibility probe') {
    throw 'Help failed.'
}
if ($help.Out -match 'driver.sha256|service.state|endpoint.open') {
    throw 'Help performed inspection.'
}
foreach ($verb in @('create', 'remove', 'cycle', 'run')) {
    $result = Invoke-Probe -ProbeArguments @($verb)
    if ($result.Code -ne 2 -or $result.Out.Length -ne 0) {
        throw "Mutating verb '$verb' was not rejected before inspection."
    }
}
$unknown = Invoke-Probe -ProbeArguments @('inspect', '--force')
if ($unknown.Code -ne 2 -or $unknown.Out.Length -ne 0) {
    throw 'Unknown option was not rejected before inspection.'
}
$inspection = Invoke-Probe -ProbeArguments @('inspect', '--verbose')
$inspection.Out
if ($inspection.Error) { $inspection.Error }
if ($inspection.Code -ne 0) {
    throw "Inspection did not finish: exit $($inspection.Code)."
}
if ($inspection.Out -notmatch 'probe.capability: Inspection only') {
    throw 'Inspection omitted its unqualified lifecycle limitation.'
}
# Compare the emitted fixed version with the same installed file as observed by
# the Windows-manifested PowerShell host. This catches compatibility substitution.
$driverPath = Join-Path ([System.Environment]::SystemDirectory) 'drivers\ramdisk.sys'
if (Test-Path -LiteralPath $driverPath) {
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($driverPath)
    $expectedVersion = '{0}.{1}.{2}.{3}' -f $version.FileMajorPart,
        $version.FileMinorPart, $version.FileBuildPart, $version.FilePrivatePart
    $match = [regex]::Match($inspection.Out,
        '\[Information\] driver\.version: ([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
    if (-not $match.Success -or $match.Groups[1].Value -ne $expectedVersion) {
        throw "Driver version differs from the installed file: expected $expectedVersion, observed '$($match.Groups[1].Value)'."
    }
}
$after = Get-DriverState
if ($before -ne $after) {
    throw "Driver state changed during read-only checks: '$before' -> '$after'."
}
$volumesAfter = Get-VolumeInventory
if ($volumesBefore -ne $volumesAfter) {
    throw "Volume inventory changed during read-only checks. Before: $volumesBefore After: $volumesAfter"
}
'Ramdisk service state and volume inventory are unchanged.'
'Read-only probe integration checks passed.'
