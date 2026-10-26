#nullable disable
// Project code, MIT License. Shared by the MSBuild task and LanguageTests.
// Read-only PE inspection: never loads or executes the library being checked.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
#if !MONACONATIVE_BINARY_TESTS
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
#endif

namespace MonacoNative.Build
{
    public static class NativeBinaryInspector
    {
        public static string ResolveArchitecture(string platform, string platformTarget, string rid)
        {
            string selected = null;
            foreach (string raw in new[] { rid, platformTarget, platform })
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string value = raw.Replace(" ", "").ToLowerInvariant();
                if (value == "anycpu") continue;
                string arch = value == "x64" || value == "win-x64" ? "x64" :
                              value == "arm64" || value == "win-arm64" ? "arm64" : null;
                if (arch == null) throw new InvalidDataException("Unsupported native target: " + raw + ". Select x64 or ARM64 (win-x64 / win-arm64).");
                if (selected != null && selected != arch)
                    throw new InvalidDataException("Conflicting Platform, PlatformTarget and RuntimeIdentifier. They must select the same native architecture.");
                selected = arch;
            }
            if (selected == null) throw new InvalidDataException("Native validation needs an explicit x64 or ARM64 target; AnyCPU alone is not a native architecture.");
            return selected;
        }

        public static string FindDeployedLibrary(string directory, string fileName, string architecture)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new InvalidDataException("The native output directory is empty.");
            // Match the normal deployed lookup order. A bad root DLL must NOT be
            // hidden by falling back to a good RID DLL. No global LLVM / PATH search.
            string root = Path.Combine(directory, fileName);
            if (File.Exists(root)) return root;
            string rid = Path.Combine(directory, "runtimes", "win-" + architecture, "native", fileName);
            if (File.Exists(rid)) return rid;
            throw new FileNotFoundException("Required native library was not deployed. Checked " + root + " and " + rid + ". Restore packages and rebuild the selected architecture.", root);
        }

        public static void Validate(string path, string architecture, IEnumerable<string> requiredExports)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Required native library is missing: " + path, path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, Encoding.ASCII))
            {
                long length = stream.Length;
                Action<long, long> bounds = (offset, count) =>
                {
                    if (offset < 0 || count < 0 || offset > length || count > length - offset)
                        throw new InvalidDataException("Truncated or out-of-range PE data: " + path);
                };
                Func<long, ushort> u16 = offset => { bounds(offset, 2); stream.Position = offset; return reader.ReadUInt16(); };
                Func<long, uint> u32 = offset => { bounds(offset, 4); stream.Position = offset; return reader.ReadUInt32(); };
                if (u16(0) != 0x5A4D) throw new InvalidDataException("Not a Windows PE image (MZ signature missing): " + path);
                long pe = u32(0x3c);
                if (pe < 0x40 || u32(pe) != 0x00004550) throw new InvalidDataException("Invalid PE signature: " + path);
                ushort expected = architecture == "x64" ? (ushort)0x8664 : architecture == "arm64" ? (ushort)0xAA64 : (ushort)0;
                if (expected == 0) throw new InvalidDataException("Unsupported architecture: " + architecture);
                ushort actual = u16(pe + 4);
                if (actual != expected)
                    throw new InvalidDataException("Wrong native architecture for " + path + ": PE machine 0x" + actual.ToString("X4") + ", expected " + architecture + " (0x" + expected.ToString("X4") + ").");
                if ((u16(pe + 22) & 0x2002) != 0x2002)
                    throw new InvalidDataException("PE image is not an executable DLL: " + path);
                int sections = u16(pe + 6), optionalSize = u16(pe + 20);
                long optional = pe + 24;
                bounds(optional, optionalSize);
                if (optionalSize < 120 || u16(optional) != 0x20B)
                    throw new InvalidDataException("Expected a 64-bit PE32+ optional header: " + path);
                uint headersSize = u32(optional + 60), imageSize = u32(optional + 56);
                long sectionTable = optional + optionalSize;
                if (sections < 1 || sections > 96) throw new InvalidDataException("Invalid PE section count: " + path);
                bounds(sectionTable, sections * 40L);
                if (headersSize < sectionTable + sections * 40L || headersSize > length || imageSize < headersSize)
                    throw new InvalidDataException("Invalid PE image/header sizes: " + path);
                var regions = new List<uint[]>();
                for (int i = 0; i < sections; ++i)
                {
                    long header = sectionTable + i * 40L;
                    uint virtualSize = u32(header + 8), address = u32(header + 12), size = u32(header + 16), raw = u32(header + 20);
                    if (size != 0) bounds(raw, size);
                    if ((ulong)address + Math.Max(virtualSize, size) > imageSize)
                        throw new InvalidDataException("PE section exceeds the image: " + path);
                    regions.Add(new[] { address, virtualSize, raw, size });
                }
                Func<uint, uint, long> rva = (address, count) =>
                {
                    if ((ulong)address + count <= headersSize) { bounds(address, count); return address; }
                    foreach (var section in regions)
                    {
                        if (address < section[0]) continue;
                        ulong delta = (ulong)address - section[0];
                        if (delta + count <= section[3])
                        {
                            long offset = (long)section[2] + (long)delta;
                            bounds(offset, count); return offset;
                        }
                    }
                    throw new InvalidDataException("PE RVA has no file-backed data: " + path);
                };
                if (u32(optional + 108) < 1) throw new InvalidDataException("PE export directory is missing: " + path);
                uint exportRva = u32(optional + 112), exportSize = u32(optional + 116);
                if (exportRva == 0 || exportSize < 40 || (ulong)exportRva + exportSize > imageSize)
                    throw new InvalidDataException("PE export directory is invalid: " + path);
                long exports = rva(exportRva, 40);
                uint functionCount = u32(exports + 20), nameCount = u32(exports + 24);
                if (functionCount == 0 || functionCount > 1000000 || nameCount > functionCount)
                    throw new InvalidDataException("Invalid PE export counts: " + path);
                long functions = rva(u32(exports + 28), checked(functionCount * 4));
                long names = rva(u32(exports + 32), checked(nameCount * 4));
                long ordinals = rva(u32(exports + 36), checked(nameCount * 2));
                var found = new HashSet<string>(StringComparer.Ordinal);
                var required = new HashSet<string>(requiredExports ?? new string[0], StringComparer.Ordinal);
                for (uint i = 0; i < nameCount; ++i)
                {
                    uint nameRva = u32(names + i * 4L);
                    var name = new StringBuilder();
                    bool terminated = false;
                    for (uint j = 0; j < 4096; ++j)
                    {
                        stream.Position = rva(checked(nameRva + j), 1);
                        byte ch = reader.ReadByte();
                        if (ch == 0) { terminated = true; break; }
                        if (ch > 0x7f) throw new InvalidDataException("Non-ASCII PE export name: " + path);
                        name.Append((char)ch);
                    }
                    if (!terminated) throw new InvalidDataException("Unterminated PE export name: " + path);
                    string symbol = name.ToString();
                    ushort ordinal = u16(ordinals + i * 2L);
                    if (ordinal >= functionCount) throw new InvalidDataException("PE export ordinal is out of range: " + path);
                    uint address = u32(functions + ordinal * 4L);
                    if (!required.Contains(symbol)) continue;
                    if (address == 0) throw new InvalidDataException("Required export has no address: " + symbol);
                    if ((ulong)address >= exportRva && (ulong)address < (ulong)exportRva + exportSize)
                        throw new InvalidDataException("Required export is forwarded to another DLL: " + symbol + ". This check requires the actual implementation library.");
                    rva(address, 1);
                    found.Add(symbol);
                }
                foreach (string symbol in required)
                    if (!found.Contains(symbol)) throw new InvalidDataException("Required native export is missing: " + symbol + " in " + path + ". Do not mix binaries from different builds.");
            }
        }
    }

