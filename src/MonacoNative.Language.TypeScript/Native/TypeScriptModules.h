#pragma once
#include "TypeScriptDeclarations.h"
#include "TypeScriptPreProcess.h"
#include <filesystem>
#include <map>
#include <memory>

namespace mn::typescript
{
// Snapshot-only ES module linking for the bounded declaration service. This is not
// TypeScript's resolver/checker: no package.json/tsconfig evaluation or disk crawling.
// Types are qualified by source path; identically named classes in different modules
// must never share a member set. Imports do not leak a dependency's private globals.
class ModuleGraph
{
    struct Binding { std::string local, remote, specifier; size_t token = 0; };
    struct Export { std::string exported, local, specifier; };
    struct Entry
    {
        std::string path;
        mn::language::Text text;
        Declarations declarations;
        PreProcessedFileInfo dependencies;
        std::vector<Binding> imports;
        std::vector<Export> exportSpecs;
        std::map<std::string, Symbol> exports;
        unsigned state = 0;
        Entry(std::string p, std::string_view source)
            : path(std::move(p)), text(source), declarations(text, path),
              dependencies(pre_process_file(text, true, javascript_path(path))) {}
    };
    std::map<std::string, std::unique_ptr<Entry>> entries;
    Entry* active = nullptr;
    static std::filesystem::path utf8_path(std::string_view value)
    {
        return std::filesystem::path(std::u8string(reinterpret_cast<const char8_t*>(value.data()), value.size()));
    }
    static std::string path_key(std::string_view path)
    {
        auto value = utf8_path(path).lexically_normal().generic_u8string();
        std::string key(reinterpret_cast<const char*>(value.data()), value.size());
#ifdef _WIN32
        // Windows file URI paths are case insensitive for the supported host setup.
        for (auto& c : key) if (c >= 'A' && c <= 'Z') c = static_cast<char>(c + ('a' - 'A'));
#endif
        return key;
    }
    static bool starts(std::string_view value, std::string_view prefix) { return value.starts_with(prefix); }
    static size_t statement_end(const Declarations& d, size_t begin)
    {
        for (auto i = begin; i < d.tokens.size(); i = d.skip(i, d.tokens.size()))
        {
            if (d.k(i) == sk::SemicolonToken) return i;
            if (i > begin && (d.tokens[i].flags & tf::PrecedingLineBreak) &&
                (d.k(i) == sk::ImportKeyword || d.k(i) == sk::ExportKeyword ||
                 d.k(i) == sk::ConstKeyword || d.k(i) == sk::LetKeyword ||
                 d.k(i) == sk::ClassKeyword || d.k(i) == sk::FunctionKeyword)) return i;
        }
        return d.tokens.size();
    }
    static void read_module_syntax(Entry& e)
    {
        const auto& d = e.declarations;
        for (size_t i = 0; i < d.tokens.size();)
        {
            const auto initial = i;
            if (d.k(i) == sk::ImportKeyword && d.k(i + 1) != sk::OpenParenToken && d.k(i + 1) != sk::DotToken)
            {
                const auto end = statement_end(d, i + 1);
                size_t from = i + 1;
                while (from < end && d.k(from) != sk::FromKeyword) from = d.skip(from, end);
                if (from + 1 < end && d.k(from + 1) == sk::StringLiteral)
                {
                    auto specifier = d.spelling(from + 1);
                    auto j = i + 1;
                    if (d.k(j) == sk::TypeKeyword && d.k(j + 1) != sk::FromKeyword) ++j;
                    if (d.name(j))
                    {
                        e.imports.push_back({d.spelling(j), "default", specifier, j});
                        if (d.k(++j) == sk::CommaToken) ++j;
                    }
                    if (d.k(j) == sk::AsteriskToken && d.k(j + 1) == sk::AsKeyword && d.name(j + 2))
                        e.imports.push_back({d.spelling(j + 2), "*", specifier, j + 2});
                    else if (d.k(j) == sk::OpenBraceToken)
                    {
                        for (++j; j < from && d.k(j) != sk::CloseBraceToken;)
                        {
                            if (d.k(j) == sk::TypeKeyword && d.name(j + 1) && d.k(j + 1) != sk::AsKeyword) ++j;
                            if (!d.name(j)) { ++j; continue; }
                            auto remote = d.spelling(j), local = remote; auto token = j++;
                            if (d.k(j) == sk::AsKeyword && d.name(j + 1)) { local = d.spelling(j + 1); token = j + 1; j += 2; }
                            e.imports.push_back({local, remote, specifier, token});
                        }
                    }
                }
                i = end; if (d.k(i) == sk::SemicolonToken) ++i;
            }
            else if (d.k(i) == sk::ExportKeyword)
            {
                auto j = i + 1;
                const bool isDefault = d.k(j) == sk::DefaultKeyword;
                if (isDefault) ++j;
                while (d.k(j) == sk::DeclareKeyword || d.k(j) == sk::AsyncKeyword || d.k(j) == sk::AbstractKeyword) ++j;
                if (d.k(j) == sk::OpenBraceToken && d.mate[j] != Declarations::none)
                {
                    const auto close = d.mate[j]; std::string specifier;
                    if (d.k(close + 1) == sk::FromKeyword && d.k(close + 2) == sk::StringLiteral) specifier = d.spelling(close + 2);
                    for (++j; j < close;)
                    {
                        if (d.k(j) == sk::TypeKeyword && d.name(j + 1) && d.k(j + 1) != sk::AsKeyword) ++j;
                        if (!d.name(j)) { ++j; continue; }
                        auto local = d.spelling(j++), exported = local;
                        if (d.k(j) == sk::AsKeyword && d.name(j + 1)) { exported = d.spelling(j + 1); j += 2; }
                        e.exportSpecs.push_back({exported, local, specifier});
                    }
                    i = close + (specifier.empty() ? 1 : 3);
                }
                else if (d.k(j) == sk::AsteriskToken && d.k(j + 1) == sk::FromKeyword && d.k(j + 2) == sk::StringLiteral)
                { e.exportSpecs.push_back({"*", "*", d.spelling(j + 2)}); i = j + 3; }
                else if ((d.k(j) == sk::ClassKeyword || d.k(j) == sk::InterfaceKeyword || d.k(j) == sk::EnumKeyword || d.k(j) == sk::FunctionKeyword || d.k(j) == sk::TypeKeyword) && d.name(j + 1))
                { e.exportSpecs.push_back({isDefault ? "default" : d.spelling(j + 1), d.spelling(j + 1), ""}); i = j + 2; }
                else if (d.k(j) == sk::ConstKeyword || d.k(j) == sk::LetKeyword || d.k(j) == sk::VarKeyword)
                {
                    const auto end = statement_end(d, j + 1);
                    for (const auto& symbol : d.symbols)
                        if (!symbol.builtin && symbol.scope == 0 && symbol.owner.empty() && symbol.token > j && symbol.token < end)
                            e.exportSpecs.push_back({symbol.name, symbol.name, ""});
                    i = end;
                }
                else if (isDefault && d.name(j)) { e.exportSpecs.push_back({"default", d.spelling(j), ""}); i = j + 1; }
                else i = j;
            }
            else i = d.skip(i, d.tokens.size());
            if (i <= initial) i = initial + 1;
        }
    }
    Entry* resolve(const Entry& from, std::string_view specifier) const
    {
        if (!(starts(specifier, "./") || starts(specifier, "../"))) return nullptr;
        auto base = utf8_path(from.path).parent_path() / utf8_path(specifier);
        auto raw = base.generic_u8string();
        std::string key = path_key(std::string_view(reinterpret_cast<const char*>(raw.data()), raw.size()));
        std::vector<std::string> candidates;
        // Match TS's common .js -> .ts source substitution, but do not claim full
        // NodeNext/Bundler/package resolution. Only supplied snapshots can resolve.
        const auto extension = base.extension().string();
        if (extension == ".js" || extension == ".jsx" || extension == ".mjs" || extension == ".cjs")
        {
            auto stem = key.substr(0, key.size() - extension.size());
            if (extension == ".mjs") candidates = {stem + ".mts", stem + ".d.mts"};
            else if (extension == ".cjs") candidates = {stem + ".cts", stem + ".d.cts"};
            else candidates = {stem + ".ts", stem + ".d.ts"};
        }
        candidates.push_back(key);
        // A dotted module basename (e.g. ./demo.types) is not necessarily a
        // source extension. Still probe its .ts/.d.ts and index candidates.
        if (extension != ".ts" && extension != ".tsx" && extension != ".js" && extension != ".jsx" &&
            extension != ".mts" && extension != ".cts" && extension != ".mjs" && extension != ".cjs")
            for (auto suffix : {".ts", ".d.ts", ".js", ".mts", ".cts", "/index.ts", "/index.d.ts", "/index.js"}) candidates.push_back(key + suffix);
        for (const auto& candidate : candidates)
            if (const auto found = entries.find(candidate); found != entries.end()) return found->second.get();
        return nullptr;
    }
    static std::string qualify(const Entry& e, std::string type)
    {
        if (type.empty() || starts(type, "@module:") || starts(type, "@namespace:")) return type;
        // Qualify local declarations and object-literal shapes only, never primitives.
        if (starts(type, "@object:")) return "@module:" + e.path + "#" + type;
        if (e.declarations.aliases.contains(type)) return "@module:" + e.path + "#" + type;
        for (const auto& symbol : e.declarations.symbols)
            if (!symbol.builtin && symbol.owner.empty() && symbol.scope == 0 && symbol.name == type &&
                (symbol.kind == "Class" || symbol.kind == "Interface" || symbol.kind == "Enum"))
                return symbol.imported ? symbol.type : "@module:" + e.path + "#" + type;
        return type;
    }
    static void copy_shapes(const Entry& from, Declarations& to)
    {
        std::unordered_set<std::string> existing;
        for (const auto& s : to.symbols) if (!s.owner.empty()) existing.insert(s.owner + '\0' + s.name + '\0' + s.detail);
        for (const auto& original : from.declarations.symbols)
        {
            if (original.builtin || original.owner.empty()) continue;
            auto symbol = original; symbol.owner = qualify(from, symbol.owner); symbol.type = qualify(from, symbol.type);
            symbol.imported = true; symbol.scope = 0; symbol.token = 0;
            if (existing.insert(symbol.owner + '\0' + symbol.name + '\0' + symbol.detail).second)
            {
                if (to.symbols.size() >= 100000) throw std::length_error("TypeScript imported-symbol budget exceeded.");
                to.symbols.push_back(std::move(symbol));
            }
        }
        for (const auto& [key, value] : from.declarations.bases) to.bases[qualify(from, key)] = qualify(from, value);
        for (const auto& [key, value] : from.declarations.aliases) to.aliases[qualify(from, key)] = qualify(from, value);
    }
    static void bind(const Binding& binding, const Entry& dependency, Declarations& d)
    {
        copy_shapes(dependency, d);
        if (binding.remote == "*")
        {
            const auto owner = "@namespace:" + dependency.path;
            Symbol root; root.name = binding.local; root.kind = "Module"; root.type = owner; root.token = binding.token;
            root.imported = true; root.detail = "module " + binding.local; root.sourceFile = dependency.path;
            d.symbols.push_back(std::move(root));
            for (const auto& [exported, original] : dependency.exports)
            {
                auto symbol = original; symbol.owner = owner; symbol.name = exported; symbol.isStatic = false;
                symbol.imported = true; symbol.scope = 0; symbol.token = binding.token;
                d.symbols.push_back(std::move(symbol));
                d.aliases[binding.local + "." + exported] = original.type;
            }
        }
        else if (const auto found = dependency.exports.find(binding.remote); found != dependency.exports.end())
        {
            auto symbol = found->second;
            symbol.name = binding.local; symbol.owner.clear(); symbol.scope = 0; symbol.token = binding.token; symbol.imported = true;
            if (symbol.kind == "Class" || symbol.kind == "Interface" || symbol.kind == "Enum") d.aliases[binding.local] = symbol.type;
            d.symbols.push_back(std::move(symbol));
        }
    }
    // Host integration of upstream dependency spans with the snapshot-only
    // resolver. This is not TypeScript's full module resolver or completion engine.
    const LocatedFileReference* dependency_at(size_t byteOffset) const
    {
        const auto offset = active->text.to_u16(byteOffset);
        for (const auto& ref : active->dependencies.locations)
            if (ref.contentStart <= offset && offset <= ref.contentEnd) return &ref;
        return nullptr;
    }
    Entry* resolve_dependency(const LocatedFileReference& ref) const
    {
        const auto& name = ref.reference.fileName;
        if (name.empty() || name.find('\0') != std::string::npos) return nullptr;
        if (ref.kind == FileReferenceKind::Import) return resolve(*active, name);
        if (ref.kind != FileReferenceKind::Path) return nullptr; // no implicit @types/lib installation
        const auto path = (utf8_path(active->path).parent_path() / utf8_path(name)).lexically_normal().generic_u8string();
        const auto key = path_key(std::string_view(reinterpret_cast<const char*>(path.data()), path.size()));
        const auto found = entries.find(key);
        return found == entries.end() ? nullptr : found->second.get();
    }
    static std::string escape_path_literal(std::string_view value, char16_t delimiter)
    {
        std::string escaped;
        for (size_t i = 0; i < value.size(); ++i)
        {
            const auto c = value[i];
            if (c == '\n') escaped += "\\n";
            else if (c == '\r') escaped += "\\r";
            else if (c == '\t') escaped += "\\t";
            else
            {
                if (c == '\\' || c == delimiter || (delimiter == '`' && c == '$' && i + 1 < value.size() && value[i + 1] == '{')) escaped += '\\';
                escaped += c;
            }
        }
        return escaped;
    }
    std::string complete_dependency(const LocatedFileReference& ref, size_t byteOffset) const
    {
        using namespace mn::language;
        if (ref.kind != FileReferenceKind::Import && ref.kind != FileReferenceKind::Path) return "{\"items\":[]}";
        const auto offset = active->text.to_u16(byteOffset);
        const auto prefix = active->text.slice(ref.contentStart, offset);
        // Raw escape sequences cannot be prefix-compared to decoded filenames.
        // Navigation still resolves their cooked name, but completion never
        // guesses an unsafe partial escape replacement.
        if (prefix.find('\\') != std::string::npos) return "{\"items\":[]}";
        if (!prefix.empty() && ref.kind == FileReferenceKind::Import && prefix != "." &&
            !starts(prefix, "./") && prefix != ".." && !starts(prefix, "../")) return "{\"items\":[]}";
        const auto base = utf8_path(active->path).parent_path();
        std::map<std::string, std::pair<bool, std::string>> choices;
        for (const auto& [path, entry] : entries)
        {
            if (entry.get() == active) continue;
            auto relative = utf8_path(path).lexically_relative(base).generic_u8string();
            if (relative.empty()) continue;
            std::string name(reinterpret_cast<const char*>(relative.data()), relative.size());
            const auto ext = utf8_path(path).extension().string();
            if (ext != ".ts" && ext != ".tsx" && ext != ".js" && ext != ".jsx" && ext != ".mts" && ext != ".cts" && ext != ".mjs" && ext != ".cjs") continue;
            if (!starts(name, "./") && !starts(name, "../") && !starts(prefix, "/") &&
                (ref.kind == FileReferenceKind::Import || starts(prefix, "."))) name = "./" + name;
            if (ref.kind == FileReferenceKind::Import)
            {
                // Existing subset resolver supports extensionless local imports.
                // Preserve an explicitly started extension; no tsconfig is guessed.
                const auto lastSlash = prefix.find_last_of('/');
                const auto basename = prefix.substr(lastSlash == std::string::npos ? 0 : lastSlash + 1);
                if (basename.find('.') == std::string::npos)
                {
                    if (name.ends_with(".d.ts")) name.resize(name.size() - 5);
                    else name.resize(name.size() - ext.size());
                }
            }
            if (!starts(name, prefix)) continue;
            const auto slash = name.find('/', prefix.size());
            if (slash != std::string::npos && slash + 1 < name.size())
            { choices.try_emplace(name.substr(0, slash + 1), true, "Workspace directory"); }
            else choices.try_emplace(name, false, entry->path);
        }
        const char16_t delimiter = ref.contentStart ? active->text.utf16[ref.contentStart - 1] : u'"';
        std::string items = "[";
        for (const auto& [name, description] : choices)
        {
            const auto insertion = ref.kind == FileReferenceKind::Path ? name : escape_path_literal(name, delimiter);
            if (ref.kind == FileReferenceKind::Path && name.find(static_cast<char>(delimiter)) != std::string::npos) continue;
            append(items, "{\"label\":" + quote(name) + ",\"kind\":" + quote(description.first ? "Folder" : "File") +
                ",\"insertText\":" + quote(insertion) + ",\"filterText\":" + quote(name) + ",\"sort\":" + quote(name) +
                ",\"detail\":" + quote(description.second) + ',' + range_json(active->text.to_byte(ref.contentStart), active->text.to_byte(ref.contentEnd)) + "}");
        }
        return "{\"items\":" + items + "]}";
    }
    void build(Entry& e, unsigned depth = 0)
    {
        if (e.state != 0) return; // Cycles are bounded; no recursive project parsing loop.
        if (depth >= 64) throw std::length_error("TypeScript module-depth budget exceeded.");
        e.state = 1;
        auto& d = e.declarations;
        // Preserve builtins, but parse local statements AFTER installing imported symbols
        // so initializers like `const v = makeWidget()` see the imported return type.
        d.symbols.erase(std::remove_if(d.symbols.begin(), d.symbols.end(), [](const Symbol& s) { return !s.builtin; }), d.symbols.end());
        d.scopes.resize(1); d.aliases.clear(); d.bases.clear(); d.declarationParens.clear(); d.functionBodies.clear();
        for (const auto& binding : e.imports)
            if (auto* dependency = resolve(e, binding.specifier); dependency && dependency != &e)
            { build(*dependency, depth + 1); bind(binding, *dependency, d); }
        d.parse_statements(0, d.tokens.size(), 0, 0);
        for (const auto& ex : e.exportSpecs)
            if (ex.specifier.empty())
                if (const auto* local = d.lookup(ex.local, 0, e.text.utf16.size()); local && !local->builtin)
                { auto symbol = *local; symbol.type = qualify(e, symbol.type); e.exports[ex.exported] = std::move(symbol); }
        // Explicit re-exports override star exports. Ambiguous star names are omitted.
        std::map<std::string, Symbol> stars;
        std::unordered_set<std::string> ambiguous;
        for (const auto& ex : e.exportSpecs)
        {
            if (ex.specifier.empty()) continue;
            auto* dependency = resolve(e, ex.specifier);
            if (!dependency || dependency == &e) continue;
            build(*dependency, depth + 1); copy_shapes(*dependency, d);
            if (ex.exported == "*")
            {
                for (const auto& [name, symbol] : dependency->exports)
                {
                    if (name == "default") continue;
                    if (const auto old = stars.find(name); old != stars.end() &&
                        (old->second.sourceFile != symbol.sourceFile || old->second.sourceStart != symbol.sourceStart)) ambiguous.insert(name);
                    else stars[name] = symbol;
                }
            }
            else if (const auto found = dependency->exports.find(ex.local); found != dependency->exports.end()) e.exports[ex.exported] = found->second;
        }
        for (const auto& [name, symbol] : stars) if (!ambiguous.contains(name)) e.exports.try_emplace(name, symbol);
        e.state = 2;
    }
public:
    ModuleGraph(const mn_language_document* documents, uint32_t count)
    {
        if (count > 256) throw std::length_error("TypeScript snapshot project exceeds 256 documents.");
        for (uint32_t i = 0; i < count; ++i)
        {
            const auto key = path_key(documents[i].path);
            if (entries.contains(key)) throw std::invalid_argument("Duplicate TypeScript document path.");
            auto entry = std::make_unique<Entry>(key, std::string_view(documents[i].utf8 ? documents[i].utf8 : "", documents[i].byte_length));
            read_module_syntax(*entry);
            if (i == 0) active = entry.get();
            entries.emplace(key, std::move(entry));
        }
        build(*active);
    }
    std::string request(uint32_t operation, size_t offset) const
    {
        if (const auto* ref = dependency_at(offset))
        {
            if (operation == MN_LANGUAGE_COMPLETION) return complete_dependency(*ref, offset);
            if (operation == MN_LANGUAGE_DEFINITION)
            {
                if (const auto* target = resolve_dependency(*ref))
                    return "{\"locations\":[{\"file\":" + mn::language::quote(target->path) + ",\"start\":0,\"end\":0}]}";
                return "{\"locations\":[]}";
            }
        }
        switch (operation)
        {
        case MN_LANGUAGE_COMPLETION: return active->declarations.complete(offset);
        case MN_LANGUAGE_DIAGNOSTICS: return active->declarations.diagnostics();
        case MN_LANGUAGE_SIGNATURE: return active->declarations.signature(offset);
        case MN_LANGUAGE_SIGNATURE_MANUAL: return active->declarations.signature(offset, true);
        case MN_LANGUAGE_FOLDING: return active->declarations.folding();
        case MN_LANGUAGE_HOVER: return active->declarations.location(offset, active->path, true);
        case MN_LANGUAGE_DEFINITION: return active->declarations.location(offset, active->path, false);
        default: throw std::invalid_argument("Unsupported native TypeScript operation.");
        }
    }
};
}
