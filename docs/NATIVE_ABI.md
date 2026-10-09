# Native ABI contract

MonacoNative ships **one supported native C ABI: 1.3.0** (packed version
`0x00010300`). This is a project-defined interface, not a Windows App SDK,
WinUI, or C++/WinRT version. No alternate older ABI implementation or
version-specific binding is built or distributed.

## Authoritative contract

- [`reference/native-abi.json`](../reference/native-abi.json) is the one checked-in
  contract for the exported symbol list, version, supported architectures, and
  64-bit structure sizes. It currently lists **91 exports**.
- [`MonacoNative.Core.Native.h`](../src/MonacoNative.Core.Native/MonacoNative.Core.Native.h)
  defines the corresponding native declarations, data layouts, and calling
  conventions. [`MonacoNativeAbi.cs`](../src/MonacoNative.Core.Managed/MonacoNativeAbi.cs)
  defines the managed expected version and layout checks.
- `MonacoNativeAbi.EnsureCompatible()` requires an **exact** packed version
  match, not merely matching major versions. Both binaries must be built and
  deployed together. The version and `struct_size` discovery functions remain
  in the current ABI so mismatches can be diagnosed safely; they do **not**
  constitute support for older bindings.
- Exported names, calling conventions, the UTF-16 code unit representation,
  layout, and ownership semantics are part of the contract. The Windows native
  interface uses 16-bit `wchar_t`; portable builds use 16-bit `char16_t`.

## Changing the interface

1. Keep implementation-only changes at **1.3.0**; do not bump ABI for CI,
   packaging, or implementation fixes that leave its binary contract intact.
2. When the binary contract does change, intentionally choose the new version
   (additive changes normally increment minor; incompatible changes increment
   major), then update the contract, native declarations, managed binding, and
   version/layout tests **in the same change**.
3. Do not introduce alternate versioned header, manifest, test suite, or
   distribution path for an older interface without an explicit product
   requirement. Existing exports must not be removed simply to shorten code.

## Validation

```bash
python3 scripts/check-abi-contract.py
python3 scripts/check-semantic-names.py
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build --parallel
ctest --test-dir build --output-on-failure -L '^abi$'
```

The CI jobs also validate the contract against the C# P/Invoke declarations and
execute the Windows MSVC native ABI tests and managed integration tests.
Passing Linux CTest alone is not evidence that the Windows package is valid.