#if !MONACONATIVE_BINARY_TESTS
    public sealed class MonacoNativeValidateBinary : Task
    {
        public string FilePath { get; set; }
        public string DirectoryPath { get; set; }
        public string FileName { get; set; }
        public string Platform { get; set; }
        public string PlatformTarget { get; set; }
        public string RuntimeIdentifier { get; set; }
        [Required] public string[] RequiredExports { get; set; }

        public override bool Execute()
        {
            try
            {
                string arch = NativeBinaryInspector.ResolveArchitecture(Platform, PlatformTarget, RuntimeIdentifier);
                string path = string.IsNullOrWhiteSpace(FilePath)
                    ? NativeBinaryInspector.FindDeployedLibrary(DirectoryPath, FileName, arch) : FilePath;
                NativeBinaryInspector.Validate(path, arch, RequiredExports);
                Log.LogMessage(MessageImportance.High, "Native dependency check PASS: {0} ({1}; PE and {2} required exports; not a runtime test)", path, arch, RequiredExports.Length);
                return true;
            }
            catch (Exception error)
            {
                Log.LogError(null, "MNATIVE001", null, FilePath ?? DirectoryPath ?? "", 0, 0, 0, 0, "{0}", error.Message);
                return false;
            }
        }
    }
#endif
}
