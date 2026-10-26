using System.Text;
using MonacoNative.Build;

// Project-owned regression tests, not an upstream port. The same inspector
// source is compiled into LanguageTests and into the MSBuild inline task.
internal static class NativeBinaryValidationTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "monaco-pe-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var assertions = 0;
        void Pass(Action action) { action(); assertions++; }
        void Reject(Action action)
        {
            try { action(); }
            catch (Exception e) when (e is InvalidDataException or FileNotFoundException or OverflowException)
            { assertions++; return; }
            throw new Exception("Invalid native fixture was accepted.");
        }
        var names = new[] { "mn_abi_version", "mn_typescript_request" };
        string Write(byte[] bytes, string name = "fixture.dll")
        { var path = Path.Combine(root, name); File.WriteAllBytes(path, bytes); return path; }
        void CheckMutation(Action<byte[]> mutate)
        { var bytes = Fixture(0x8664, names); mutate(bytes); Reject(() => NativeBinaryInspector.Validate(Write(bytes), "x64", names)); }
        try
        {
            Pass(() => NativeBinaryInspector.Validate(Write(Fixture(0x8664, names)), "x64", names));
            Pass(() => NativeBinaryInspector.Validate(Write(Fixture(0xaa64, names)), "arm64", names));
            Pass(() => AssertEx.Equal("x64", NativeBinaryInspector.ResolveArchitecture("x64", "AnyCPU", "win-x64"), "explicit RID"));
            Pass(() => AssertEx.Equal("arm64", NativeBinaryInspector.ResolveArchitecture("ARM64", "ARM64", null!), "platform target"));
            Pass(() => AssertEx.Equal("x64", NativeBinaryInspector.ResolveArchitecture("Any CPU", "", "win-x64"), "Any CPU library host"));
            Reject(() => NativeBinaryInspector.ResolveArchitecture("x64", "ARM64", ""));
            Reject(() => NativeBinaryInspector.ResolveArchitecture("ARM64", "", "win-x64"));
            Reject(() => NativeBinaryInspector.ResolveArchitecture("AnyCPU", "", ""));
            Reject(() => NativeBinaryInspector.ResolveArchitecture("x86", "", ""));
            Reject(() => NativeBinaryInspector.ResolveArchitecture("x64", "", "linux-x64"));
            Reject(() => NativeBinaryInspector.Validate(Path.Combine(root, "missing.dll"), "x64", names));
            Reject(() => NativeBinaryInspector.Validate(Write(Fixture(0xaa64, names)), "x64", names));
            Reject(() => NativeBinaryInspector.Validate(Write(Fixture(0x8664, names)), "x64", new[] { "missing_export" }));
            Reject(() => NativeBinaryInspector.Validate(Write(new byte[8]), "x64", names));
            CheckMutation(b => b[0] = 0); // MZ signature.
            CheckMutation(b => Put32(b, 0x3c, 0xfffffff0)); // PE pointer overflow/out of range.
            CheckMutation(b => b[0x80] = 0); // PE signature.
            CheckMutation(b => Put16(b, 0x96, 0x0002)); // EXE, not DLL.
            CheckMutation(b => Put16(b, 0x98, 0x010b)); // PE32 instead of PE32+.
            CheckMutation(b => Put16(b, 0x94, 8)); // Too-short optional header.
            CheckMutation(b => Put16(b, 0x86, 0)); // No sections.
            CheckMutation(b => Put32(b, 0x98 + 60, 16)); // Header bounds.
            CheckMutation(b => Put32(b, 0x98 + 108, 0)); // Missing export directory.
            CheckMutation(b => Put32(b, 0x98 + 112, 0xfffffff0)); // Invalid export RVA.
            CheckMutation(b => Put32(b, 0x200 + 24, 1000001)); // Invalid export count.
            CheckMutation(b => Put32(b, 0x200 + 32, 0x2fff)); // File-unbacked name table.
            CheckMutation(b => Put16(b, 0x300, 99)); // Invalid ordinal.
            CheckMutation(b => Put32(b, 0x240, 0)); // Null export.
            CheckMutation(b => Put32(b, 0x240, 0x1100)); // Forwarded required export.
            CheckMutation(b => Put32(b, 0x240, 0x2fff)); // File-unbacked function.
            CheckMutation(b => b[0x400] = 0xff); // Invalid export string.
            // The deployed lookup checks the root first. A wrong root DLL must
            // fail, even when the RID directory contains a valid alternative.
            var ridDir = Path.Combine(root, "runtimes", "win-x64", "native");
            Directory.CreateDirectory(ridDir);
            var ridDll = Path.Combine(ridDir, "libclang.dll");
            File.WriteAllBytes(ridDll, Fixture(0x8664, names));
            Pass(() => AssertEx.Equal(ridDll, NativeBinaryInspector.FindDeployedLibrary(root, "libclang.dll", "x64"), "RID layout"));
            var rootDll = Write(Fixture(0xaa64, names), "libclang.dll");
            Pass(() => AssertEx.Equal(rootDll, NativeBinaryInspector.FindDeployedLibrary(root, "libclang.dll", "x64"), "root wins"));
            Reject(() => NativeBinaryInspector.Validate(NativeBinaryInspector.FindDeployedLibrary(root, "libclang.dll", "x64"), "x64", names));
            Reject(() => NativeBinaryInspector.FindDeployedLibrary(root, "absent.dll", "x64"));
            // Inspection must not lock the file after success or failure.
            Pass(() => { NativeBinaryInspector.Validate(ridDll, "x64", names); File.Delete(ridDll); });
            Console.WriteLine($"Native binary inspector: {assertions} regression checks PASS (synthetic PE fixtures; no DLL execution).");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] Fixture(ushort machine, string[] names)
    {
        var b = new byte[0xc00];
        Put16(b, 0, 0x5a4d); Put32(b, 0x3c, 0x80); Put32(b, 0x80, 0x4550);
        Put16(b, 0x84, machine); Put16(b, 0x86, 2); Put16(b, 0x94, 240); Put16(b, 0x96, 0x2022);
        const int optional = 0x98;
        Put16(b, optional, 0x20b); Put32(b, optional + 56, 0x3000); Put32(b, optional + 60, 0x200);
        Put32(b, optional + 108, 16); Put32(b, optional + 112, 0x1000); Put32(b, optional + 116, 0x800);
        const int section = optional + 240;
        Put32(b, section + 8, 0x800); Put32(b, section + 12, 0x1000);
        Put32(b, section + 16, 0x800); Put32(b, section + 20, 0x200);
        Put32(b, section + 40 + 8, 0x200); Put32(b, section + 40 + 12, 0x2000);
        Put32(b, section + 40 + 16, 0x200); Put32(b, section + 40 + 20, 0xa00);
        Put32(b, 0x200 + 20, (uint)names.Length); Put32(b, 0x200 + 24, (uint)names.Length);
        Put32(b, 0x200 + 28, 0x1040); Put32(b, 0x200 + 32, 0x1080); Put32(b, 0x200 + 36, 0x1100);
        var pos = 0x400;
        for (var i = 0; i < names.Length; i++)
        {
            Put32(b, 0x240 + i * 4, 0x2000 + (uint)i);
            Put32(b, 0x280 + i * 4, (uint)(pos - 0x200 + 0x1000));
            Put16(b, 0x300 + i * 2, (ushort)i);
            var text = Encoding.ASCII.GetBytes(names[i]); text.CopyTo(b, pos); pos += text.Length + 1;
        }
        return b;
    }
    private static void Put16(byte[] b, int offset, ushort value)
    { b[offset] = (byte)value; b[offset + 1] = (byte)(value >> 8); }
    private static void Put32(byte[] b, int offset, uint value)
    { for (var i = 0; i < 4; i++) b[offset + i] = (byte)(value >> (i * 8)); }
}
