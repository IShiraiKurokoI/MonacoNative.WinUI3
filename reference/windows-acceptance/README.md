# Windows release acceptance (manual evidence)

The Windows release checklist is composed from:

- `reference/windows-editor-interactive-matrix.json` (full editor and package UI)
- `reference/ime-interactive-matrix.json` (native TSF only)
- `reference/accessibility-interactive-matrix.json` (Narrator and UIA clients)

Run `./scripts/test-acceptance.ps1 -Mode Interactive -Configuration Release` in an
**unlocked Windows x64 desktop session** for the automated UIA test. Then generate
an evidence template (from the same source revision that was just tested):

```powershell
python scripts/windows-acceptance.py template --version 0.1.0 --output artifacts/release/windows-manual.json
```

Record the **real** tester, Windows build, DPI/monitor configurations, IMEs used,
and UTC review date. Carry out each case in the actual sample/package consumer;
set its status to `PASS` only after personally checking it. Use `FAIL` for defects,
`BLOCKED` for missing prerequisites, or keep `NOT_RUN` until it can be tested.
Case notes can contain repro steps, issue links and result specifics. The source
SHA-256 binds evidence to the relevant code, test harness, matrices, and golden.

For a versioned release, copy the reviewed JSON to
`reference/windows-acceptance/approved/<version>.json` (e.g. `0.1.0.json`), then:

```powershell
python scripts/windows-acceptance.py verify --version 0.1.0 --file reference/windows-acceptance/approved/0.1.0.json
./scripts/test-acceptance.ps1 -Mode Release -Configuration Release -PackageVersion 0.1.0
```

An incomplete matrix is **not** a pass. There are no pre-approved records in the
source archive: genuine interactive results cannot be produced on a Linux host.
Release CI rejects a missing, stale, incomplete or wrong-version approval.
After changes to source/tools/renderer baseline, collect fresh approval evidence.

The regular CI workflow includes native ARM64 execution on a Windows ARM64
hosted runner and runs an ARM64 package-only Core consumer. These jobs are
separate from interactive device testing and their reports must be reviewed
before counting a particular build as passed. OEM ARM64 startup/IME behavior,
full Narrator/DPI display acceptance and clean-machine installation still
require appropriate Windows devices; do not infer these from native CTest.
