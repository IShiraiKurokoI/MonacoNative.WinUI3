// Basic JS/TS host regression tests. The argument-count/rest-highlight and
// region-delimiter algorithms are adapted from TypeScript 5.8.3 (Apache-2.0).
// Cases below are project-authored specification/regression tests, NOT a claim
// that the upstream fourslash/checker suite has been ported. Execution is C++ only.
#include "../../src/MonacoNative.Language.TypeScript/Native/TypeScriptBasicEditing.h"
#include "TestHarness.h"
#include <functional>
#include <iostream>
#include <stdexcept>

namespace
{
using namespace mn::typescript;
using namespace mn::language;
std::string request(std::string source, uint32_t operation = MN_LANGUAGE_SIGNATURE, const char* path = "/workspace/basic.ts")
{
    const auto caret = source.find("/*caret*/");
    const auto offset = caret == std::string::npos ? source.size() : caret;
    if (caret != std::string::npos) source.erase(caret, 9);
    mn_language_document document{path, source.data(), source.size()};
    char* data = nullptr;
    const auto ok = mn_typescript_request(&document, 1, operation, offset, &data);
    const std::string output = data ? data : "";
    mn_language_free(data);
    if (!ok) throw std::runtime_error(output);
    return output;
}
void contains(const std::string& output, std::string_view expected)
{
    if (output.find(expected) == std::string::npos)
        throw std::runtime_error("Missing " + std::string(expected) + " in " + output);
}
void absent(const std::string& output, std::string_view unexpected)
{
    if (output.find(unexpected) != std::string::npos)
        throw std::runtime_error("Unexpected " + std::string(unexpected) + " in " + output);
}
void signature(std::string source, std::string_view label, size_t argument, size_t parameter, const char* path = "/workspace/basic.ts")
{
    const auto output = request(std::move(source), MN_LANGUAGE_SIGNATURE, path);
    contains(output, "\"label\":" + quote(label));
    contains(output, "\"argumentIndex\":" + std::to_string(argument) + ',');
    contains(output, "\"activeParameter\":" + std::to_string(parameter) + ',');
}
struct Runner
{
    int passed = 0, failed = 0;
    void run(const char* name, const std::function<void()>& test)
    {
        try { test(); ++passed; }
        catch (const std::exception& e) { ++failed; std::cerr << "[basic] " << name << ": " << e.what() << '\n'; }
    }
};
}
void TypeScriptBasicEditingFixtures()
{
    Runner r;
    const ArgumentChild expression{ArgumentKind::Expression}, comma{ArgumentKind::Comma}, spread{ArgumentKind::Spread};
    // Exact getArgumentIndexOrCount branches, including missing commas/arguments
    // and the unknown-spread behavior; not merely 'count every comma in source'.
    r.run("argument empty", [&] { MN_CHECK_EQ(argument_index_or_count({}), 0U); });
    r.run("argument singleton", [&] { MN_CHECK_EQ(argument_index_or_count({expression}), 1U); });
    r.run("argument pair", [&] { MN_CHECK_EQ(argument_index_or_count({expression, comma, expression}), 2U); });
    r.run("trailing comma", [&] { MN_CHECK_EQ(argument_index_or_count({expression, comma}), 2U); });
    r.run("missing arguments", [&] { MN_CHECK_EQ(argument_index_or_count({comma, comma}), 3U); });
    r.run("missing commas", [&] { MN_CHECK_EQ(argument_index_or_count({expression, expression, expression}), 3U); });
    r.run("current first", [&] { MN_CHECK_EQ(argument_index_or_count({expression, comma, expression}, 0), 0U); });
    r.run("current comma", [&] { MN_CHECK_EQ(argument_index_or_count({expression, comma, expression}, 1), 1U); });
    r.run("current last", [&] { MN_CHECK_EQ(argument_index_or_count({expression, comma, expression}, 2), 1U); });
    r.run("unknown spread", [&] { MN_CHECK_EQ(argument_index_or_count({spread, comma, expression}), 1U); });
    r.run("known tuple spread", [&] { MN_CHECK_EQ(argument_index_or_count({{ArgumentKind::Spread, 0, 0, 3}, comma, expression}), 4U); });
    r.run("rest tail", [&] { MN_CHECK_EQ(highlighted_parameter(9, 2, 1), 1U); });
    r.run("rest middle sentinel", [&] { MN_CHECK_EQ(highlighted_parameter(2, 3, 1), 3U); });
    r.run("extra nonrest sentinel", [&] { MN_CHECK_EQ(highlighted_parameter(4, 2, {}), 4U); });
    r.run("region trim", [&] { const auto value = parse_region_delimiter(u"  // #region Name  \r"); MN_CHECK(value && value->isStart && value->name == u"Name"); });
    r.run("region end", [&] { const auto value = parse_region_delimiter(u"//#endregion"); MN_CHECK(value && !value->isStart); });
    r.run("region upstream prefix", [&] { const auto value = parse_region_delimiter(u"//#regional"); MN_CHECK(value && value->name == u"al"); });
    r.run("region noncomment", [&] { MN_CHECK(!parse_region_delimiter(u"#region")); });
    r.run("region wrong case", [&] { MN_CHECK(!parse_region_delimiter(u"// #Region")); });

    const std::string f = "function add(a: number, b: number): number { return a + b; } ";
    r.run("first argument", [&] { signature(f + "add(/*caret*/)", "add(a: number, b: number): number", 0, 0); });
    r.run("next argument", [&] { signature(f + "add(1, /*caret*/)", "add(a: number, b: number): number", 1, 1); });
    r.run("unfinished call", [&] { signature(f + "add(1, /*caret*/", "add(a: number, b: number): number", 1, 1); });
    r.run("closed call", [&] { MN_CHECK_EQ(request(f + "add(1,2)/*caret*/"), "{}"); });
    r.run("before call", [&] { MN_CHECK_EQ(request(f + "ad/*caret*/d(1,2)"), "{}"); });
    r.run("unknown call", [&] { MN_CHECK_EQ(request(f + "missing(/*caret*/)"), "{}"); });
    r.run("unknown inner call", [&] { MN_CHECK_EQ(request(f + "add(missing(/*caret*/),2)"), "{}"); });
    r.run("declaration not invocation", [&] { MN_CHECK_EQ(request("function add(a: number, /*caret*/b: number): void {}"), "{}"); });
    r.run("control header", [&] { MN_CHECK_EQ(request("if (/*caret*/true) {}"), "{}"); });
    r.run("nested return to outer", [&] { signature(f + "add(add(1,2), /*caret*/)", "add(a: number, b: number): number", 1, 1); });
    r.run("nested inner argument", [&] { signature(f + "add(add(1,/*caret*/), 3)", "add(a: number, b: number): number", 1, 1); });
    r.run("string comma", [&] { signature(f + "add('one,two', /*caret*/)", "add(a: number, b: number): number", 1, 1); });
    r.run("array comma", [&] { signature(f + "add([1,2], /*caret*/)", "add(a: number, b: number): number", 1, 1); });
    r.run("object comma", [&] { signature(f + "add({a:1,b:2}, /*caret*/)", "add(a: number, b: number): number", 1, 1); });
    r.run("template substitution comma", [&] { signature(f + "add(`x${(1,2)}`, /*caret*/)", "add(a: number, b: number): number", 1, 1); });
    r.run("regexp comma", [&] { signature(f + "add(/a,b/, /*caret*/)", "add(a: number, b: number): number", 1, 1); });
    r.run("comment comma", [&] { signature(f + "add(1 /* , ignored */, /*caret*/)", "add(a: number, b: number): number", 1, 1); });
    r.run("automatic suppressed in string", [&] { MN_CHECK_EQ(request(f + "add('a/*caret*/b',2)"), "{}"); });
    r.run("manual inside string", [&] { contains(request(f + "add('a/*caret*/b',2)", MN_LANGUAGE_SIGNATURE_MANUAL), "\"argumentIndex\":0,"); });
    r.run("automatic suppressed in comment", [&] { MN_CHECK_EQ(request(f + "add(1, // /*caret*/\n2)"), "{}"); });
    r.run("optional and default", [&] { signature("function f(a = 'x', b?:number):void {} f('a', /*caret*/)", "f(a?: string, b?: number): void", 1, 1); });
    r.run("rest parameter", [&] { signature("function f(a:number, ...values:string[]):void{} f(1,'x','y', /*caret*/)", "f(a: number, ...values: string[]): void", 3, 1); });
    r.run("nonrest extra argument", [&] { signature(f + "add(1,2, /*caret*/)", "add(a: number, b: number): number", 2, 2); });
    r.run("console rest", [&] { signature("console.log('a',1,/*caret*/)", "Console.log(...data: any[]): void", 2, 0, "/workspace/basic.js"); });
    r.run("math rest", [&] { signature("Math.max(1,2,/*caret*/)", "Math.max(...values: number[]): number", 2, 0); });
    r.run("arrow parameters", [&] { signature("const f = (name: string, times = 1): string => name; f(/*caret*/)", "f(name: string, times?: number): string", 0, 0); });
    r.run("single arrow parameter", [&] { signature("const f = item => item; f(/*caret*/)", "f(item)", 0, 0, "/workspace/basic.js"); });
    r.run("async arrow", [&] { signature("const f = async (name: string): Promise<string> => name; f(/*caret*/)", "f(name: string): Promise<string>", 0, 0); });
    r.run("function expression", [&] { signature("const f = function(name: string):void {}; f(/*caret*/)", "f(name: string): void", 0, 0); });
    r.run("optional call", [&] { signature("function f(x:number):void{} f?.(/*caret*/)", "f(x: number): void", 0, 0); });
    r.run("constructor", [&] { signature("class A {constructor(name:string, count?:number){}} new A(/*caret*/)", "new A(name: string, count?: number)", 0, 0); });
    r.run("default constructor", [&] { signature("class A {} new A(/*caret*/)", "new A()", 0, 0); });
    r.run("private constructor", [&] { MN_CHECK_EQ(request("class A {private constructor(){}} new A(/*caret*/)"), "{}"); });
    r.run("instance method", [&] { signature("class A {run(x:number):void{}} const a = new A(); a.run(/*caret*/)", "A.run(x: number): void", 0, 0); });
    r.run("static method", [&] { signature("class A {static run(x:number):void{}} A.run(/*caret*/)", "A.run(x: number): void", 0, 0); });
    r.run("object method", [&] { signature("const a = {run(x:number):void{}}; a.run(/*caret*/)", "run(x: number): void", 0, 0); });
    r.run("object arrow property", [&] { signature("const a = {run:(x:number):void=>{}}; a.run(/*caret*/)", "run(x: number): void", 0, 0); });
    r.run("class arrow property", [&] { signature("class A {run = (x:number):void=>{};} const a = new A(); a.run(/*caret*/)", "A.run(x: number): void", 0, 0); });
    r.run("scope shadow", [&] { signature("function f(x:number):void{} { const f = (text:string):void=>{}; f(/*caret*/); }", "f(text: string): void", 0, 0); });
    r.run("callback body no outer hint", [&] { MN_CHECK_EQ(request("function f(cb:any, x:number):void{} const run=()=>{ f(()=>{ /*caret*/ },1); };"), "{}"); });
    r.run("anonymous arrow parameters no outer hint", [&] { MN_CHECK_EQ(request("function f(cb:any):void{} f((x, /*caret*/y)=>x)"), "{}"); });
    r.run("anonymous function body no outer hint", [&] { MN_CHECK_EQ(request("function f(cb:any):void{} f(function(){ /*caret*/ })"), "{}"); });
    r.run("anonymous function parameter no outer hint", [&] { MN_CHECK_EQ(request("function f(cb:any):void{} f(function(x, /*caret*/y){})"), "{}"); });
    r.run("inner call in callback still works", [&] { signature("function f(cb:any):void{} function g(x:number):void{} f(()=>{g(/*caret*/);});", "g(x: number): void", 0, 0); });
    r.run("closed callback restores outer hint", [&] { signature("function f(cb:any,x:number):void{} f(()=>{}, /*caret*/)", "f(cb: any, x: number): void", 1, 1); });
    r.run("overload selects arity", [&] {
        auto output = request("function f(x:string):void; function f(x:number,y:number):void; function f(x:any,y?:any){} f(1,/*caret*/)");
        contains(output, "\"activeSignature\":1,"); absent(output, "x: any");
    });
    r.run("this parameter excluded", [&] { signature("function f(this: object, x:number):void{} f(/*caret*/)", "f(x: number): void", 0, 0); });
    r.run("generic commas in annotation", [&] { signature("function f(x: Map<string, number>, y: number):void{} f(new Map(), /*caret*/)", "f(x: Map<string, number>, y: number): void", 1, 1); });
    r.run("binding pattern label", [&] { signature("function f({x,y}: object, z:number):void{} f({}, /*caret*/)", "f({x,y}: object, z: number): void", 1, 1); });
    r.run("unicode coordinates", [&] { signature("// 中文😀\r\nfunction 测试(名字:string, 数量:number):void{} 测试('😀', /*caret*/)", "测试(名字: string, 数量: number): void", 1, 1); });

    r.run("arrow parameter members", [&] { contains(request("const f = (label:string) => { label./*caret*/ };", MN_LANGUAGE_COMPLETION), "\"label\":\"toUpperCase\""); });
    r.run("arrow scope does not leak", [&] { absent(request("const f = (secret:string) => secret; /*caret*/", MN_LANGUAGE_COMPLETION), "\"label\":\"secret\""); });
    r.run("semicolonless arrow signature", [&] { signature("const f = (name: string) => name\nf(/*caret*/)", "f(name: string): string", 0, 0); });
    r.run("semicolonless arrow scope", [&] { absent(request("const f = (secret: string) => secret\n/*caret*/", MN_LANGUAGE_COMPLETION), "\"label\":\"secret\""); });
    r.run("semicolonless next expression scope", [&] { absent(request("const f = (secret: string) => secret\nconsole.log('x')\n/*caret*/", MN_LANGUAGE_COMPLETION), "\"label\":\"secret\""); });
    r.run("multiline arrow body parameter", [&] { contains(request("const f = (name: string) =>\n name./*caret*/", MN_LANGUAGE_COMPLETION), "\"label\":\"toUpperCase\""); });
    r.run("multiline member continuation", [&] { contains(request("const f = (name: string) => name\n ./*caret*/", MN_LANGUAGE_COMPLETION), "\"label\":\"toUpperCase\""); });
    r.run("arrow completion snippet", [&] { contains(request("const greet = (name:string):void=>{}; gre/*caret*/", MN_LANGUAGE_COMPLETION), "\"snippet\":\"greet(${1:name})$0\""); });
    r.run("do not duplicate parentheses", [&] { auto output=request("console.lo/*caret*/g('x');", MN_LANGUAGE_COMPLETION); contains(output,"\"snippet\":\"log\""); absent(output,"log(${1:"); });
    r.run("JS keyword isolation", [&] { auto output=request("/*caret*/",MN_LANGUAGE_COMPLETION,"/workspace/basic.js"); contains(output,"\"label\":\"function\""); absent(output,"\"label\":\"interface\""); });
    r.run("TS keyword retained", [&] { contains(request("/*caret*/",MN_LANGUAGE_COMPLETION),"\"label\":\"interface\""); });
    r.run("JS constructor properties", [&] { auto output=request("class Counter {constructor(initial=0){this.value=initial;} increment(){this.value++;}} const counter=new Counter(); counter./*caret*/",MN_LANGUAGE_COMPLETION,"/workspace/basic.js"); contains(output,"\"label\":\"value\""); contains(output,"\"label\":\"increment\""); });
    r.run("member and diagnostic string suppression", [&] { MN_CHECK_EQ(request("const s='console./*caret*/';",MN_LANGUAGE_COMPLETION), "{\"items\":[]}"); });

    r.run("balanced structure", [&] { MN_CHECK_EQ(request("function f(x){return {items:[x]};}",MN_LANGUAGE_DIAGNOSTICS), "{\"diagnostics\":[]}"); });
    r.run("missing closing parenthesis", [&] { contains(request("f(",MN_LANGUAGE_DIAGNOSTICS), "Unclosed delimiter '('"); });
    r.run("unexpected bracket", [&] { contains(request("const x = ];",MN_LANGUAGE_DIAGNOSTICS), "Unexpected closing delimiter ']'" ); });
    r.run("mismatched nesting", [&] { contains(request("([)]",MN_LANGUAGE_DIAGNOSTICS), "Unclosed delimiter '['"); });
    r.run("ignore string delimiters", [&] { MN_CHECK_EQ(request("const s='([}'; // }])",MN_LANGUAGE_DIAGNOSTICS), "{\"diagnostics\":[]}"); });
    r.run("ignore regexp delimiters", [&] { MN_CHECK_EQ(request("/[(]/.test('('); const r = /[}]/;",MN_LANGUAGE_DIAGNOSTICS), "{\"diagnostics\":[]}"); });
    r.run("regexp after condition", [&] { MN_CHECK_EQ(request("if (true) /[(]/.test('(');",MN_LANGUAGE_DIAGNOSTICS), "{\"diagnostics\":[]}"); });
    r.run("division remains operator", [&] { MN_CHECK_EQ(request("const x=(4/2)/(3/1);",MN_LANGUAGE_DIAGNOSTICS), "{\"diagnostics\":[]}"); });
    r.run("nested template structure", [&] { MN_CHECK_EQ(request("const s = `${({x:`${1}`}).x}`;",MN_LANGUAGE_DIAGNOSTICS), "{\"diagnostics\":[]}"); });
    r.run("lexical error retained", [&] { contains(request("const s='unterminated",MN_LANGUAGE_DIAGNOSTICS), "Unterminated"); });

    r.run("block folding", [&] { MN_CHECK_EQ(request("function f() {\n  return 1;\n}\n",MN_LANGUAGE_FOLDING), "{\"folding\":[{\"startLine\":1,\"endLine\":2}]}"); });
    r.run("array folding CRLF", [&] { MN_CHECK_EQ(request("const a=[\r\n1,\r\n2\r\n];",MN_LANGUAGE_FOLDING), "{\"folding\":[{\"startLine\":1,\"endLine\":3}]}"); });
    r.run("multiline comment folding", [&] { MN_CHECK_EQ(request("/* first\nsecond\n*/",MN_LANGUAGE_FOLDING), "{\"folding\":[{\"startLine\":1,\"endLine\":3,\"kind\":\"comment\"}]}"); });
    r.run("adjacent comments folding", [&] { MN_CHECK_EQ(request("// first\n// second\nconst a=1;",MN_LANGUAGE_FOLDING), "{\"folding\":[{\"startLine\":1,\"endLine\":2,\"kind\":\"comment\"}]}"); });
    r.run("region folding", [&] { MN_CHECK_EQ(request("// #region name\nconst a=1;\n// #endregion",MN_LANGUAGE_FOLDING), "{\"folding\":[{\"startLine\":1,\"endLine\":3,\"kind\":\"region\"}]}"); });
    r.run("no region inside block comment", [&] { auto output=request("/*\n// #region name\n// #endregion\n*/",MN_LANGUAGE_FOLDING); absent(output,"\"kind\":\"region\""); contains(output,"\"kind\":\"comment\""); });
    r.run("inline braces no folds", [&] { MN_CHECK_EQ(request("const a={x:[1,2]}; const s='{[';",MN_LANGUAGE_FOLDING), "{\"folding\":[]}"); });
    r.run("template fold", [&] { MN_CHECK_EQ(request("const s=`first\nsecond\nthird`;",MN_LANGUAGE_FOLDING), "{\"folding\":[{\"startLine\":1,\"endLine\":3}]}"); });
    r.run("no fake regex folding", [&] { MN_CHECK_EQ(request("const r=/[{}]/;\nconst a=1;",MN_LANGUAGE_FOLDING), "{\"folding\":[]}"); });
    r.run("nested code folds", [&] { auto output=request("function f() {\n  if(true) {\n    x();\n  }\n}\n",MN_LANGUAGE_FOLDING); contains(output,"\"startLine\":1,\"endLine\":4"); contains(output,"\"startLine\":2,\"endLine\":3"); });
    r.run("method completion requests parameter hints", [&] { contains(request("console.lo/*caret*/", MN_LANGUAGE_COMPLETION), "\"commandId\":\"editor.action.triggerParameterHints\""); });
    r.run("typed semicolonless declaration", [&] { contains(request("let title: string\ntitle./*caret*/", MN_LANGUAGE_COMPLETION), "\"label\":\"toUpperCase\""); });
    r.run("basic services malformed-input safety", [&] {
        const std::vector<std::string> atoms = {"function", "class", "const", "let", "new", "return", "if", "foo", "number",
            "{", "}", "(", ")", "[", "]", ":", ";", ",", ".", "?", "=", "=>", "...", "'text'", "42", "/*comment*/", "`x${foo}`", "\n"};
        uint32_t state = 0x58304;
        for (unsigned n = 0; n < 128; ++n)
        {
            std::string source;
            for (unsigned i = 0; i < (n % 40); ++i)
            { state = state * 1664525u + 1013904223u; source += atoms[state % atoms.size()] + " "; }
            source += "/*caret*/";
            (void)request(source, MN_LANGUAGE_SIGNATURE);
            (void)request(source, MN_LANGUAGE_SIGNATURE_MANUAL);
            (void)request(source, MN_LANGUAGE_FOLDING);
            (void)request(source, MN_LANGUAGE_DIAGNOSTICS);
        }
    });
    r.run("empty document", [&] { MN_CHECK_EQ(request("",MN_LANGUAGE_FOLDING),"{\"folding\":[]}"); MN_CHECK_EQ(request(""),"{}"); });

    std::cout << "TypeScript basic editing: " << r.passed << " cases passed, " << r.failed << " failed.\n";
    if (r.failed) throw std::runtime_error("TypeScript basic editing regression failures");
}
