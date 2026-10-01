from pathlib import Path
import sys
root = Path(__file__).resolve().parents[1]
tsf = (root/'src/MonacoNative.Input.Native/src/TsfTextStore.cpp').read_text()
test = (root/'tests/NativeInputTests/TsfTextStoreTests.cpp').read_text()
core = (root/'src/MonacoNative.Core.Native/MonacoNative.Core.Native.cpp').read_text()
checks = [
    ('ocidl interface header', '#include <ocidl.h>' in tsf),
    ('obsolete olectl include absent', '#include <olectl.h>' not in tsf),
    ('no nonexistent TS_E_INVALIDARG return', 'return TS_E_INVALIDARG' not in tsf),
    ('sink start transaction', 'OnStartEditTransaction() noexcept override' in test),
    ('sink end transaction', 'OnEndEditTransaction() noexcept override' in test),
    ('memory backend header', 'MemoryAcpTextStoreBackend.h' in test),
    ('no stale range.start', 'change.range.start.' not in core),
    ('no stale range.end', 'change.range.end.' not in core),
    ('no vulnerable std::min(', 'std::min(' not in core),
    ('collapsed GetTextExt test', 'GetTextExt accepts collapsed caret range' in test),
]
failed=[name for name, ok in checks if not ok]
if failed:
    print('TSF source guard FAILED: ' + ', '.join(failed))
    sys.exit(1)
print(f'Windows TSF source guards OK: {len(checks)}/{len(checks)}')
