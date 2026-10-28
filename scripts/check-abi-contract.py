#!/usr/bin/env python3
"""Verify the current native ABI against its checked-in contract and C# binding."""

import json
import re
import sys
from pathlib import Path

root = Path(__file__).resolve().parents[1]
header = (root / 'src/MonacoNative.Core.Native/MonacoNative.Core.Native.h').read_text(encoding='utf-8')
managed = (root / 'src/MonacoNative.Core.Managed/MonacoNativeAbi.cs').read_text(encoding='utf-8')
bindings = (root / 'src/MonacoNative.Core.Managed/NativeMethods.cs').read_text(encoding='utf-8')
contract = json.loads((root / 'reference/native-abi.json').read_text(encoding='utf-8'))

version = contract['abiVersion']
match = re.fullmatch(r'(\d+)\.(\d+)\.(\d+)', version)
if match is None:
    sys.exit(f'Invalid current ABI version: {version!r}')
major, minor, patch = map(int, match.groups())
if any(part > 255 for part in (major, minor, patch)):
    sys.exit(f'ABI version exceeds packed-version field width: {version}')
packed = (major << 16) | (minor << 8) | patch
if contract['packedVersion'] != f'0x{packed:08X}':
    sys.exit(f'ABI manifest has inconsistent packedVersion for {version}')

exports = [m.group(2) for m in re.finditer(
    r'MN_API\s+[^;\n]+?\b(MN_CALL\s+)?(mn_[A-Za-z0-9_]+)\s*\(', header
)]
expected = contract['exports']
if (not isinstance(expected, list) or not expected or
        any(not isinstance(name, str) or re.fullmatch(r'mn_[A-Za-z0-9_]+', name) is None for name in expected) or
        len(set(expected)) != len(expected)):
    sys.exit('Native ABI manifest must contain a nonempty, unique list of mn_* exports')
if exports != expected:
    missing = [item for item in expected if item not in exports]
    added = [item for item in exports if item not in expected]
    sys.exit(f'Current ABI export contract drift: missing={missing}, added={added}; check order as well')

# Managed imports cannot silently refer to functions outside the one native contract.
imports = re.findall(r'\b(mn_[A-Za-z0-9_]+)\s*\(', bindings)
if len(imports) != len(set(imports)):
    sys.exit('Duplicate native P/Invoke method names in managed bindings')
uncontracted = sorted(set(imports) - set(expected))
if uncontracted:
    sys.exit(f'Managed bindings reference exports absent from current ABI: {uncontracted}')

structs = contract.get('structs')
if not isinstance(structs, dict) or not structs:
    sys.exit('Native ABI manifest must list its 64-bit layout contracts')
for name, layout in structs.items():
    if re.fullmatch(r'mn_[A-Za-z0-9_]+', name) is None:
        sys.exit(f'Invalid native struct name in ABI manifest: {name!r}')
    if re.search(rf'\bstruct\s+{re.escape(name)}\s*\{{', header) is None:
        sys.exit(f'ABI manifest references missing native struct: {name}')
    if not isinstance(layout, dict) or type(layout.get('size64')) is not int or layout['size64'] <= 0:
        sys.exit(f'Invalid 64-bit ABI size contract for {name}')

for name, value in [('MAJOR', major), ('MINOR', minor), ('PATCH', patch)]:
    native_pattern = rf'^#define\s+MN_ABI_VERSION_{name}\s+{value}u\s*$'
    if re.search(native_pattern, header, flags=re.MULTILINE) is None:
        sys.exit(f'Native ABI {name} must match current contract {version}')
    managed_pattern = rf'public\s+const\s+uint\s+{name.title()}\s*=\s*{value}\s*;'
    if re.search(managed_pattern, managed) is None:
        sys.exit(f'Managed ABI {name} must match current contract {version}')

# A matching version declaration is not enough: do not accept a different native
# minor/patch at runtime when shipping only one managed/native ABI pair.
if re.search(r'if\s*\(\s*native\.AbiVersion\s*!=\s*PackedVersion\s*\)\s*throw\s+new\s+PlatformNotSupportedException', managed) is None:
    sys.exit('Managed ABI must require the exact current packed version')

print(f'Current native ABI {version} contract OK: {len(exports)} exports, {len(imports)} managed imports, {len(structs)} layouts; native and managed match')

# Language extension is separately versioned, without changing the editor/model ABI.
language = contract['languageExtension']
language_header = (root / 'src/MonacoNative.Core.Native' / language['header']).read_text(encoding="utf-8")
language_binding = (root / 'src/MonacoNative.Language/NativeCompilerService.cs').read_text(encoding="utf-8")
language_exports = re.findall(r'MN_API\s+[^;\n]+?\bMN_CALL\s+(mn_[A-Za-z0-9_]+)\s*\(', language_header)
assert language_exports == language['exports'], 'native language export contract drift'
language_imports = re.findall(r'private static extern [^;\n]+?\b(mn_[A-Za-z0-9_]+)\s*\(', language_binding)
assert language_imports == language['exports'], 'managed language import contract drift'
assert language['version'] == 1 and 'mn_language_abi_version() != 1' in language_binding
assert language['structs']['mn_language_document']['size64'] == 24
assert 'public IntPtr Path, Text; public nuint Length;' in language_binding
print(f'Native language extension ABI {language["version"]} OK: {len(language_exports)} exports and matching managed imports')
