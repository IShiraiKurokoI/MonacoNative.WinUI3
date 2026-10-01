#!/usr/bin/env python3
from pathlib import Path
import sys
root = Path(__file__).resolve().parents[1]
ts = (root / 'src/MonacoNative.Input.Native/src/TsfTextStore.cpp').read_text(encoding='utf-8')
checks = [
    ('obsolete olectl include absent', '#include <olectl.h>' not in ts),
    ('CONNECT_E_NOCONNECTION fallback', '#define CONNECT_E_NOCONNECTION _HRESULT_TYPEDEF_(0x80040200L)' in ts),
    ('CONNECT_E_ADVISELIMIT fallback', '#define CONNECT_E_ADVISELIMIT _HRESULT_TYPEDEF_(0x80040201L)' in ts),
    ('NOCONNECTION semantic return', 'return CONNECT_E_NOCONNECTION;' in ts),
    ('ADVISELIMIT semantic return', 'return CONNECT_E_ADVISELIMIT;' in ts),
]
for name, ok in checks:
    print(f"{'PASS' if ok else 'FAIL'}: {name}")
if not all(ok for _, ok in checks):
    sys.exit(1)
print(f'PASS: {len(checks)}/{len(checks)} TSF connection HRESULT guards')
