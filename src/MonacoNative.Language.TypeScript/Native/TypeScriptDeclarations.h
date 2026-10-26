#pragma once
#include "TypeScriptScanner.h"
#include "TypeScriptBuiltins.h"
#include "TypeScriptBasicEditing.h"
#include <set>
#include <cctype>
#include <unordered_set>

namespace mn::typescript
{
// Structural, single-document declaration service on the ported upstream lexer.
// This is NOT TypeScript's binder/checker. Unsupported inference stays unknown;
// it must not fabricate members or pretend to validate assignability/modules.
struct Scope { size_t start, end; int parent; std::string owner; };
struct Parameter
{
    std::string name, type;
    size_t token = 0;
    bool optional = false, rest = false, bindingPattern = false;
};
struct Symbol
{
    std::string name, kind, type, owner, detail;
    size_t token = 0;
    int scope = 0;
    bool isStatic = false, hidden = false, method = false, builtin = false, imported = false;
    std::string sourceFile;
    size_t sourceStart = 0, sourceEnd = 0;
    std::vector<Parameter> parameters;
    bool hasBody = false;
};
struct Receiver { std::string type; bool isStatic = false; };
class Declarations
{
    friend class ModuleGraph;
    using Text = mn::language::Text;
    const Text& text;
    std::string sourceFile;
    std::vector<Token> tokens;
    std::vector<size_t> mate;
    std::vector<std::pair<size_t, size_t>> suppressed;
    std::vector<Token> comments;
    std::unordered_set<size_t> declarationParens;
    std::vector<std::pair<size_t, size_t>> functionBodies;
    std::vector<Scope> scopes;
    std::vector<Symbol> symbols;
    std::unordered_map<std::string, std::string> bases;
    std::unordered_map<std::string, std::string> aliases;
    static constexpr size_t none = static_cast<size_t>(-1);
    int k(size_t i) const { return i < tokens.size() ? tokens[i].kind : sk::EndOfFileToken; }
    bool is_javascript() const
    { return sourceFile.ends_with(".js") || sourceFile.ends_with(".mjs") || sourceFile.ends_with(".cjs") || sourceFile.ends_with(".jsx"); }
    std::string spelling(size_t i) const
    {
        if (i >= tokens.size()) return {};
        const auto& t = tokens[i];
        return (t.kind == sk::Identifier || t.kind == sk::PrivateIdentifier || t.kind == sk::StringLiteral ||
            (t.kind >= sk::BreakKeyword && t.kind <= 165)) ? t.value : text.slice(t.start, t.end);
    }
    bool name(size_t i) const { return k(i) == sk::Identifier || k(i) == sk::PrivateIdentifier || k(i) == sk::StringLiteral || (k(i) >= 83 && k(i) <= 165); }
    size_t skip(size_t i, size_t end) const { return i < mate.size() && mate[i] != none && mate[i] > i ? std::min(mate[i] + 1, end) : i + 1; }
    size_t delimiter(size_t i, size_t end, std::initializer_list<int> stop) const
    {
        while (i < end && std::find(stop.begin(), stop.end(), k(i)) == stop.end()) i = skip(i, end);
        return i;
    }
    // Bounded automatic-semicolon boundary detection for ordinary declarations.
    // Delimited subexpressions are skipped; continuation tokens (., (, [, binary
    // operators, tagged templates) are never treated as a new statement here.
    // This is host recovery, not the upstream parser's complete ASI production.
    size_t statement_end(size_t begin, size_t end) const
    {
        for (size_t j = begin; j < end; j = skip(j, end))
        {
            if (j == begin || !(tokens[j].flags & tf::PrecedingLineBreak)) continue;
            const int before = k(j - 1), after = k(j);
            const bool completed = before == sk::Identifier || before == sk::ThisKeyword ||
                before == sk::NumericLiteral || before == sk::StringLiteral || before == sk::TrueKeyword ||
                before == sk::FalseKeyword || before == sk::NullKeyword || before == sk::CloseParenToken ||
                before == sk::CloseBracketToken || before == sk::CloseBraceToken || before == sk::TemplateTail ||
                before == sk::NoSubstitutionTemplateLiteral || before == sk::PlusPlusToken || before == sk::MinusMinusToken ||
                before == sk::StringKeyword || before == sk::NumberKeyword || before == sk::BooleanKeyword ||
                before == sk::AnyKeyword || before == sk::UnknownKeyword || before == sk::VoidKeyword || before == sk::NeverKeyword;
            const bool statement = after == sk::Identifier || after == sk::ThisKeyword || after == sk::NewKeyword ||
                after == sk::ConstKeyword || after == sk::LetKeyword || after == sk::VarKeyword || after == sk::FunctionKeyword ||
                after == sk::ClassKeyword || after == sk::ReturnKeyword || after == sk::IfKeyword || after == sk::ForKeyword ||
                after == sk::WhileKeyword || after == sk::ThrowKeyword || after == sk::ExportKeyword || after == sk::ImportKeyword;
            if (completed && statement) return j;
        }
        return end;
    }
    std::string type_text(size_t begin, size_t end) const
    {
        if (begin >= end || begin >= tokens.size()) return {};
        auto result = text.slice(tokens[begin].start, tokens[end - 1].end);
        while (!result.empty() && std::isspace(static_cast<unsigned char>(result.back()))) result.pop_back();
        return result;
    }
    std::string canonical(std::string type) const
    {
        // Do not choose an arbitrary arm of a union or intersection.
        if (type.find('|') != std::string::npos || type.find('&') != std::string::npos) return {};
        if (type.size() >= 2 && type.substr(type.size() - 2) == "[]") return "Array";
        if (auto at = type.find('<'); at != std::string::npos) type.resize(at);
        for (unsigned n = 0; n < 16; ++n)
        {
            auto alias = aliases.find(type);
            if (alias == aliases.end() || alias->second == type) break;
            type = alias->second;
        }
        if (type == "string") return "String";
        if (type == "number") return "Number";
        if (type == "boolean") return "Boolean";
        return type;
    }
    int new_scope(size_t open, size_t close, int parent, std::string owner = {})
    {
        if (scopes.size() > 100000) throw std::length_error("TypeScript scope budget exceeded.");
        scopes.push_back({tokens[open].start, close < tokens.size() ? tokens[close].end : text.utf16.size(), parent, std::move(owner)});
        return static_cast<int>(scopes.size() - 1);
    }
    int scope_at(size_t position) const
    {
        int found = 0;
        for (size_t i = 1; i < scopes.size(); ++i)
            if (position >= scopes[i].start && position <= scopes[i].end && scopes[i].end - scopes[i].start <= scopes[found].end - scopes[found].start)
                found = static_cast<int>(i);
        return found;
    }
    const Symbol* lookup(std::string_view value, int scope, size_t position) const
    {
        for (int current = scope; current >= 0; current = scopes[current].parent)
        {
            const Symbol* match = nullptr;
            for (const auto& symbol : symbols)
                if (symbol.owner.empty() && symbol.scope == current && symbol.name == value &&
                    (symbol.builtin || symbol.imported || tokens[symbol.token].start <= position || symbol.kind == "Function" || symbol.kind == "Class" || symbol.kind == "Interface" || symbol.kind == "Enum")) match = &symbol;
            if (match) return match;
        }
        return nullptr;
    }
    const Symbol* member(std::string type, std::string_view value, bool isStatic, bool allowHidden = false) const
    {
        type = canonical(std::move(type));
        for (unsigned depth = 0; !type.empty() && depth < 32; ++depth)
        {
            for (const auto& symbol : symbols)
                if (symbol.owner == type && symbol.name == value && symbol.isStatic == isStatic && (allowHidden || !symbol.hidden)) return &symbol;
            auto base = bases.find(type); if (base == bases.end()) break; type = canonical(base->second);
        }
        return nullptr;
    }
    std::string infer(size_t i, size_t end, int scope, unsigned depth = 0)
    {
        if (i >= end || depth > 32) return {};
        if (k(i) == sk::StringLiteral || k(i) == sk::NoSubstitutionTemplateLiteral) return "string";
        if (k(i) == sk::NumericLiteral || ((k(i) == sk::MinusToken || k(i) == sk::PlusToken) && k(i + 1) == sk::NumericLiteral)) return "number";
        if (k(i) == sk::TrueKeyword || k(i) == sk::FalseKeyword) return "boolean";
        if (k(i) == sk::NewKeyword && name(i + 1))
        {
            std::string type = spelling(i + 1);
            for (size_t j = i + 2; j + 1 < end && k(j) == sk::DotToken && name(j + 1); j += 2)
                type += "." + spelling(j + 1);
            return type;
        }
        if (k(i) == sk::OpenBracketToken) return "Array";
        if (k(i) == sk::OpenBraceToken)
        {
            auto owner = "@object:" + std::to_string(tokens[i].start);
            const auto close = mate[i] == none ? end : mate[i];
            parse_members(i + 1, close, scope, owner, true, false, depth + 1);
            return owner;
        }
        if (k(i) == sk::Identifier)
        {
            auto last = i;
            while (last + 2 < end && (k(last + 1) == sk::DotToken || k(last + 1) == sk::QuestionDotToken) && name(last + 2)) last += 2;
            if (const auto* symbol = resolve(last, scope, tokens[i].start, depth + 1)) return symbol->type;
        }
        return {};
    }
    std::vector<Parameter> parameters(size_t begin, size_t end)
    {
        std::vector<Parameter> values;
        for (size_t i = begin; i < end;)
        {
            // A type annotation may contain a generic comma. This is delimiter
            // tracking only, not generic type inference or a package resolver.
            size_t next = i; unsigned angles = 0; bool annotation = false;
            for (; next < end; next = skip(next, end))
            {
                if (k(next) == sk::EqualsToken && angles == 0) annotation = false;
                if (k(next) == sk::ColonToken) annotation = true;
                if (annotation && k(next) == sk::LessThanToken) ++angles;
                else if (annotation && k(next) == sk::GreaterThanToken && angles) --angles;
                if (k(next) == sk::CommaToken && angles == 0) break;
            }
            auto id = i;
            bool rest = k(id) == sk::DotDotDotToken;
            if (rest) ++id;
            while (id < next && (k(id) == sk::PublicKeyword || k(id) == sk::PrivateKeyword ||
                k(id) == sk::ProtectedKeyword || k(id) == sk::ReadonlyKeyword)) ++id;
            const bool pattern = (k(id) == sk::OpenBraceToken || k(id) == sk::OpenBracketToken) && mate[id] != none;
            if (name(id) || pattern)
            {
                const auto nameEnd = pattern ? mate[id] + 1 : id + 1;
                auto colon = nameEnd;
                const bool optional = k(colon) == sk::QuestionToken;
                if (optional) ++colon;
                const auto equals = delimiter(colon, next, {sk::EqualsToken});
                auto type = k(colon) == sk::ColonToken ? type_text(colon + 1, equals) : std::string{};
                // Keep simple default-value types useful in plain JavaScript.
                if (type.empty() && equals < next)
                {
                    if (k(equals + 1) == sk::StringLiteral || k(equals + 1) == sk::NoSubstitutionTemplateLiteral) type = "string";
                    else if (k(equals + 1) == sk::NumericLiteral) type = "number";
                    else if (k(equals + 1) == sk::TrueKeyword || k(equals + 1) == sk::FalseKeyword) type = "boolean";
                }
                values.push_back({pattern ? type_text(id, nameEnd) : spelling(id), type, id, optional || equals < next, rest, pattern});
            }
            i = next < end ? next + 1 : end;
        }
        return values;
    }
    static std::string parameter_label(const Parameter& parameter)
    {
        std::string value = parameter.rest ? "..." : "";
        value += parameter.name;
        if (parameter.optional && !parameter.rest) value += '?';
        if (!parameter.type.empty()) value += ": " + parameter.type;
        return value;
    }
    std::string describe(const Symbol& symbol) const
    {
        std::string value = symbol.owner.empty() || symbol.owner[0] == '@' ? symbol.name : symbol.owner + "." + symbol.name;
        if (symbol.method)
        {
            value += '(';
            for (size_t n = 0; n < symbol.parameters.size(); ++n)
            {
                if (n) value += ", ";
                value += parameter_label(symbol.parameters[n]);
            }
            value += ')';
        }
        if (!symbol.type.empty() && symbol.type[0] != '@') value += ": " + symbol.type;
        return value;
    }
    void add(Symbol symbol)
    {
        if (symbols.size() >= 100000) throw std::length_error("TypeScript symbol budget exceeded.");
        if (!symbol.builtin && symbol.sourceFile.empty() && symbol.token < tokens.size())
        {
            symbol.sourceFile = sourceFile;
            symbol.sourceStart = text.to_byte(tokens[symbol.token].start);
            symbol.sourceEnd = text.to_byte(tokens[symbol.token].end);
        }
        symbol.detail = describe(symbol); symbols.push_back(std::move(symbol));
    }
    size_t callable(Symbol symbol, size_t open, size_t end, int scope, unsigned depth)
    {
        declarationParens.insert(open);
        const auto close = mate[open];
        if (close == none || close >= end) { add(std::move(symbol)); return open + 1; }
        symbol.method = symbol.kind != "Property"; symbol.parameters = parameters(open + 1, close);
        auto next = close + 1;
        if (k(next) == sk::ColonToken)
        {
            auto stop = delimiter(next + 1, end, {sk::OpenBraceToken, sk::SemicolonToken, sk::CommaToken});
            symbol.type = type_text(next + 1, stop); next = stop;
        }
        auto params = symbol.parameters; auto owner = symbol.owner;
        symbol.hasBody = k(next) == sk::OpenBraceToken;
        add(std::move(symbol));
        if (k(next) == sk::OpenBraceToken)
        {
            const auto bodyEnd = mate[next] == none ? end : mate[next];
            const auto bodyScope = new_scope(next, bodyEnd, scope, owner);
            functionBodies.emplace_back(tokens[next].start, bodyEnd < tokens.size() ? tokens[bodyEnd].end : text.utf16.size() + 1);
            for (const auto& parameter : params)
            {
                if (parameter.bindingPattern || parameter.name == "this") continue;
                Symbol local; local.name = parameter.name; local.type = parameter.type; local.kind = "Variable"; local.token = parameter.token; local.scope = bodyScope;
                add(std::move(local));
            }
            parse_statements(next + 1, bodyEnd, bodyScope, depth + 1);
            return bodyEnd < end ? bodyEnd + 1 : end;
        }
        return next;
    }
    // Declaration extraction for common arrow/function expressions. It deliberately
    // does not attempt overload inference, destructuring bindings or contextual typing.
    bool callable_initializer(Symbol& symbol, size_t begin, size_t end, int scope, unsigned depth)
    {
        if (begin >= end || depth > 96) return false;
        auto first = begin;
        if (k(first) == sk::AsyncKeyword) ++first;
        bool expression = k(first) == sk::FunctionKeyword;
        auto open = first;
        if (expression)
        {
            ++open;
            if (k(open) == sk::AsteriskToken) ++open;
            if (name(open)) ++open;
        }
        size_t afterParameters = open;
        std::vector<Parameter> params;
        if (k(open) == sk::OpenParenToken && mate[open] != none && mate[open] < end)
        {
            afterParameters = mate[open] + 1;
            params = parameters(open + 1, mate[open]);
        }
        else if (!expression && name(open) && k(open + 1) == sk::EqualsGreaterThanToken)
        {
            params.push_back({spelling(open), "", open});
            afterParameters = open + 1;
        }
        else return false;
        auto body = afterParameters;
        std::string resultType;
        if (k(body) == sk::ColonToken)
        {
            const auto stop = delimiter(body + 1, end, expression
                ? std::initializer_list<int>{sk::OpenBraceToken}
                : std::initializer_list<int>{sk::EqualsGreaterThanToken});
            resultType = type_text(body + 1, stop); body = stop;
        }
        if (!expression)
        {
            if (k(body) != sk::EqualsGreaterThanToken) return false;
            ++body;
        }
        else if (k(body) != sk::OpenBraceToken) return false;
        if (k(open) == sk::OpenParenToken) declarationParens.insert(open);
        symbol.method = true; symbol.parameters = params; symbol.hasBody = true;
        if (symbol.owner.empty()) symbol.kind = "Function";
        // A class/object function-valued property is still a property in the UI.
        symbol.type = resultType;
        const auto bodyEnd = k(body) == sk::OpenBraceToken && mate[body] != none ? mate[body] : end;
        if (body >= tokens.size()) return true;
        const auto child = new_scope(body, bodyEnd, scope, symbol.owner);
        if (k(body) != sk::OpenBraceToken) scopes[child].end = end > body ? tokens[end - 1].end : tokens[body].end;
        functionBodies.emplace_back(tokens[body].start, k(body) == sk::OpenBraceToken
            ? (bodyEnd < tokens.size() ? tokens[bodyEnd].end : text.utf16.size() + 1) : scopes[child].end);
        for (const auto& parameter : params)
        {
            if (parameter.bindingPattern || parameter.name == "this") continue;
            Symbol local; local.name = parameter.name; local.kind = "Variable";
            local.type = parameter.type; local.token = parameter.token; local.scope = child;
            add(std::move(local));
        }
        if (k(body) == sk::OpenBraceToken) parse_statements(body + 1, bodyEnd, child, depth + 1);
        else if (symbol.type.empty()) symbol.type = infer(body, end, child, depth + 1);
        return true;
    }
    void parse_members(size_t begin, size_t end, int scope, const std::string& owner, bool object, bool enumeration, unsigned depth)
    {
        if (depth > 96) return;
        for (size_t i = begin; i < end;)
        {
            bool isStatic = enumeration, hidden = false;
            while (i < end && (k(i) == sk::StaticKeyword || k(i) == sk::PrivateKeyword || k(i) == sk::ProtectedKeyword || k(i) == sk::PublicKeyword || k(i) == sk::ReadonlyKeyword || k(i) == sk::AbstractKeyword || k(i) == sk::DeclareKeyword || k(i) == sk::OverrideKeyword || k(i) == sk::AsyncKeyword))
            { isStatic |= k(i) == sk::StaticKeyword; hidden |= k(i) == sk::PrivateKeyword || k(i) == sk::ProtectedKeyword; ++i; }
            const bool accessor = (k(i) == sk::GetKeyword || k(i) == sk::SetKeyword) && name(i + 1);
            if (accessor) ++i;
            if (!name(i)) { i = skip(i, end); continue; }
            Symbol symbol; symbol.name = spelling(i); symbol.kind = enumeration ? "EnumMember" : "Property";
            symbol.owner = owner; symbol.scope = scope; symbol.token = i; symbol.isStatic = isStatic;
            symbol.hidden = hidden || k(i) == sk::PrivateIdentifier;
            if (enumeration) symbol.type = owner;
            auto next = i + 1;
            if (k(next) == sk::QuestionToken || k(next) == sk::ExclamationToken) ++next;
            if (k(next) == sk::OpenParenToken)
            {
                symbol.kind = accessor ? "Property" : symbol.name == "constructor" ? "Constructor" : "Method";
                i = callable(std::move(symbol), next, end, scope, depth + 1); continue;
            }
            auto stop = delimiter(next, end, {sk::SemicolonToken, sk::CommaToken});
            // Property declarations can be separated by a newline without a semicolon.
            for (auto j = next; j < stop; j = skip(j, stop))
                if (j > next && (tokens[j].flags & tf::PrecedingLineBreak) && name(j)) { stop = j; break; }
            if (k(next) == sk::ColonToken)
            {
                if (object)
                {
                    if (!callable_initializer(symbol, next + 1, stop, scope, depth + 1))
                        symbol.type = infer(next + 1, stop, scope, depth + 1);
                }
                else
                {
                    auto equals = delimiter(next + 1, stop, {sk::EqualsToken});
                    symbol.type = type_text(next + 1, equals);
                }
            }
            else if (k(next) == sk::EqualsToken)
            {
                if (!callable_initializer(symbol, next + 1, stop, scope, depth + 1))
                    symbol.type = infer(next + 1, stop, scope, depth + 1);
            }
            else if (object)
                if (const auto* original = lookup(symbol.name, scope, tokens[i].start)) symbol.type = original->type;
            add(std::move(symbol));
            i = stop < end && (k(stop) == sk::SemicolonToken || k(stop) == sk::CommaToken) ? stop + 1 : std::max(stop, i + 1);
        }
    }
    void parse_statements(size_t begin, size_t end, int scope, unsigned depth)
    {
        if (depth > 96) return;
        for (size_t i = begin; i < end;)
        {
            const auto kind = k(i);
            if ((kind == sk::ClassKeyword || kind == sk::InterfaceKeyword || kind == sk::EnumKeyword) && name(i + 1))
            {
                const auto id = i + 1; auto open = delimiter(id + 1, end, {sk::OpenBraceToken, sk::SemicolonToken});
                Symbol symbol; symbol.name = spelling(id); symbol.type = symbol.name; symbol.kind = kind == sk::ClassKeyword ? "Class" : kind == sk::InterfaceKeyword ? "Interface" : "Enum"; symbol.token = id; symbol.scope = scope;
                add(symbol);
                for (size_t j = id + 1; j + 1 < open; ++j) if (k(j) == sk::ExtendsKeyword) { bases[symbol.name] = spelling(j + 1); break; }
                if (k(open) == sk::OpenBraceToken)
                {
                    const auto close = mate[open] == none ? end : mate[open];
                    parse_members(open + 1, close, scope, symbol.name, false, kind == sk::EnumKeyword, depth + 1);
                    i = close < end ? close + 1 : end;
                }
                else i = std::max(open, i + 1);
                continue;
            }
            if (kind == sk::TypeKeyword && name(i + 1) && k(i + 2) == sk::EqualsToken)
            {
                const auto id = i + 1, value = i + 3; auto stop = delimiter(value, end, {sk::SemicolonToken});
                Symbol symbol; symbol.name = spelling(id); symbol.type = symbol.name; symbol.kind = "Interface"; symbol.token = id; symbol.scope = scope; add(symbol);
                if (k(value) == sk::OpenBraceToken) parse_members(value + 1, mate[value] == none ? stop : mate[value], scope, symbol.name, false, false, depth + 1);
                else aliases[symbol.name] = type_text(value, stop);
                i = stop < end ? stop + 1 : end; continue;
            }
            if (kind == sk::FunctionKeyword && name(i + 1))
            {
                auto open = delimiter(i + 2, end, {sk::OpenParenToken, sk::OpenBraceToken, sk::SemicolonToken});
                if (k(open) == sk::OpenParenToken)
                {
                    Symbol symbol; symbol.name = spelling(i + 1); symbol.kind = "Function"; symbol.token = i + 1; symbol.scope = scope;
                    i = callable(std::move(symbol), open, end, scope, depth + 1); continue;
                }
            }
            if ((kind == sk::ConstKeyword || kind == sk::LetKeyword || kind == sk::VarKeyword) && name(i + 1))
            {
                ++i;
                while (i < end && name(i))
                {
                    Symbol symbol; symbol.name = spelling(i); symbol.kind = kind == sk::ConstKeyword ? "Constant" : "Variable"; symbol.scope = scope; symbol.token = i;
                    auto stop = delimiter(i + 1, end, {sk::SemicolonToken, sk::CommaToken});
                    stop = statement_end(i + 1, stop);
                    const auto equals = delimiter(i + 1, stop, {sk::EqualsToken});
                    if (k(i + 1) == sk::ColonToken) symbol.type = type_text(i + 2, equals);
                    if (equals < stop && !callable_initializer(symbol, equals + 1, stop, scope, depth + 1) && symbol.type.empty())
                        symbol.type = infer(equals + 1, stop, scope, depth + 1);
                    add(std::move(symbol));
                    if (k(stop) == sk::CommaToken) { i = stop + 1; continue; }
                    i = stop < end && k(stop) == sk::SemicolonToken ? stop + 1 : stop; break;
                }
                continue;
            }
            if (is_javascript() && kind == sk::ThisKeyword && k(i + 1) == sk::DotToken && name(i + 2) && k(i + 3) == sk::EqualsToken)
            {
                const auto target = receiver(i, scope, tokens[i].start);
                if (!target.type.empty() && !member(target.type, spelling(i + 2), false, true))
                {
                    const auto stop = delimiter(i + 4, end, {sk::SemicolonToken});
                    Symbol property; property.name = spelling(i + 2); property.kind = "Property";
                    property.owner = target.type; property.scope = scope; property.token = i + 2;
                    property.type = infer(i + 4, stop, scope, depth + 1); add(std::move(property));
                }
            }
            if (kind == sk::OpenBraceToken)
            {
                auto close = mate[i] == none ? end : mate[i];
                auto block = new_scope(i, close, scope); parse_statements(i + 1, close, block, depth + 1);
                i = close < end ? close + 1 : end; continue;
            }
            // Walk into conditions/loop headers, but not opaque string/template tokens.
            ++i;
        }
    }
    Receiver receiver(size_t i, int scope, size_t position, unsigned depth = 0) const
    {
        if (i >= tokens.size() || depth > 32) return {};
        if (k(i) == sk::ThisKeyword)
        {
            for (int current = scope; current >= 0; current = scopes[current].parent)
                if (!scopes[current].owner.empty()) return {scopes[current].owner, false};
            return {};
        }
        if (k(i) == sk::StringLiteral || k(i) == sk::NoSubstitutionTemplateLiteral) return {"String", false};
        if (k(i) == sk::NumericLiteral) return {"Number", false};
        if (k(i) == sk::CloseParenToken && mate[i] != none)
        {
            auto open = mate[i]; if (!open) return {};
            if (open >= 2 && k(open - 2) == sk::NewKeyword) return {spelling(open - 1), false};
            if (const auto* symbol = resolve(open - 1, scope, position, depth + 1)) return {symbol->type, false};
            return {};
        }
        if (name(i))
        {
            if (const auto* symbol = resolve(i, scope, position, depth + 1))
                return {symbol->type, symbol->kind == "Class" || symbol->kind == "Enum"};
        }
        return {};
    }
    const Symbol* resolve(size_t i, int scope, size_t position, unsigned depth = 0) const
    {
        if (depth > 32 || i >= tokens.size()) return nullptr;
        if (i >= 2 && (k(i - 1) == sk::DotToken || k(i - 1) == sk::QuestionDotToken))
        {
            auto target = receiver(i - 2, scope, position, depth + 1);
            return member(target.type, spelling(i), target.isStatic, k(i - 2) == sk::ThisKeyword);
        }
        return lookup(spelling(i), scope, position);
    }
    size_t token_at(size_t position) const
    {
        for (size_t i = 0; i < tokens.size(); ++i)
            if (tokens[i].start <= position && position <= tokens[i].end && name(i)) return i;
        return none;
    }
    bool is_suppressed(size_t position) const
    {
        return std::any_of(suppressed.begin(), suppressed.end(), [position](auto span) { return position > span.first && position < span.second; });
    }
public:
    std::vector<ScanError> errors;
    explicit Declarations(const Text& source, std::string file = {}) : text(source), sourceFile(std::move(file))
    {
        Scanner scanner(text);
        unsigned pendingFlags = 0;
        std::vector<size_t> templateDepth;
        size_t braces = 0;
        int previous = sk::Unknown;
        std::vector<bool> controlParentheses;
        bool closedControl = false;
        for (size_t budget = 0; budget < 1000000; ++budget)
        {
            auto token = scanner.scan();
            if (token.kind >= sk::SingleLineCommentTrivia && token.kind <= sk::NonTextFileMarkerTrivia)
            {
                pendingFlags |= token.flags & (tf::PrecedingLineBreak | tf::PrecedingJSDocComment);
                if (token.kind == sk::SingleLineCommentTrivia || token.kind == sk::MultiLineCommentTrivia || token.kind == sk::ShebangTrivia)
                {
                    comments.push_back(token);
                    suppressed.emplace_back(token.start, token.end + ((token.flags & tf::Unterminated) || token.kind == sk::SingleLineCommentTrivia ? 1 : 0));
                }
                continue;
            }
            token.flags |= pendingFlags; pendingFlags = 0;
            if (token.kind == sk::CloseBraceToken && !templateDepth.empty() && braces == templateDepth.back())
            { token = scanner.rescan_template(); if (token.kind == sk::TemplateTail) templateDepth.pop_back(); }
            else if (token.kind == sk::OpenBraceToken) ++braces;
            else if (token.kind == sk::CloseBraceToken && braces) --braces;
            if (token.kind == sk::TemplateHead) templateDepth.push_back(braces);
            // The upstream scanner delegates slash-vs-division to its parser.
            // This basic host recognizes expression starts and control headers;
            // it does not claim to implement the full JavaScript grammar.
            const bool regexStart = closedControl || previous == sk::Unknown || previous == sk::EqualsToken ||
                previous == sk::OpenParenToken || previous == sk::OpenBracketToken || previous == sk::OpenBraceToken ||
                previous == sk::CommaToken || previous == sk::SemicolonToken || previous == sk::ReturnKeyword ||
                previous == sk::ThrowKeyword || previous == sk::CaseKeyword || previous == sk::ColonToken ||
                previous == sk::EqualsGreaterThanToken || previous == sk::ExclamationToken || previous == sk::QuestionToken ||
                previous == sk::AmpersandAmpersandToken || previous == sk::BarBarToken || previous == sk::QuestionQuestionToken ||
                previous == sk::TemplateHead || previous == sk::TemplateMiddle;
            if ((token.kind == sk::SlashToken || token.kind == sk::SlashEqualsToken) && regexStart) token = scanner.rescan_slash();
            closedControl = false;
            if (token.kind == sk::OpenParenToken) controlParentheses.push_back(previous == sk::IfKeyword || previous == sk::WhileKeyword ||
                previous == sk::ForKeyword || previous == sk::WithKeyword || previous == sk::SwitchKeyword || previous == sk::CatchKeyword);
            else if (token.kind == sk::CloseParenToken && !controlParentheses.empty())
            { closedControl = controlParentheses.back(); controlParentheses.pop_back(); }
            if (token.kind == sk::EndOfFileToken) break;
            if (token.kind == sk::StringLiteral || token.kind == sk::RegularExpressionLiteral || (token.kind >= sk::NoSubstitutionTemplateLiteral && token.kind <= sk::TemplateTail)) suppressed.emplace_back(token.start, token.end + ((token.flags & tf::Unterminated) ? 1 : 0));
            tokens.push_back(token); previous = token.kind;
            if (budget == 999999) throw std::length_error("TypeScript token budget exceeded.");
        }
        errors = scanner.errors;
        mate.assign(tokens.size(), none);
        std::vector<size_t> stack;
        for (size_t i = 0; i < tokens.size(); ++i)
        {
            const auto kind = k(i);
            if (kind == sk::TemplateHead || kind == sk::OpenBraceToken || kind == sk::OpenParenToken || kind == sk::OpenBracketToken) stack.push_back(i);
            else if (kind == sk::TemplateMiddle || kind == sk::TemplateTail || kind == sk::CloseBraceToken || kind == sk::CloseParenToken || kind == sk::CloseBracketToken)
            {
                const int expected = kind == sk::TemplateTail || kind == sk::TemplateMiddle ? sk::TemplateHead :
                    kind == sk::CloseBraceToken ? sk::OpenBraceToken : kind == sk::CloseParenToken ? sk::OpenParenToken : sk::OpenBracketToken;
                auto found = std::find_if(stack.rbegin(), stack.rend(), [&](size_t n) { return k(n) == expected; });
                if (found == stack.rend())
                {
                    if (kind != sk::TemplateMiddle && kind != sk::TemplateTail)
                        errors.push_back({tokens[i].start, tokens[i].end, "Unexpected closing delimiter '" + spelling(i) + "'."});
                    continue;
                }
                const auto opening = *found;
                while (stack.back() != opening)
                {
                    const auto unclosed = stack.back(); stack.pop_back();
                    errors.push_back({tokens[unclosed].start, tokens[unclosed].end, "Unclosed delimiter '" + spelling(unclosed) + "'."});
                }
                if (kind != sk::TemplateMiddle)
                { mate[i] = opening; mate[opening] = i; stack.pop_back(); }
            }
        }
        for (auto opening : stack)
            errors.push_back({tokens[opening].start, tokens[opening].end, "Unclosed delimiter '" + spelling(opening) + "'."});
        scopes.push_back({0, text.utf16.size(), -1, ""});
        for (const auto& data : builtinMembers)
        {
            Symbol symbol; symbol.name = data.name; symbol.owner = data.owner; symbol.kind = data.kind; symbol.type = data.type;
            symbol.builtin = true; symbol.method = data.kind == "Method";
            for (const auto& parameter : data.parameters)
            {
                Parameter p{std::string(parameter.first), std::string(parameter.second), 0};
                // Selected parameter modifiers from the same pinned lib.es5/lib.dom
                // declarations as builtinMembers; not inferred from runtime values.
                p.rest = (data.owner == "Console" && p.name == "data") ||
                    (data.owner == "Math" && (data.name == "max" || data.name == "min")) ||
                    (data.owner == "Array" && (data.name == "push" || data.name == "unshift" || data.name == "concat" || data.name == "splice") && p.name == "items") ||
                    (data.owner == "String" && data.name == "concat");
                p.optional = (data.owner == "Console" && !p.rest &&
                        (p.name == "condition" || p.name == "label" || p.name == "options" || p.name == "properties" || p.name == "tabularData")) ||
                    ((data.owner == "String" || data.owner == "Array") && (p.name == "thisArg" || p.name == "position" || p.name == "end")) ||
                    (data.owner == "Array" && data.name == "slice" && p.name == "start") ||
                    (data.owner == "String" && data.name == "split" && p.name == "limit") ||
                    (data.owner == "Array" && data.name == "splice" && data.parameters.size() == 2 && p.name == "deleteCount");
                symbol.parameters.push_back(std::move(p));
            }
            add(std::move(symbol));
        }
        const std::pair<std::string_view, std::string_view> globals[] = {
            {"console", "Console"}, {"Math", "Math"}, {"JSON", "JSON"}, {"String", "StringConstructor"},
            {"Number", "NumberConstructor"}, {"Boolean", "BooleanConstructor"}, {"Object", "ObjectConstructor"},
            {"Array", "ArrayConstructor"}, {"Promise", "PromiseConstructor"}, {"Map", "MapConstructor"}, {"Set", "SetConstructor"},
            {"RegExp", "RegExpConstructor"}
        };
        for (const auto& [name, type] : globals)
        {
            Symbol symbol; symbol.name = name; symbol.type = type; symbol.kind = "Variable"; symbol.builtin = true; add(std::move(symbol));
        }
        parse_statements(0, tokens.size(), 0, 0);
    }
    std::string complete(size_t offset) const
    {
        using namespace mn::language;
        auto position = text.to_u16(offset);
        if (is_suppressed(position)) return "{\"items\":[]}";
        auto [start, end] = word_range(text.utf8, offset);
        auto prefixStart = text.to_u16(start);
        auto scope = scope_at(position);
        size_t previous = none;
        for (size_t i = 0; i < tokens.size() && tokens[i].end <= prefixStart; ++i) previous = i;
        const bool memberAccess = previous != none && (k(previous) == sk::DotToken || k(previous) == sk::QuestionDotToken);
        Receiver target;
        if (memberAccess && previous) target = receiver(previous - 1, scope, position);
        auto targetType = canonical(target.type);
        std::vector<const Symbol*> candidates;
        std::unordered_set<std::string> seen;
        if (memberAccess)
        {
            bool allowHidden = previous && k(previous - 1) == sk::ThisKeyword;
            for (unsigned depth = 0; !targetType.empty() && depth < 32; ++depth)
            {
                for (const auto& s : symbols) if (s.owner == targetType && s.isStatic == target.isStatic && (allowHidden || !s.hidden) && s.kind != "Constructor" && seen.insert(s.name + s.detail).second) candidates.push_back(&s);
                auto base = bases.find(targetType); if (base == bases.end()) break; targetType = canonical(base->second);
            }
        }
        else
        {
            for (int current = scope; current >= 0; current = scopes[current].parent)
                for (auto it = symbols.rbegin(); it != symbols.rend(); ++it)
                {
                    const auto& s = *it;
                    if (s.owner.empty() && s.scope == current && (s.builtin || s.imported || tokens[s.token].start <= position || s.kind == "Function" || s.kind == "Class" || s.kind == "Interface" || s.kind == "Enum") && seen.insert(s.name).second) candidates.push_back(&s);
                }
        }
        std::string items = "[";
        for (const auto* s : candidates)
        {
            std::string snippet = snippet_escape(s->name);
            auto afterWord = text.to_u16(end);
            while (afterWord < text.utf16.size() && (text.utf16[afterWord] == ' ' || text.utf16[afterWord] == '\t')) ++afterWord;
            const bool existingCall = afterWord < text.utf16.size() && text.utf16[afterWord] == '(';
            if (s->method && !existingCall)
            {
                snippet += '(';
                size_t placeholder = 0;
                for (const auto& parameter : s->parameters)
                {
                    if (parameter.name == "this") continue;
                    if (placeholder) snippet += ", ";
                    snippet += "${" + std::to_string(++placeholder) + ":" + snippet_escape(parameter.bindingPattern ? "value" : parameter.name) + "}";
                }
                snippet += ")$0";
            }
            append(items, "{\"label\":" + quote(s->name) + ",\"kind\":" + quote(s->kind) + ",\"detail\":" + quote(s->detail) + ",\"snippet\":" + quote(snippet) + ",\"sort\":" + quote("0:" + s->name) +
                (s->method && !existingCall && !s->parameters.empty() ? ",\"commandId\":\"editor.action.triggerParameterHints\"" : "") + ',' + range_json(start, end) + "}");
        }
        const bool javascript = is_javascript();
        static const std::unordered_set<std::string_view> jsKeywords = {
            "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default",
            "delete", "do", "else", "export", "extends", "false", "finally", "for", "from", "function",
            "if", "import", "in", "instanceof", "let", "new", "null", "of", "return", "static", "super",
            "switch", "this", "throw", "true", "try", "typeof", "undefined", "var", "void", "while", "yield"
        };
        if (!memberAccess)
            for (const auto& entry : keywordTokens)
                if ((!javascript || jsKeywords.contains(entry.first)) && !seen.contains(std::string(entry.first))) append(items, "{\"label\":" + quote(entry.first) + ",\"kind\":\"Keyword\",\"sort\":" + quote("1:" + std::string(entry.first)) + ',' + range_json(start, end) + "}");
        return "{\"items\":" + items + "]}";
    }
    std::string location(size_t offset, std::string_view file, bool hover) const
    {
        using namespace mn::language;
        const auto position = text.to_u16(offset), token = token_at(position);
        if (token == none || is_suppressed(position)) return hover ? "{}" : "{\"locations\":[]}";
        auto* symbol = resolve(token, scope_at(position), position);
        if (!symbol) return hover ? "{}" : "{\"locations\":[]}";
        if (hover) return "{\"hover\":" + quote("```typescript\n" + symbol->detail + "\n```") + ',' + range_json(text.to_byte(tokens[token].start), text.to_byte(tokens[token].end)) + "}";
        if (symbol->builtin) return "{\"locations\":[]}";
        return "{\"locations\":[{\"file\":" + quote(symbol->sourceFile.empty() ? file : std::string_view(symbol->sourceFile)) + ',' +
            range_json(symbol->sourceStart, symbol->sourceEnd) + "}]}";
    }
    std::string signature(size_t offset, bool manuallyInvoked = false) const
    {
        using namespace mn::language;
        const auto position = text.to_u16(offset);
        if (!manuallyInvoked && is_suppressed(position)) return "{}";
        for (size_t open = tokens.size(); open-- > 0;)
        {
            if (k(open) != sk::OpenParenToken || tokens[open].end > position) continue;
            const auto close = mate[open];
            if (close != none && position > tokens[close].start) continue;
            if (declarationParens.contains(open) || (close != none && k(close + 1) == sk::EqualsGreaterThanToken)) return "{}";
            if (!open) continue;
            if (k(open - 1) == sk::FunctionKeyword || (open >= 2 && name(open - 1) && k(open - 2) == sk::FunctionKeyword)) return "{}";
            // Do not resurrect an enclosing call while editing a callback body.
            // Anonymous inline callbacks are intentionally not global declarations.
            if (std::any_of(functionBodies.begin(), functionBodies.end(), [&](auto body) {
                return body.first > tokens[open].start && body.first < position && position < body.second;
            })) return "{}";
            for (size_t j = open + 1; j < tokens.size() && tokens[j].start < position; ++j)
            {
                size_t body = none;
                if (k(j) == sk::EqualsGreaterThanToken && k(j + 1) == sk::OpenBraceToken) body = j + 1;
                if (k(j) == sk::FunctionKeyword)
                {
                    auto parameters = j + 1;
                    if (k(parameters) == sk::AsteriskToken) ++parameters;
                    if (name(parameters)) ++parameters;
                    if (k(parameters) == sk::OpenParenToken && mate[parameters] != none && k(mate[parameters] + 1) == sk::OpenBraceToken)
                        body = mate[parameters] + 1;
                }
                if (body != none && tokens[body].end <= position &&
                    (mate[body] == none || position < tokens[mate[body]].end)) return "{}";
            }
            auto callee = open - 1;
            if (k(callee) == sk::QuestionDotToken && callee) --callee;
            if (!name(callee)) continue; // grouping/control-flow, not a supported call
            size_t first = callee;
            while (first >= 2 && (k(first - 1) == sk::DotToken || k(first - 1) == sk::QuestionDotToken) && name(first - 2)) first -= 2;
            const bool construction = first > 0 && k(first - 1) == sk::NewKeyword;
            const auto* target = resolve(callee, scope_at(position), position);
            if (!target) return "{}"; // Never guess signatures from a same-name unrelated symbol.
            std::vector<const Symbol*> candidates;
            Symbol defaultConstructor;
            if (construction && target->kind == "Class")
            {
                const auto type = canonical(target->type);
                for (const auto& symbol : symbols)
                    if (symbol.owner == type && symbol.kind == "Constructor" && !symbol.hidden) candidates.push_back(&symbol);
                if (candidates.empty())
                {
                    const bool inaccessible = std::any_of(symbols.begin(), symbols.end(), [&](const auto& s) {
                        return s.owner == type && s.kind == "Constructor";
                    });
                    if (inaccessible) return "{}";
                    defaultConstructor.name = target->name; defaultConstructor.method = true;
                    candidates.push_back(&defaultConstructor);
                }
            }
            else if (target->method)
            {
                for (const auto& symbol : symbols)
                    if (symbol.name == target->name && symbol.owner == target->owner && symbol.scope == target->scope &&
                        symbol.isStatic == target->isStatic && symbol.method && symbol.sourceFile == target->sourceFile)
                        candidates.push_back(&symbol);
            }
            if (candidates.empty()) return "{}";
            // In an overload set, the implementation signature is not public.
            if (candidates.size() > 1 && std::any_of(candidates.begin(), candidates.end(), [](auto s) { return !s->hasBody && !s->builtin; }))
                std::erase_if(candidates, [](auto s) { return s->hasBody; });

            std::vector<ArgumentChild> arguments;
            const auto stop = close == none ? tokens.size() : close;
            for (size_t i = open + 1; i < stop;)
            {
                if (k(i) == sk::CommaToken)
                { arguments.push_back({ArgumentKind::Comma, tokens[i].start, tokens[i].end}); ++i; continue; }
                const auto begin = i;
                while (i < stop && k(i) != sk::CommaToken && k(i) != sk::SemicolonToken) i = skip(i, stop);
                if (i == begin) break;
                arguments.push_back({k(begin) == sk::DotDotDotToken ? ArgumentKind::Spread : ArgumentKind::Expression,
                    tokens[begin].start, tokens[i - 1].end});
                if (k(i) == sk::SemicolonToken) break;
            }
            std::optional<size_t> child;
            for (size_t i = 0; i < arguments.size(); ++i)
            {
                if (arguments[i].start > position) break;
                // At the left edge of a comma, the preceding argument still owns the caret.
                if (arguments[i].kind == ArgumentKind::Comma && arguments[i].end > position) break;
                child = i;
            }
            const auto argumentIndex = child ? argument_index_or_count(arguments, child) : 0;
            const auto argumentCount = argument_index_or_count(arguments);
            size_t selected = 0;
            auto visibleCount = [](const Symbol* s) {
                return static_cast<size_t>(std::count_if(s->parameters.begin(), s->parameters.end(), [](const auto& p) { return p.name != "this"; }));
            };
            // Arity-only selection for the basic service. This is NOT upstream
            // checker overload resolution; unknown types are not fabricated.
            bool fit = false;
            for (size_t i = 0; i < candidates.size(); ++i)
            {
                const auto* symbol = candidates[i];
                const bool rest = std::any_of(symbol->parameters.begin(), symbol->parameters.end(), [](const auto& p) { return p.rest; });
                if (rest || visibleCount(symbol) >= argumentCount) { selected = i; fit = true; break; }
            }
            if (!fit)
                for (size_t i = 1; i < candidates.size(); ++i)
                    if (visibleCount(candidates[i]) > visibleCount(candidates[selected])) selected = i;
            std::string signatures = "[";
            size_t activeParameter = argumentIndex;
            for (size_t i = 0; i < candidates.size(); ++i)
            {
                const auto* symbol = candidates[i];
                std::string label = construction ? "new " + spelling(callee) :
                    symbol->owner.empty() || symbol->owner[0] == '@' ? symbol->name : symbol->owner + "." + symbol->name;
                label += '(';
                std::string params = "[";
                size_t count = 0;
                std::optional<size_t> firstRest;
                for (const auto& parameter : symbol->parameters)
                {
                    if (parameter.name == "this") continue;
                    if (count) label += ", ";
                    if (parameter.rest && !firstRest) firstRest = count;
                    const auto parameterLabel = parameter_label(parameter);
                    label += parameterLabel;
                    append(params, "{\"label\":" + quote(parameterLabel) + ",\"optional\":" + (parameter.optional ? "true" : "false") + ",\"rest\":" + (parameter.rest ? "true" : "false") + "}");
                    ++count;
                }
                label += ')';
                if (!construction && !symbol->type.empty() && symbol->type[0] != '@') label += ": " + symbol->type;
                const auto highlighted = highlighted_parameter(argumentIndex, count, firstRest);
                if (i == selected) activeParameter = highlighted;
                append(signatures, "{\"label\":" + quote(label) + ",\"parameters\":" + params + "],\"activeParameter\":" +
                    std::to_string(highlighted) + ",\"isVariadic\":" + (firstRest ? "true" : "false") + "}");
            }
            return "{\"signatures\":" + signatures + "],\"activeSignature\":" + std::to_string(selected) + ",\"activeParameter\":" +
                std::to_string(activeParameter) + ",\"argumentIndex\":" + std::to_string(argumentIndex) + ",\"argumentCount\":" +
                std::to_string(argumentCount) + ',' + range_json(text.to_byte(tokens[open].end), close == none ? text.utf8.size() : text.to_byte(tokens[close].start)) + "}";
        }
        return "{}";
    }
    std::string folding() const
    {
        using namespace mn::language;
        struct Fold { size_t start, end; std::string kind; };
        std::vector<size_t> lines{0};
        for (size_t i = 0; i < text.utf16.size(); ++i)
            if (line_break(text.utf16[i]))
            {
                if (text.utf16[i] == '\r' && i + 1 < text.utf16.size() && text.utf16[i + 1] == '\n') ++i;
                lines.push_back(i + 1);
            }
        auto lineOf = [&](size_t p) { return static_cast<size_t>(std::upper_bound(lines.begin(), lines.end(), p) - lines.begin()); };
        std::vector<Fold> ranges;
        auto addRange = [&](size_t start, size_t end, std::string kind, bool excludeLast) {
            auto a = lineOf(start), b = lineOf(end);
            if (excludeLast && b > a) --b;
            if (b > a) ranges.push_back({a, b, std::move(kind)});
        };
        for (size_t i = 0; i < tokens.size(); ++i)
        {
            if (mate[i] != none && mate[i] > i && (k(i) == sk::OpenBraceToken || k(i) == sk::OpenBracketToken || k(i) == sk::OpenParenToken))
                addRange(tokens[i].start, tokens[mate[i]].start, "", true);
            else if (k(i) == sk::TemplateHead && mate[i] != none)
                addRange(tokens[i].start, tokens[mate[i]].end, "", false);
            else if (k(i) == sk::NoSubstitutionTemplateLiteral)
                addRange(tokens[i].start, tokens[i].end, "", false);
        }
        std::vector<size_t> regions;
        std::optional<Token> firstComment, lastComment;
        auto flush = [&]() {
            if (firstComment && lastComment && firstComment->start != lastComment->start)
                addRange(firstComment->start, lastComment->end, "comment", false);
            firstComment.reset(); lastComment.reset();
        };
        for (const auto& comment : comments)
        {
            const auto line = lineOf(comment.start);
            bool lineStart = true;
            for (size_t p = lines[line - 1]; p < comment.start; ++p)
                if (!space(text.utf16[p])) { lineStart = false; break; }
            if (comment.kind == sk::SingleLineCommentTrivia && lineStart)
            {
                auto marker = parse_region_delimiter(std::u16string_view(text.utf16).substr(comment.start, comment.end - comment.start));
                if (marker)
                {
                    flush();
                    if (marker->isStart) regions.push_back(comment.start);
                    else if (!regions.empty()) { addRange(regions.back(), comment.end, "region", false); regions.pop_back(); }
                    continue;
                }
                if (lastComment && line != lineOf(lastComment->end) + 1) flush();
                if (!firstComment) firstComment = comment;
                lastComment = comment;
            }
            else
            {
                flush();
                if (comment.kind == sk::MultiLineCommentTrivia) addRange(comment.start, comment.end, "comment", false);
            }
        }
        flush();
        std::sort(ranges.begin(), ranges.end(), [](const auto& a, const auto& b) {
            if (a.start != b.start) return a.start < b.start;
            if (a.end != b.end) return a.end > b.end;
            return a.kind > b.kind;
        });
        std::vector<Fold> accepted;
        std::vector<size_t> parents;
        for (auto& range : ranges)
        {
            if (!accepted.empty() && accepted.back().start == range.start) continue;
            while (!parents.empty() && range.start > parents.back()) parents.pop_back();
            if (!parents.empty() && range.end > parents.back()) continue; // no crossing model folds
            parents.push_back(range.end); accepted.push_back(std::move(range));
        }
        std::string result = "[";
        for (const auto& range : accepted)
            append(result, "{\"startLine\":" + std::to_string(range.start) + ",\"endLine\":" + std::to_string(range.end) +
                (range.kind.empty() ? "" : ",\"kind\":" + quote(range.kind)) + "}");
        return "{\"folding\":" + result + "]}";
    }
    std::string diagnostics() const
    {
        using namespace mn::language;
        std::string result = "[";
        for (const auto& error : errors) append(result, "{" + range_json(text.to_byte(error.start), text.to_byte(error.end)) + ",\"severity\":1,\"message\":" + quote(error.message) + "}");
        return "{\"diagnostics\":" + result + "]}";
    }
};
}
