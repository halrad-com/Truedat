# TrueCache

TrueCache explores a Windows 10/11 x64 RAM-drive utility using Microsoft's
installed RAM-disk driver. The current source is a **read-only feasibility probe**.
It cannot create, format, or remove a disk, and no driver version is qualified
for those operations.

The planned utility supports explicit create/remove and command-scoped cleanup.
TrueDat can consume a RAM drive through its existing `stage-dir` setting once
the native lifecycle has been demonstrated.

## Build the probe

Requires Visual Studio C++ desktop tools and a Windows SDK. Everything is
contained under `TrueCache/`; this build does not invoke the TrueDat build.

```cmd
TrueCache\build.cmd
TrueCache\build.cmd test
```

Both commands build Release x64 with the static C++ runtime. The second also
builds and runs the policy tests and read-only inspection checks. The inspection
checks compare the reported file version and verify unchanged service state and
volume inventory, including volumes without drive letters. Build products,
symbols, and logs go to the ignored `TrueCache/out/` directory. No package
download is part of the build.

In a shared checkout, follow its coordination and explicit build-authorization
rules before running either command.

## Inspect

```cmd
TrueCache\out\truecache-probe.exe inspect
TrueCache\out\truecache-probe.exe inspect --verbose
```

The probe reports:

- Native architecture, OS build, and process elevation.
- Installed driver version, PE machine, SHA-256, and cached trust-check result.
- Service configuration/state and relevant existing startup parameter values.
- Present RAM-disk bus interfaces, device instance IDs, and endpoint accessibility.
- Whether the file matches the binary inspected during development.

A matching fingerprint means the static research applies to that file. It does
not mean disk creation or removal works. A stopped service or absent endpoint is
reported as an observation, not as proof the native approach cannot work.

The probe requests no driver-start or configuration-write access, sends no
private controls, and creates no disk. Endpoint opens request zero data access.
They do not establish that a later read/write or creation request is permitted.
It does not elevate itself. Access failures retain their native error codes.

Trust checking uses WinVerifyTrust with cached information, no UI, and revocation
checking disabled. It makes no claim of a fresh revocation check; an unsuccessful
trust check alone does not establish that a file is unsigned. File reads briefly
deny replacement/writes while the driver identity is collected.

Logs contain UTC timestamps. Redirected probe output is UTF-8. Keep detailed logs
local when paths or device identifiers are private.

Exit codes:

| Code | Meaning |
| --- | --- |
| 0 | Help or inspection finished; this is not a RAM-disk feasibility result |
| 2 | Invalid arguments, including unsupported mutation commands |
| 3 | Inspection incomplete; review API diagnostics |

## Native lifecycle qualification

The next experiment must establish startup, exact requests, live disk identity,
raw write/read correctness, removal, and memory reclamation in a disposable
Windows test environment. Filesystem formatting and normal command execution
depend on that evidence.

Undocumented interfaces may change with Windows updates. The probe deliberately
contains no creation/removal request encoder or compatibility override.

Microsoft names the driver and creation control in its
[RAM-disk boot diagnostic documentation](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/bug-check-0xf8--ramdisk-boot-initialization-failed).
That reference does not specify a public desktop creation/removal contract.
