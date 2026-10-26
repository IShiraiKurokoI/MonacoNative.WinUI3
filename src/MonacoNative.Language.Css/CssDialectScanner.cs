// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License; see THIRD_PARTY_NOTICES.md.
// Source port: vscode-css-languageservice v6.3.1/src/parser/{scss,less}Scanner.ts.
using System.Text;

namespace MonacoNative.Language.Css;

public static class CssDialectScanner
{
    public static CssScanner Create(string languageId, string text = "", CancellationToken cancellationToken = default) =>
        languageId switch
        {
            "scss" => new ScssScanner(text, cancellationToken),
            "less" => new LessScanner(text, cancellationToken),
            _ => new CssScanner(text, cancellationToken)
        };
}

/// <summary>Upstream SCSS token extensions. Parser, evaluator and completion are separate layers.</summary>
public sealed class ScssScanner : CssScanner
{
    public const CssTokenType VariableName = CssTokenType.CustomToken;
    public const CssTokenType InterpolationFunction = (CssTokenType)((int)CssTokenType.CustomToken + 1);
    public const CssTokenType Default = (CssTokenType)((int)CssTokenType.CustomToken + 2);
    public const CssTokenType EqualsOperator = (CssTokenType)((int)CssTokenType.CustomToken + 3);
    public const CssTokenType NotEqualsOperator = (CssTokenType)((int)CssTokenType.CustomToken + 4);
    public const CssTokenType GreaterEqualsOperator = (CssTokenType)((int)CssTokenType.CustomToken + 5);
    public const CssTokenType SmallerEqualsOperator = (CssTokenType)((int)CssTokenType.CustomToken + 6);
    public const CssTokenType Ellipsis = (CssTokenType)((int)CssTokenType.CustomToken + 7);
    public const CssTokenType Module = (CssTokenType)((int)CssTokenType.CustomToken + 8);

    public ScssScanner(string text = "", CancellationToken cancellationToken = default) : base(text, cancellationToken) { }

    protected override CssToken ScanNext(int offset)
    {
        if (AdvanceIf('$'))
        {
            var content = new StringBuilder("$");
            if (Ident(content)) return Finish(offset, VariableName, content.ToString());
            GoBackTo(offset);
        }
        if (AdvanceIf("#{")) return Finish(offset, InterpolationFunction);
        if (AdvanceIf("==")) return Finish(offset, EqualsOperator);
        if (AdvanceIf("!=")) return Finish(offset, NotEqualsOperator);
        if (AdvanceIf('<')) return Finish(offset, AdvanceIf('=') ? SmallerEqualsOperator : CssTokenType.Delim);
        if (AdvanceIf('>')) return Finish(offset, AdvanceIf('=') ? GreaterEqualsOperator : CssTokenType.Delim);
        if (AdvanceIf("...")) return Finish(offset, Ellipsis);
        return base.ScanNext(offset);
    }

    protected override bool Comment()
    {
        if (base.Comment()) return true;
        if (InUrl || !AdvanceIf("//")) return false;
        AdvanceWhile(ch => ch is not ('\n' or '\r' or '\f'));
        return true;
    }
}

/// <summary>Upstream LESS scanner; escaped JavaScript is tokenized, never executed.</summary>
public sealed class LessScanner : CssScanner
{
    // Each upstream dialect owns its own CustomToken namespace.
    public const CssTokenType Ellipsis = CssTokenType.CustomToken;
    public LessScanner(string text = "", CancellationToken cancellationToken = default) : base(text, cancellationToken) { }

    protected override CssToken ScanNext(int offset)
    {
        if (AdvanceIf('`'))
        {
            AdvanceWhile(ch => ch != '`');
            return Finish(offset, AdvanceIf('`') ? CssTokenType.EscapedJavaScript : CssTokenType.BadEscapedJavaScript);
        }
        if (AdvanceIf("...")) return Finish(offset, Ellipsis);
        return base.ScanNext(offset);
    }

    protected override bool Comment()
    {
        if (base.Comment()) return true;
        if (InUrl || !AdvanceIf("//")) return false;
        AdvanceWhile(ch => ch is not ('\n' or '\r' or '\f'));
        return true;
    }
}
