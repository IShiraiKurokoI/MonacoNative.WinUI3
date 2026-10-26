// Derived from Microsoft TypeScript 5.8.3, scanner.ts / types.ts.
// Copyright (c) Microsoft Corporation. Apache-2.0; see THIRD_PARTY_NOTICES.md.
// Audited npm lib/typescript.js SHA-256: dd17428736a07e1db1a138d8a14295ddb2699ba780ee15038acdd2c6da5373a0
// Only tables/enums are copied here; no JavaScript runtime is shipped.
#pragma once
#include <cstdint>
#include <string_view>
#include <utility>
namespace mn::typescript {
namespace sk {
inline constexpr int Unknown = 0;
inline constexpr int EndOfFileToken = 1;
inline constexpr int SingleLineCommentTrivia = 2;
inline constexpr int MultiLineCommentTrivia = 3;
inline constexpr int NewLineTrivia = 4;
inline constexpr int WhitespaceTrivia = 5;
inline constexpr int ShebangTrivia = 6;
inline constexpr int ConflictMarkerTrivia = 7;
inline constexpr int NonTextFileMarkerTrivia = 8;
inline constexpr int NumericLiteral = 9;
inline constexpr int BigIntLiteral = 10;
inline constexpr int StringLiteral = 11;
inline constexpr int JsxText = 12;
inline constexpr int JsxTextAllWhiteSpaces = 13;
inline constexpr int RegularExpressionLiteral = 14;
inline constexpr int NoSubstitutionTemplateLiteral = 15;
inline constexpr int TemplateHead = 16;
inline constexpr int TemplateMiddle = 17;
inline constexpr int TemplateTail = 18;
inline constexpr int OpenBraceToken = 19;
inline constexpr int CloseBraceToken = 20;
inline constexpr int OpenParenToken = 21;
inline constexpr int CloseParenToken = 22;
inline constexpr int OpenBracketToken = 23;
inline constexpr int CloseBracketToken = 24;
inline constexpr int DotToken = 25;
inline constexpr int DotDotDotToken = 26;
inline constexpr int SemicolonToken = 27;
inline constexpr int CommaToken = 28;
inline constexpr int QuestionDotToken = 29;
inline constexpr int LessThanToken = 30;
inline constexpr int LessThanSlashToken = 31;
inline constexpr int GreaterThanToken = 32;
inline constexpr int LessThanEqualsToken = 33;
inline constexpr int GreaterThanEqualsToken = 34;
inline constexpr int EqualsEqualsToken = 35;
inline constexpr int ExclamationEqualsToken = 36;
inline constexpr int EqualsEqualsEqualsToken = 37;
inline constexpr int ExclamationEqualsEqualsToken = 38;
inline constexpr int EqualsGreaterThanToken = 39;
inline constexpr int PlusToken = 40;
inline constexpr int MinusToken = 41;
inline constexpr int AsteriskToken = 42;
inline constexpr int AsteriskAsteriskToken = 43;
inline constexpr int SlashToken = 44;
inline constexpr int PercentToken = 45;
inline constexpr int PlusPlusToken = 46;
inline constexpr int MinusMinusToken = 47;
inline constexpr int LessThanLessThanToken = 48;
inline constexpr int GreaterThanGreaterThanToken = 49;
inline constexpr int GreaterThanGreaterThanGreaterThanToken = 50;
inline constexpr int AmpersandToken = 51;
inline constexpr int BarToken = 52;
inline constexpr int CaretToken = 53;
inline constexpr int ExclamationToken = 54;
inline constexpr int TildeToken = 55;
inline constexpr int AmpersandAmpersandToken = 56;
inline constexpr int BarBarToken = 57;
inline constexpr int QuestionToken = 58;
inline constexpr int ColonToken = 59;
inline constexpr int AtToken = 60;
inline constexpr int QuestionQuestionToken = 61;
inline constexpr int BacktickToken = 62;
inline constexpr int HashToken = 63;
inline constexpr int EqualsToken = 64;
inline constexpr int PlusEqualsToken = 65;
inline constexpr int MinusEqualsToken = 66;
inline constexpr int AsteriskEqualsToken = 67;
inline constexpr int AsteriskAsteriskEqualsToken = 68;
inline constexpr int SlashEqualsToken = 69;
inline constexpr int PercentEqualsToken = 70;
inline constexpr int LessThanLessThanEqualsToken = 71;
inline constexpr int GreaterThanGreaterThanEqualsToken = 72;
inline constexpr int GreaterThanGreaterThanGreaterThanEqualsToken = 73;
inline constexpr int AmpersandEqualsToken = 74;
inline constexpr int BarEqualsToken = 75;
inline constexpr int BarBarEqualsToken = 76;
inline constexpr int AmpersandAmpersandEqualsToken = 77;
inline constexpr int QuestionQuestionEqualsToken = 78;
inline constexpr int CaretEqualsToken = 79;
inline constexpr int Identifier = 80;
inline constexpr int PrivateIdentifier = 81;
inline constexpr int JSDocCommentTextToken = 82;
inline constexpr int BreakKeyword = 83;
inline constexpr int CaseKeyword = 84;
inline constexpr int CatchKeyword = 85;
inline constexpr int ClassKeyword = 86;
inline constexpr int ConstKeyword = 87;
inline constexpr int ContinueKeyword = 88;
inline constexpr int DebuggerKeyword = 89;
inline constexpr int DefaultKeyword = 90;
inline constexpr int DeleteKeyword = 91;
inline constexpr int DoKeyword = 92;
inline constexpr int ElseKeyword = 93;
inline constexpr int EnumKeyword = 94;
inline constexpr int ExportKeyword = 95;
inline constexpr int ExtendsKeyword = 96;
inline constexpr int FalseKeyword = 97;
inline constexpr int FinallyKeyword = 98;
inline constexpr int ForKeyword = 99;
inline constexpr int FunctionKeyword = 100;
inline constexpr int IfKeyword = 101;
inline constexpr int ImportKeyword = 102;
inline constexpr int InKeyword = 103;
inline constexpr int InstanceOfKeyword = 104;
inline constexpr int NewKeyword = 105;
inline constexpr int NullKeyword = 106;
inline constexpr int ReturnKeyword = 107;
inline constexpr int SuperKeyword = 108;
inline constexpr int SwitchKeyword = 109;
inline constexpr int ThisKeyword = 110;
inline constexpr int ThrowKeyword = 111;
inline constexpr int TrueKeyword = 112;
inline constexpr int TryKeyword = 113;
inline constexpr int TypeOfKeyword = 114;
inline constexpr int VarKeyword = 115;
inline constexpr int VoidKeyword = 116;
inline constexpr int WhileKeyword = 117;
inline constexpr int WithKeyword = 118;
inline constexpr int ImplementsKeyword = 119;
inline constexpr int InterfaceKeyword = 120;
inline constexpr int LetKeyword = 121;
inline constexpr int PackageKeyword = 122;
inline constexpr int PrivateKeyword = 123;
inline constexpr int ProtectedKeyword = 124;
inline constexpr int PublicKeyword = 125;
inline constexpr int StaticKeyword = 126;
inline constexpr int YieldKeyword = 127;
inline constexpr int AbstractKeyword = 128;
inline constexpr int AccessorKeyword = 129;
inline constexpr int AsKeyword = 130;
inline constexpr int AssertsKeyword = 131;
inline constexpr int AssertKeyword = 132;
inline constexpr int AnyKeyword = 133;
inline constexpr int AsyncKeyword = 134;
inline constexpr int AwaitKeyword = 135;
inline constexpr int BooleanKeyword = 136;
inline constexpr int ConstructorKeyword = 137;
inline constexpr int DeclareKeyword = 138;
inline constexpr int GetKeyword = 139;
inline constexpr int InferKeyword = 140;
inline constexpr int IntrinsicKeyword = 141;
inline constexpr int IsKeyword = 142;
inline constexpr int KeyOfKeyword = 143;
inline constexpr int ModuleKeyword = 144;
inline constexpr int NamespaceKeyword = 145;
inline constexpr int NeverKeyword = 146;
inline constexpr int OutKeyword = 147;
inline constexpr int ReadonlyKeyword = 148;
inline constexpr int RequireKeyword = 149;
inline constexpr int NumberKeyword = 150;
inline constexpr int ObjectKeyword = 151;
inline constexpr int SatisfiesKeyword = 152;
inline constexpr int SetKeyword = 153;
inline constexpr int StringKeyword = 154;
inline constexpr int SymbolKeyword = 155;
inline constexpr int TypeKeyword = 156;
inline constexpr int UndefinedKeyword = 157;
inline constexpr int UniqueKeyword = 158;
inline constexpr int UnknownKeyword = 159;
inline constexpr int UsingKeyword = 160;
inline constexpr int FromKeyword = 161;
inline constexpr int GlobalKeyword = 162;
inline constexpr int BigIntKeyword = 163;
inline constexpr int OverrideKeyword = 164;
inline constexpr int OfKeyword = 165;
inline constexpr int FirstAssignment = 64;
inline constexpr int LastAssignment = 79;
inline constexpr int FirstCompoundAssignment = 65;
inline constexpr int LastCompoundAssignment = 79;
inline constexpr int FirstReservedWord = 83;
inline constexpr int LastReservedWord = 118;
inline constexpr int FirstKeyword = 83;
inline constexpr int LastKeyword = 165;
inline constexpr int FirstFutureReservedWord = 119;
inline constexpr int LastFutureReservedWord = 127;
inline constexpr int FirstPunctuation = 19;
inline constexpr int LastPunctuation = 79;
inline constexpr int FirstToken = 0;
inline constexpr int LastToken = 165;
inline constexpr int FirstTriviaToken = 2;
inline constexpr int LastTriviaToken = 7;
inline constexpr int FirstLiteralToken = 9;
inline constexpr int LastLiteralToken = 15;
inline constexpr int FirstTemplateToken = 15;
inline constexpr int LastTemplateToken = 18;
inline constexpr int FirstBinaryOperator = 30;
inline constexpr int LastBinaryOperator = 79;
inline constexpr int FirstContextualKeyword = 128;
inline constexpr int LastContextualKeyword = 165;
}
namespace tf {
inline constexpr unsigned None = 0U;
inline constexpr unsigned PrecedingLineBreak = 1U;
inline constexpr unsigned PrecedingJSDocComment = 2U;
inline constexpr unsigned Unterminated = 4U;
inline constexpr unsigned ExtendedUnicodeEscape = 8U;
inline constexpr unsigned Scientific = 16U;
inline constexpr unsigned Octal = 32U;
inline constexpr unsigned HexSpecifier = 64U;
inline constexpr unsigned BinarySpecifier = 128U;
inline constexpr unsigned OctalSpecifier = 256U;
inline constexpr unsigned ContainsSeparator = 512U;
inline constexpr unsigned UnicodeEscape = 1024U;
inline constexpr unsigned ContainsInvalidEscape = 2048U;
inline constexpr unsigned HexEscape = 4096U;
inline constexpr unsigned ContainsLeadingZero = 8192U;
inline constexpr unsigned ContainsInvalidSeparator = 16384U;
inline constexpr unsigned PrecedingJSDocLeadingAsterisks = 32768U;
inline constexpr unsigned BinaryOrOctalSpecifier = 384U;
inline constexpr unsigned WithSpecifier = 448U;
inline constexpr unsigned StringLiteralFlags = 7176U;
inline constexpr unsigned NumericLiteralFlags = 25584U;
inline constexpr unsigned TemplateLiteralLikeFlags = 7176U;
inline constexpr unsigned IsInvalid = 26656U;
}
inline constexpr uint32_t unicodeESNextIdentifierStart[] = {
    65, 90, 97, 122, 170, 170, 181, 181, 186, 186, 192, 214, 216, 246, 248, 705,
    710, 721, 736, 740, 748, 748, 750, 750, 880, 884, 886, 887, 890, 893, 895, 895,
    902, 902, 904, 906, 908, 908, 910, 929, 931, 1013, 1015, 1153, 1162, 1327, 1329, 1366,
    1369, 1369, 1376, 1416, 1488, 1514, 1519, 1522, 1568, 1610, 1646, 1647, 1649, 1747, 1749, 1749,
    1765, 1766, 1774, 1775, 1786, 1788, 1791, 1791, 1808, 1808, 1810, 1839, 1869, 1957, 1969, 1969,
    1994, 2026, 2036, 2037, 2042, 2042, 2048, 2069, 2074, 2074, 2084, 2084, 2088, 2088, 2112, 2136,
    2144, 2154, 2160, 2183, 2185, 2190, 2208, 2249, 2308, 2361, 2365, 2365, 2384, 2384, 2392, 2401,
    2417, 2432, 2437, 2444, 2447, 2448, 2451, 2472, 2474, 2480, 2482, 2482, 2486, 2489, 2493, 2493,
    2510, 2510, 2524, 2525, 2527, 2529, 2544, 2545, 2556, 2556, 2565, 2570, 2575, 2576, 2579, 2600,
    2602, 2608, 2610, 2611, 2613, 2614, 2616, 2617, 2649, 2652, 2654, 2654, 2674, 2676, 2693, 2701,
    2703, 2705, 2707, 2728, 2730, 2736, 2738, 2739, 2741, 2745, 2749, 2749, 2768, 2768, 2784, 2785,
    2809, 2809, 2821, 2828, 2831, 2832, 2835, 2856, 2858, 2864, 2866, 2867, 2869, 2873, 2877, 2877,
    2908, 2909, 2911, 2913, 2929, 2929, 2947, 2947, 2949, 2954, 2958, 2960, 2962, 2965, 2969, 2970,
    2972, 2972, 2974, 2975, 2979, 2980, 2984, 2986, 2990, 3001, 3024, 3024, 3077, 3084, 3086, 3088,
    3090, 3112, 3114, 3129, 3133, 3133, 3160, 3162, 3165, 3165, 3168, 3169, 3200, 3200, 3205, 3212,
    3214, 3216, 3218, 3240, 3242, 3251, 3253, 3257, 3261, 3261, 3293, 3294, 3296, 3297, 3313, 3314,
    3332, 3340, 3342, 3344, 3346, 3386, 3389, 3389, 3406, 3406, 3412, 3414, 3423, 3425, 3450, 3455,
    3461, 3478, 3482, 3505, 3507, 3515, 3517, 3517, 3520, 3526, 3585, 3632, 3634, 3635, 3648, 3654,
    3713, 3714, 3716, 3716, 3718, 3722, 3724, 3747, 3749, 3749, 3751, 3760, 3762, 3763, 3773, 3773,
    3776, 3780, 3782, 3782, 3804, 3807, 3840, 3840, 3904, 3911, 3913, 3948, 3976, 3980, 4096, 4138,
    4159, 4159, 4176, 4181, 4186, 4189, 4193, 4193, 4197, 4198, 4206, 4208, 4213, 4225, 4238, 4238,
    4256, 4293, 4295, 4295, 4301, 4301, 4304, 4346, 4348, 4680, 4682, 4685, 4688, 4694, 4696, 4696,
    4698, 4701, 4704, 4744, 4746, 4749, 4752, 4784, 4786, 4789, 4792, 4798, 4800, 4800, 4802, 4805,
    4808, 4822, 4824, 4880, 4882, 4885, 4888, 4954, 4992, 5007, 5024, 5109, 5112, 5117, 5121, 5740,
    5743, 5759, 5761, 5786, 5792, 5866, 5870, 5880, 5888, 5905, 5919, 5937, 5952, 5969, 5984, 5996,
    5998, 6000, 6016, 6067, 6103, 6103, 6108, 6108, 6176, 6264, 6272, 6312, 6314, 6314, 6320, 6389,
    6400, 6430, 6480, 6509, 6512, 6516, 6528, 6571, 6576, 6601, 6656, 6678, 6688, 6740, 6823, 6823,
    6917, 6963, 6981, 6988, 7043, 7072, 7086, 7087, 7098, 7141, 7168, 7203, 7245, 7247, 7258, 7293,
    7296, 7304, 7312, 7354, 7357, 7359, 7401, 7404, 7406, 7411, 7413, 7414, 7418, 7418, 7424, 7615,
    7680, 7957, 7960, 7965, 7968, 8005, 8008, 8013, 8016, 8023, 8025, 8025, 8027, 8027, 8029, 8029,
    8031, 8061, 8064, 8116, 8118, 8124, 8126, 8126, 8130, 8132, 8134, 8140, 8144, 8147, 8150, 8155,
    8160, 8172, 8178, 8180, 8182, 8188, 8305, 8305, 8319, 8319, 8336, 8348, 8450, 8450, 8455, 8455,
    8458, 8467, 8469, 8469, 8472, 8477, 8484, 8484, 8486, 8486, 8488, 8488, 8490, 8505, 8508, 8511,
    8517, 8521, 8526, 8526, 8544, 8584, 11264, 11492, 11499, 11502, 11506, 11507, 11520, 11557, 11559, 11559,
    11565, 11565, 11568, 11623, 11631, 11631, 11648, 11670, 11680, 11686, 11688, 11694, 11696, 11702, 11704, 11710,
    11712, 11718, 11720, 11726, 11728, 11734, 11736, 11742, 12293, 12295, 12321, 12329, 12337, 12341, 12344, 12348,
    12353, 12438, 12443, 12447, 12449, 12538, 12540, 12543, 12549, 12591, 12593, 12686, 12704, 12735, 12784, 12799,
    13312, 19903, 19968, 42124, 42192, 42237, 42240, 42508, 42512, 42527, 42538, 42539, 42560, 42606, 42623, 42653,
    42656, 42735, 42775, 42783, 42786, 42888, 42891, 42954, 42960, 42961, 42963, 42963, 42965, 42969, 42994, 43009,
    43011, 43013, 43015, 43018, 43020, 43042, 43072, 43123, 43138, 43187, 43250, 43255, 43259, 43259, 43261, 43262,
    43274, 43301, 43312, 43334, 43360, 43388, 43396, 43442, 43471, 43471, 43488, 43492, 43494, 43503, 43514, 43518,
    43520, 43560, 43584, 43586, 43588, 43595, 43616, 43638, 43642, 43642, 43646, 43695, 43697, 43697, 43701, 43702,
    43705, 43709, 43712, 43712, 43714, 43714, 43739, 43741, 43744, 43754, 43762, 43764, 43777, 43782, 43785, 43790,
    43793, 43798, 43808, 43814, 43816, 43822, 43824, 43866, 43868, 43881, 43888, 44002, 44032, 55203, 55216, 55238,
    55243, 55291, 63744, 64109, 64112, 64217, 64256, 64262, 64275, 64279, 64285, 64285, 64287, 64296, 64298, 64310,
    64312, 64316, 64318, 64318, 64320, 64321, 64323, 64324, 64326, 64433, 64467, 64829, 64848, 64911, 64914, 64967,
    65008, 65019, 65136, 65140, 65142, 65276, 65313, 65338, 65345, 65370, 65382, 65470, 65474, 65479, 65482, 65487,
    65490, 65495, 65498, 65500, 65536, 65547, 65549, 65574, 65576, 65594, 65596, 65597, 65599, 65613, 65616, 65629,
    65664, 65786, 65856, 65908, 66176, 66204, 66208, 66256, 66304, 66335, 66349, 66378, 66384, 66421, 66432, 66461,
    66464, 66499, 66504, 66511, 66513, 66517, 66560, 66717, 66736, 66771, 66776, 66811, 66816, 66855, 66864, 66915,
    66928, 66938, 66940, 66954, 66956, 66962, 66964, 66965, 66967, 66977, 66979, 66993, 66995, 67001, 67003, 67004,
    67072, 67382, 67392, 67413, 67424, 67431, 67456, 67461, 67463, 67504, 67506, 67514, 67584, 67589, 67592, 67592,
    67594, 67637, 67639, 67640, 67644, 67644, 67647, 67669, 67680, 67702, 67712, 67742, 67808, 67826, 67828, 67829,
    67840, 67861, 67872, 67897, 67968, 68023, 68030, 68031, 68096, 68096, 68112, 68115, 68117, 68119, 68121, 68149,
    68192, 68220, 68224, 68252, 68288, 68295, 68297, 68324, 68352, 68405, 68416, 68437, 68448, 68466, 68480, 68497,
    68608, 68680, 68736, 68786, 68800, 68850, 68864, 68899, 69248, 69289, 69296, 69297, 69376, 69404, 69415, 69415,
    69424, 69445, 69488, 69505, 69552, 69572, 69600, 69622, 69635, 69687, 69745, 69746, 69749, 69749, 69763, 69807,
    69840, 69864, 69891, 69926, 69956, 69956, 69959, 69959, 69968, 70002, 70006, 70006, 70019, 70066, 70081, 70084,
    70106, 70106, 70108, 70108, 70144, 70161, 70163, 70187, 70207, 70208, 70272, 70278, 70280, 70280, 70282, 70285,
    70287, 70301, 70303, 70312, 70320, 70366, 70405, 70412, 70415, 70416, 70419, 70440, 70442, 70448, 70450, 70451,
    70453, 70457, 70461, 70461, 70480, 70480, 70493, 70497, 70656, 70708, 70727, 70730, 70751, 70753, 70784, 70831,
    70852, 70853, 70855, 70855, 71040, 71086, 71128, 71131, 71168, 71215, 71236, 71236, 71296, 71338, 71352, 71352,
    71424, 71450, 71488, 71494, 71680, 71723, 71840, 71903, 71935, 71942, 71945, 71945, 71948, 71955, 71957, 71958,
    71960, 71983, 71999, 71999, 72001, 72001, 72096, 72103, 72106, 72144, 72161, 72161, 72163, 72163, 72192, 72192,
    72203, 72242, 72250, 72250, 72272, 72272, 72284, 72329, 72349, 72349, 72368, 72440, 72704, 72712, 72714, 72750,
    72768, 72768, 72818, 72847, 72960, 72966, 72968, 72969, 72971, 73008, 73030, 73030, 73056, 73061, 73063, 73064,
    73066, 73097, 73112, 73112, 73440, 73458, 73474, 73474, 73476, 73488, 73490, 73523, 73648, 73648, 73728, 74649,
    74752, 74862, 74880, 75075, 77712, 77808, 77824, 78895, 78913, 78918, 82944, 83526, 92160, 92728, 92736, 92766,
    92784, 92862, 92880, 92909, 92928, 92975, 92992, 92995, 93027, 93047, 93053, 93071, 93760, 93823, 93952, 94026,
    94032, 94032, 94099, 94111, 94176, 94177, 94179, 94179, 94208, 100343, 100352, 101589, 101632, 101640, 110576, 110579,
    110581, 110587, 110589, 110590, 110592, 110882, 110898, 110898, 110928, 110930, 110933, 110933, 110948, 110951, 110960, 111355,
    113664, 113770, 113776, 113788, 113792, 113800, 113808, 113817, 119808, 119892, 119894, 119964, 119966, 119967, 119970, 119970,
    119973, 119974, 119977, 119980, 119982, 119993, 119995, 119995, 119997, 120003, 120005, 120069, 120071, 120074, 120077, 120084,
    120086, 120092, 120094, 120121, 120123, 120126, 120128, 120132, 120134, 120134, 120138, 120144, 120146, 120485, 120488, 120512,
    120514, 120538, 120540, 120570, 120572, 120596, 120598, 120628, 120630, 120654, 120656, 120686, 120688, 120712, 120714, 120744,
    120746, 120770, 120772, 120779, 122624, 122654, 122661, 122666, 122928, 122989, 123136, 123180, 123191, 123197, 123214, 123214,
    123536, 123565, 123584, 123627, 124112, 124139, 124896, 124902, 124904, 124907, 124909, 124910, 124912, 124926, 124928, 125124,
    125184, 125251, 125259, 125259, 126464, 126467, 126469, 126495, 126497, 126498, 126500, 126500, 126503, 126503, 126505, 126514,
    126516, 126519, 126521, 126521, 126523, 126523, 126530, 126530, 126535, 126535, 126537, 126537, 126539, 126539, 126541, 126543,
    126545, 126546, 126548, 126548, 126551, 126551, 126553, 126553, 126555, 126555, 126557, 126557, 126559, 126559, 126561, 126562,
    126564, 126564, 126567, 126570, 126572, 126578, 126580, 126583, 126585, 126588, 126590, 126590, 126592, 126601, 126603, 126619,
    126625, 126627, 126629, 126633, 126635, 126651, 131072, 173791, 173824, 177977, 177984, 178205, 178208, 183969, 183984, 191456,
    191472, 192093, 194560, 195101, 196608, 201546, 201552, 205743,
};
inline constexpr uint32_t unicodeESNextIdentifierPart[] = {
    48, 57, 65, 90, 95, 95, 97, 122, 170, 170, 181, 181, 183, 183, 186, 186,
    192, 214, 216, 246, 248, 705, 710, 721, 736, 740, 748, 748, 750, 750, 768, 884,
    886, 887, 890, 893, 895, 895, 902, 906, 908, 908, 910, 929, 931, 1013, 1015, 1153,
    1155, 1159, 1162, 1327, 1329, 1366, 1369, 1369, 1376, 1416, 1425, 1469, 1471, 1471, 1473, 1474,
    1476, 1477, 1479, 1479, 1488, 1514, 1519, 1522, 1552, 1562, 1568, 1641, 1646, 1747, 1749, 1756,
    1759, 1768, 1770, 1788, 1791, 1791, 1808, 1866, 1869, 1969, 1984, 2037, 2042, 2042, 2045, 2045,
    2048, 2093, 2112, 2139, 2144, 2154, 2160, 2183, 2185, 2190, 2200, 2273, 2275, 2403, 2406, 2415,
    2417, 2435, 2437, 2444, 2447, 2448, 2451, 2472, 2474, 2480, 2482, 2482, 2486, 2489, 2492, 2500,
    2503, 2504, 2507, 2510, 2519, 2519, 2524, 2525, 2527, 2531, 2534, 2545, 2556, 2556, 2558, 2558,
    2561, 2563, 2565, 2570, 2575, 2576, 2579, 2600, 2602, 2608, 2610, 2611, 2613, 2614, 2616, 2617,
    2620, 2620, 2622, 2626, 2631, 2632, 2635, 2637, 2641, 2641, 2649, 2652, 2654, 2654, 2662, 2677,
    2689, 2691, 2693, 2701, 2703, 2705, 2707, 2728, 2730, 2736, 2738, 2739, 2741, 2745, 2748, 2757,
    2759, 2761, 2763, 2765, 2768, 2768, 2784, 2787, 2790, 2799, 2809, 2815, 2817, 2819, 2821, 2828,
    2831, 2832, 2835, 2856, 2858, 2864, 2866, 2867, 2869, 2873, 2876, 2884, 2887, 2888, 2891, 2893,
    2901, 2903, 2908, 2909, 2911, 2915, 2918, 2927, 2929, 2929, 2946, 2947, 2949, 2954, 2958, 2960,
    2962, 2965, 2969, 2970, 2972, 2972, 2974, 2975, 2979, 2980, 2984, 2986, 2990, 3001, 3006, 3010,
    3014, 3016, 3018, 3021, 3024, 3024, 3031, 3031, 3046, 3055, 3072, 3084, 3086, 3088, 3090, 3112,
    3114, 3129, 3132, 3140, 3142, 3144, 3146, 3149, 3157, 3158, 3160, 3162, 3165, 3165, 3168, 3171,
    3174, 3183, 3200, 3203, 3205, 3212, 3214, 3216, 3218, 3240, 3242, 3251, 3253, 3257, 3260, 3268,
    3270, 3272, 3274, 3277, 3285, 3286, 3293, 3294, 3296, 3299, 3302, 3311, 3313, 3315, 3328, 3340,
    3342, 3344, 3346, 3396, 3398, 3400, 3402, 3406, 3412, 3415, 3423, 3427, 3430, 3439, 3450, 3455,
    3457, 3459, 3461, 3478, 3482, 3505, 3507, 3515, 3517, 3517, 3520, 3526, 3530, 3530, 3535, 3540,
    3542, 3542, 3544, 3551, 3558, 3567, 3570, 3571, 3585, 3642, 3648, 3662, 3664, 3673, 3713, 3714,
    3716, 3716, 3718, 3722, 3724, 3747, 3749, 3749, 3751, 3773, 3776, 3780, 3782, 3782, 3784, 3790,
    3792, 3801, 3804, 3807, 3840, 3840, 3864, 3865, 3872, 3881, 3893, 3893, 3895, 3895, 3897, 3897,
    3902, 3911, 3913, 3948, 3953, 3972, 3974, 3991, 3993, 4028, 4038, 4038, 4096, 4169, 4176, 4253,
    4256, 4293, 4295, 4295, 4301, 4301, 4304, 4346, 4348, 4680, 4682, 4685, 4688, 4694, 4696, 4696,
    4698, 4701, 4704, 4744, 4746, 4749, 4752, 4784, 4786, 4789, 4792, 4798, 4800, 4800, 4802, 4805,
    4808, 4822, 4824, 4880, 4882, 4885, 4888, 4954, 4957, 4959, 4969, 4977, 4992, 5007, 5024, 5109,
    5112, 5117, 5121, 5740, 5743, 5759, 5761, 5786, 5792, 5866, 5870, 5880, 5888, 5909, 5919, 5940,
    5952, 5971, 5984, 5996, 5998, 6000, 6002, 6003, 6016, 6099, 6103, 6103, 6108, 6109, 6112, 6121,
    6155, 6157, 6159, 6169, 6176, 6264, 6272, 6314, 6320, 6389, 6400, 6430, 6432, 6443, 6448, 6459,
    6470, 6509, 6512, 6516, 6528, 6571, 6576, 6601, 6608, 6618, 6656, 6683, 6688, 6750, 6752, 6780,
    6783, 6793, 6800, 6809, 6823, 6823, 6832, 6845, 6847, 6862, 6912, 6988, 6992, 7001, 7019, 7027,
    7040, 7155, 7168, 7223, 7232, 7241, 7245, 7293, 7296, 7304, 7312, 7354, 7357, 7359, 7376, 7378,
    7380, 7418, 7424, 7957, 7960, 7965, 7968, 8005, 8008, 8013, 8016, 8023, 8025, 8025, 8027, 8027,
    8029, 8029, 8031, 8061, 8064, 8116, 8118, 8124, 8126, 8126, 8130, 8132, 8134, 8140, 8144, 8147,
    8150, 8155, 8160, 8172, 8178, 8180, 8182, 8188, 8204, 8205, 8255, 8256, 8276, 8276, 8305, 8305,
    8319, 8319, 8336, 8348, 8400, 8412, 8417, 8417, 8421, 8432, 8450, 8450, 8455, 8455, 8458, 8467,
    8469, 8469, 8472, 8477, 8484, 8484, 8486, 8486, 8488, 8488, 8490, 8505, 8508, 8511, 8517, 8521,
    8526, 8526, 8544, 8584, 11264, 11492, 11499, 11507, 11520, 11557, 11559, 11559, 11565, 11565, 11568, 11623,
    11631, 11631, 11647, 11670, 11680, 11686, 11688, 11694, 11696, 11702, 11704, 11710, 11712, 11718, 11720, 11726,
    11728, 11734, 11736, 11742, 11744, 11775, 12293, 12295, 12321, 12335, 12337, 12341, 12344, 12348, 12353, 12438,
    12441, 12447, 12449, 12543, 12549, 12591, 12593, 12686, 12704, 12735, 12784, 12799, 13312, 19903, 19968, 42124,
    42192, 42237, 42240, 42508, 42512, 42539, 42560, 42607, 42612, 42621, 42623, 42737, 42775, 42783, 42786, 42888,
    42891, 42954, 42960, 42961, 42963, 42963, 42965, 42969, 42994, 43047, 43052, 43052, 43072, 43123, 43136, 43205,
    43216, 43225, 43232, 43255, 43259, 43259, 43261, 43309, 43312, 43347, 43360, 43388, 43392, 43456, 43471, 43481,
    43488, 43518, 43520, 43574, 43584, 43597, 43600, 43609, 43616, 43638, 43642, 43714, 43739, 43741, 43744, 43759,
    43762, 43766, 43777, 43782, 43785, 43790, 43793, 43798, 43808, 43814, 43816, 43822, 43824, 43866, 43868, 43881,
    43888, 44010, 44012, 44013, 44016, 44025, 44032, 55203, 55216, 55238, 55243, 55291, 63744, 64109, 64112, 64217,
    64256, 64262, 64275, 64279, 64285, 64296, 64298, 64310, 64312, 64316, 64318, 64318, 64320, 64321, 64323, 64324,
    64326, 64433, 64467, 64829, 64848, 64911, 64914, 64967, 65008, 65019, 65024, 65039, 65056, 65071, 65075, 65076,
    65101, 65103, 65136, 65140, 65142, 65276, 65296, 65305, 65313, 65338, 65343, 65343, 65345, 65370, 65381, 65470,
    65474, 65479, 65482, 65487, 65490, 65495, 65498, 65500, 65536, 65547, 65549, 65574, 65576, 65594, 65596, 65597,
    65599, 65613, 65616, 65629, 65664, 65786, 65856, 65908, 66045, 66045, 66176, 66204, 66208, 66256, 66272, 66272,
    66304, 66335, 66349, 66378, 66384, 66426, 66432, 66461, 66464, 66499, 66504, 66511, 66513, 66517, 66560, 66717,
    66720, 66729, 66736, 66771, 66776, 66811, 66816, 66855, 66864, 66915, 66928, 66938, 66940, 66954, 66956, 66962,
    66964, 66965, 66967, 66977, 66979, 66993, 66995, 67001, 67003, 67004, 67072, 67382, 67392, 67413, 67424, 67431,
    67456, 67461, 67463, 67504, 67506, 67514, 67584, 67589, 67592, 67592, 67594, 67637, 67639, 67640, 67644, 67644,
    67647, 67669, 67680, 67702, 67712, 67742, 67808, 67826, 67828, 67829, 67840, 67861, 67872, 67897, 67968, 68023,
    68030, 68031, 68096, 68099, 68101, 68102, 68108, 68115, 68117, 68119, 68121, 68149, 68152, 68154, 68159, 68159,
    68192, 68220, 68224, 68252, 68288, 68295, 68297, 68326, 68352, 68405, 68416, 68437, 68448, 68466, 68480, 68497,
    68608, 68680, 68736, 68786, 68800, 68850, 68864, 68903, 68912, 68921, 69248, 69289, 69291, 69292, 69296, 69297,
    69373, 69404, 69415, 69415, 69424, 69456, 69488, 69509, 69552, 69572, 69600, 69622, 69632, 69702, 69734, 69749,
    69759, 69818, 69826, 69826, 69840, 69864, 69872, 69881, 69888, 69940, 69942, 69951, 69956, 69959, 69968, 70003,
    70006, 70006, 70016, 70084, 70089, 70092, 70094, 70106, 70108, 70108, 70144, 70161, 70163, 70199, 70206, 70209,
    70272, 70278, 70280, 70280, 70282, 70285, 70287, 70301, 70303, 70312, 70320, 70378, 70384, 70393, 70400, 70403,
    70405, 70412, 70415, 70416, 70419, 70440, 70442, 70448, 70450, 70451, 70453, 70457, 70459, 70468, 70471, 70472,
    70475, 70477, 70480, 70480, 70487, 70487, 70493, 70499, 70502, 70508, 70512, 70516, 70656, 70730, 70736, 70745,
    70750, 70753, 70784, 70853, 70855, 70855, 70864, 70873, 71040, 71093, 71096, 71104, 71128, 71133, 71168, 71232,
    71236, 71236, 71248, 71257, 71296, 71352, 71360, 71369, 71424, 71450, 71453, 71467, 71472, 71481, 71488, 71494,
    71680, 71738, 71840, 71913, 71935, 71942, 71945, 71945, 71948, 71955, 71957, 71958, 71960, 71989, 71991, 71992,
    71995, 72003, 72016, 72025, 72096, 72103, 72106, 72151, 72154, 72161, 72163, 72164, 72192, 72254, 72263, 72263,
    72272, 72345, 72349, 72349, 72368, 72440, 72704, 72712, 72714, 72758, 72760, 72768, 72784, 72793, 72818, 72847,
    72850, 72871, 72873, 72886, 72960, 72966, 72968, 72969, 72971, 73014, 73018, 73018, 73020, 73021, 73023, 73031,
    73040, 73049, 73056, 73061, 73063, 73064, 73066, 73102, 73104, 73105, 73107, 73112, 73120, 73129, 73440, 73462,
    73472, 73488, 73490, 73530, 73534, 73538, 73552, 73561, 73648, 73648, 73728, 74649, 74752, 74862, 74880, 75075,
    77712, 77808, 77824, 78895, 78912, 78933, 82944, 83526, 92160, 92728, 92736, 92766, 92768, 92777, 92784, 92862,
    92864, 92873, 92880, 92909, 92912, 92916, 92928, 92982, 92992, 92995, 93008, 93017, 93027, 93047, 93053, 93071,
    93760, 93823, 93952, 94026, 94031, 94087, 94095, 94111, 94176, 94177, 94179, 94180, 94192, 94193, 94208, 100343,
    100352, 101589, 101632, 101640, 110576, 110579, 110581, 110587, 110589, 110590, 110592, 110882, 110898, 110898, 110928, 110930,
    110933, 110933, 110948, 110951, 110960, 111355, 113664, 113770, 113776, 113788, 113792, 113800, 113808, 113817, 113821, 113822,
    118528, 118573, 118576, 118598, 119141, 119145, 119149, 119154, 119163, 119170, 119173, 119179, 119210, 119213, 119362, 119364,
    119808, 119892, 119894, 119964, 119966, 119967, 119970, 119970, 119973, 119974, 119977, 119980, 119982, 119993, 119995, 119995,
    119997, 120003, 120005, 120069, 120071, 120074, 120077, 120084, 120086, 120092, 120094, 120121, 120123, 120126, 120128, 120132,
    120134, 120134, 120138, 120144, 120146, 120485, 120488, 120512, 120514, 120538, 120540, 120570, 120572, 120596, 120598, 120628,
    120630, 120654, 120656, 120686, 120688, 120712, 120714, 120744, 120746, 120770, 120772, 120779, 120782, 120831, 121344, 121398,
    121403, 121452, 121461, 121461, 121476, 121476, 121499, 121503, 121505, 121519, 122624, 122654, 122661, 122666, 122880, 122886,
    122888, 122904, 122907, 122913, 122915, 122916, 122918, 122922, 122928, 122989, 123023, 123023, 123136, 123180, 123184, 123197,
    123200, 123209, 123214, 123214, 123536, 123566, 123584, 123641, 124112, 124153, 124896, 124902, 124904, 124907, 124909, 124910,
    124912, 124926, 124928, 125124, 125136, 125142, 125184, 125259, 125264, 125273, 126464, 126467, 126469, 126495, 126497, 126498,
    126500, 126500, 126503, 126503, 126505, 126514, 126516, 126519, 126521, 126521, 126523, 126523, 126530, 126530, 126535, 126535,
    126537, 126537, 126539, 126539, 126541, 126543, 126545, 126546, 126548, 126548, 126551, 126551, 126553, 126553, 126555, 126555,
    126557, 126557, 126559, 126559, 126561, 126562, 126564, 126564, 126567, 126570, 126572, 126578, 126580, 126583, 126585, 126588,
    126590, 126590, 126592, 126601, 126603, 126619, 126625, 126627, 126629, 126633, 126635, 126651, 130032, 130041, 131072, 173791,
    173824, 177977, 177984, 178205, 178208, 183969, 183984, 191456, 191472, 192093, 194560, 195101, 196608, 201546, 201552, 205743,
    917760, 917999,
};
inline constexpr std::pair<std::string_view, int> keywordTokens[] = {
    {"break", 83},
    {"case", 84},
    {"catch", 85},
    {"class", 86},
    {"const", 87},
    {"continue", 88},
    {"debugger", 89},
    {"default", 90},
    {"delete", 91},
    {"do", 92},
    {"else", 93},
    {"enum", 94},
    {"export", 95},
    {"extends", 96},
    {"false", 97},
    {"finally", 98},
    {"for", 99},
    {"function", 100},
    {"if", 101},
    {"import", 102},
    {"in", 103},
    {"instanceof", 104},
    {"new", 105},
    {"null", 106},
    {"return", 107},
    {"super", 108},
    {"switch", 109},
    {"this", 110},
    {"throw", 111},
    {"true", 112},
    {"try", 113},
    {"typeof", 114},
    {"var", 115},
    {"void", 116},
    {"while", 117},
    {"with", 118},
    {"implements", 119},
    {"interface", 120},
    {"let", 121},
    {"package", 122},
    {"private", 123},
    {"protected", 124},
    {"public", 125},
    {"static", 126},
    {"yield", 127},
    {"abstract", 128},
    {"accessor", 129},
    {"as", 130},
    {"asserts", 131},
    {"assert", 132},
    {"any", 133},
    {"async", 134},
    {"await", 135},
    {"boolean", 136},
    {"constructor", 137},
    {"declare", 138},
    {"get", 139},
    {"infer", 140},
    {"intrinsic", 141},
    {"is", 142},
    {"keyof", 143},
    {"module", 144},
    {"namespace", 145},
    {"never", 146},
    {"out", 147},
    {"readonly", 148},
    {"require", 149},
    {"number", 150},
    {"object", 151},
    {"satisfies", 152},
    {"set", 153},
    {"string", 154},
    {"symbol", 155},
    {"type", 156},
    {"undefined", 157},
    {"unique", 158},
    {"unknown", 159},
    {"using", 160},
    {"from", 161},
    {"global", 162},
    {"bigint", 163},
    {"override", 164},
    {"of", 165},
};
inline constexpr std::pair<std::string_view, int> punctuationTokens[] = {
    {">>>=", 73},
    {"...", 26},
    {"===", 37},
    {"!==", 38},
    {">>>", 50},
    {"**=", 68},
    {"<<=", 71},
    {">>=", 72},
    {"||=", 76},
    {"&&=", 77},
    {"?" "?=", 78},
    {"?.", 29},
    {"</", 31},
    {"<=", 33},
    {">=", 34},
    {"==", 35},
    {"!=", 36},
    {"=>", 39},
    {"**", 43},
    {"++", 46},
    {"--", 47},
    {"<<", 48},
    {">>", 49},
    {"&&", 56},
    {"||", 57},
    {"??", 61},
    {"+=", 65},
    {"-=", 66},
    {"*=", 67},
    {"/=", 69},
    {"%=", 70},
    {"&=", 74},
    {"|=", 75},
    {"^=", 79},
    {"{", 19},
    {"}", 20},
    {"(", 21},
    {")", 22},
    {"[", 23},
    {"]", 24},
    {".", 25},
    {";", 27},
    {",", 28},
    {"<", 30},
    {">", 32},
    {"+", 40},
    {"-", 41},
    {"*", 42},
    {"/", 44},
    {"%", 45},
    {"&", 51},
    {"|", 52},
    {"^", 53},
    {"!", 54},
    {"~", 55},
    {"?", 58},
    {":", 59},
    {"@", 60},
    {"`", 62},
    {"#", 63},
    {"=", 64},
};
}
