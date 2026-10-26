using System.Runtime.InteropServices;
using System.Text.Json;

namespace MonacoNative.Language.Cpp;

public sealed record CppToolchainPaths(string? LibraryPath, string? ResourceDirectory);

/// <summary>Locates an explicitly supplied or app-bundled in-process Clang runtime. Never launches a tool.</summary>
public static class CppToolchain
{
    public static CppToolchainPaths Locate(CppLanguageOptions options, string? applicationDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var library = options.LibClangPath ?? Environment.GetEnvironmentVariable("MONACONATIVE_LIBCLANG");
        var root = options.ToolchainRoot ?? Environment.GetEnvironmentVariable("MONACONATIVE_LLVM_ROOT");
        var resource = options.ClangResourceDirectory;
        var appDirectory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        // NuGet may flatten a RID-specific native asset into the application directory,
        // or retain runtimes/<rid>/native for a framework-dependent, RID-less build.
        // Match the process, never the host OS architecture (x64 emulation on ARM64).
        if (string.IsNullOrWhiteSpace(library) && string.IsNullOrWhiteSpace(root) && OperatingSystem.IsWindows())
        {
            var rid = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "win-x64", Architecture.Arm64 => "win-arm64", _ => null
            };
            if (rid is null) throw new PlatformNotSupportedException("The precompiled Clang runtime supports Windows x64 and ARM64 processes.");
            library = new[] { Path.Combine(appDirectory, "libclang.dll"),
                Path.Combine(appDirectory, "runtimes", rid, "native", "libclang.dll") }.FirstOrDefault(File.Exists);
            if (library is not null && resource is null)
            {
                var bundledHeaders = Path.Combine(appDirectory, "clang", "lib", "clang", "17");
                if (File.Exists(Path.Combine(bundledHeaders, "include", "stddef.h"))) resource = bundledHeaders;
            }
        }
        var inferResources = true;
        library = string.IsNullOrWhiteSpace(library) ? null : library;
        root = string.IsNullOrWhiteSpace(root) ? null : root;
        if (!string.IsNullOrWhiteSpace(library)) library = Path.GetFullPath(library);
        if (string.IsNullOrWhiteSpace(root))
        {
            if (!string.IsNullOrEmpty(library))
            {
                var parent = Path.GetDirectoryName(library)!;
                root = Path.GetFileName(parent) is "bin" or "lib" ? Path.GetDirectoryName(parent) : parent;
            }
            else
            {
                var bundled = Path.Combine(applicationDirectory ?? AppContext.BaseDirectory, "clang");
                if (File.Exists(Path.Combine(bundled, "monaco-upstream.json"))) root = bundled;
            }
        }
        if (!string.IsNullOrWhiteSpace(root))
        {
            root = Path.GetFullPath(root);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Clang toolchain directory does not exist: {root}");
            var receiptPath = Path.Combine(root, "monaco-upstream.json");
            if (File.Exists(receiptPath))
            {
                if (new FileInfo(receiptPath).Length > 64 * 1024) throw new InvalidDataException("Clang runtime receipt is too large.");
                using var receipt = JsonDocument.Parse(File.ReadAllText(receiptPath));
                var data = receipt.RootElement;
                if (data.ValueKind != JsonValueKind.Object ||
                    !data.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Number ||
                    !format.TryGetInt32(out var version) || version != 1 || Text(data, "component") != "llvm")
                    throw new InvalidDataException("Unsupported Clang runtime receipt.");
                var architecture = Text(data, "architecture");
                if (!string.Equals(architecture, RuntimeInformation.ProcessArchitecture.ToString(), StringComparison.OrdinalIgnoreCase))
                    throw new BadImageFormatException($"Clang runtime architecture {architecture} does not match this process ({RuntimeInformation.ProcessArchitecture}).");
                var receiptLibrary = ChildPath(root, Text(data, "library"));
                var receiptResource = ChildPath(root, Text(data, "resourceDirectory"));
                // Do not infer headers from an unrelated build when the caller overrides its DLL.
                inferResources = library is null || string.Equals(library, receiptLibrary,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                library ??= receiptLibrary;
                if (resource is null && inferResources) resource = receiptResource;
            }
            library ??= new[] { "bin/libclang.dll", "lib/libclang.so", "lib/libclang.dylib" }
                .Select(relative => Path.Combine(root, relative)).FirstOrDefault(File.Exists);
            if (library is null) throw new FileNotFoundException($"No libclang runtime was found in the configured directory: {root}");
            if (resource is null && inferResources)
            {
                var clang = Path.Combine(root, "lib", "clang");
                var candidates = Directory.Exists(clang) ? Directory.GetDirectories(clang)
                    .Where(path => File.Exists(Path.Combine(path, "include", "stddef.h"))).ToArray() : Array.Empty<string>();
                // With several installed versions, do not guess which one matches the library.
                if (candidates.Length == 1) resource = candidates[0];
            }
        }
        if (library is null && OperatingSystem.IsWindows())
            throw new DllNotFoundException("The libclang 17.0.4 native asset is missing. Restore NuGet packages and build/publish for win-x64 or win-arm64. No LLVM source build or clangd process is required.");
        if (library is not null && !File.Exists(library)) throw new FileNotFoundException("The configured libclang library does not exist.", library);
        if (library is not null && OperatingSystem.IsWindows()) ValidateWindowsArchitecture(library);
        if (resource is not null)
        {
            resource = Path.GetFullPath(resource);
            if (!Directory.Exists(Path.Combine(resource, "include"))) throw new DirectoryNotFoundException($"Clang resource headers do not exist: {resource}");
        }
        return new(library, resource);
    }

    private static void ValidateWindowsArchitecture(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d)
            throw new BadImageFormatException("The configured libclang library is not a Windows PE image.", path);
        stream.Position = 0x3c;
        var header = reader.ReadInt32();
        if (header < 64 || header > stream.Length - 6)
            throw new BadImageFormatException("Invalid PE header in the configured libclang library.", path);
        stream.Position = header;
        if (reader.ReadUInt32() != 0x00004550)
            throw new BadImageFormatException("Invalid PE signature in the configured libclang library.", path);
        var machine = reader.ReadUInt16();
        var expected = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0xaa64 : 0x8664;
        if (machine != expected)
            throw new BadImageFormatException($"libclang machine 0x{machine:x4} does not match {RuntimeInformation.ProcessArchitecture}.", path);
    }

    private static string Text(JsonElement data, string property)
    {
        if (!data.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"Clang runtime receipt is missing a valid {property}.");
        return value.GetString()!;
    }

    private static string ChildPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("Clang receipt paths must be relative.");
        var path = Path.GetFullPath(relative, root);
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Clang receipt path escapes the toolchain directory.");
        return path;
    }
}
