#!/usr/bin/env python3
import sys, zipfile
from pathlib import Path
if len(sys.argv) != 2:
    raise SystemExit('usage: check-package.py <nupkg>')
p=Path(sys.argv[1])
with zipfile.ZipFile(p) as z:
    names=set(z.namelist())
    if p.name.startswith('MonacoNative.WinRT.CoreHost.'):
        required={
          'build/native/MonacoNative.WinRT.winmd',
          'runtimes/win-x64/native/MonacoNative.WinRT.Component.dll',
          'runtimes/win-arm64/native/MonacoNative.WinRT.Component.dll',
          'build/native/MonacoNative.WinRT.CoreHost.targets',
          'lib/native/_._',
        }
    else:
        required={
          'runtimes/win-x64/native/MonacoNative.Core.Native.dll',
          'runtimes/win-arm64/native/MonacoNative.Core.Native.dll',
          'lib/net8.0-windows10.0.26100.0/MonacoNative.Core.Managed.dll',
          'lib/net8.0-windows10.0.26100.0/MonacoNative.Renderer.WinUI3.dll',
          'lib/net8.0-windows10.0.26100.0/MonacoNative.EditorServices.dll',
          'lib/net8.0-windows10.0.26100.0/MonacoNative.Language.dll',
          'buildTransitive/net8.0-windows10.0.26100.0/MonacoNative.WinUI3.targets',
        }
    if p.name.startswith('MonacoNative.WinRT.CoreHost.') and 'lib/native/_._' in names and z.read('lib/native/_._'):
        raise SystemExit('native NuGet placeholder lib/native/_._ must be zero bytes')
    missing=sorted(required-names)
    if missing: raise SystemExit('package missing required assets: '+', '.join(missing))
    if not p.name.startswith('MonacoNative.WinRT.CoreHost.'):
        tfm='lib/net8.0-windows10.0.26100.0/'
        if not any(n.startswith(tfm) and n.endswith('.pri') for n in names):
            raise SystemExit('package missing WinUI PRI resource output')
        if not any(n.startswith(tfm) and n.endswith('.xbf') for n in names):
            raise SystemExit('package missing WinUI XBF resource output')
    forbidden=[n for n in names if '/obj/' in n or '__pycache__' in n]
    if forbidden: raise SystemExit('package contains build/cache artifacts: '+', '.join(forbidden[:10]))
print(f'Package layout OK: {p.name}')
