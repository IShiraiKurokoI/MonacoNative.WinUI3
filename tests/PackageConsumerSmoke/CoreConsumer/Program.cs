using MonacoNative.Core;

Console.Error.WriteLine("[CoreConsumer] Main entered; testing packaged native DLL");
Console.Error.Flush();
MonacoNativeAbi.EnsureCompatible();
Console.Error.WriteLine("[CoreConsumer] Native ABI verified");
Console.Error.Flush();
var abi = MonacoNativeAbi.GetInfo();
if (abi.PackedVersion != MonacoNativeAbi.PackedVersion || abi.PointerSize != 8)
    throw new InvalidOperationException($"Unexpected ABI: {abi}");
using var model = new MonacoTextModel("one\ntwo", "plaintext");
model.Replace(0, 3, "ONE");
if (model.Text != "ONE\ntwo")
    throw new InvalidOperationException("Package native/managed smoke edit failed.");
Console.WriteLine($"MonacoNative package smoke OK: ABI {abi.Major}.{abi.Minor}.{abi.Patch}, lines={model.LineCount}");
