#include "MonacoNative/Core/Commands.h"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <regex>
#include <sstream>
#include <stdexcept>
#include <utility>

namespace MonacoNative::Core
{
    namespace
    {
        enum class TokenKind
        {
            End,
            Identifier,
            String,
            Number,
            True,
            False,
            LParen,
            RParen,
            And,
            Or,
            Not,
            Equal,
            NotEqual,
            Less,
            LessEqual,
            Greater,
            GreaterEqual,
            RegexMatch,
        };

        struct Token final
        {
            TokenKind kind{TokenKind::End};
            std::string text{};
            double number{};
        };

        class Lexer final
        {
        public:
            explicit Lexer(std::string_view source)
                : source_(source)
            {
            }

            Token Next()
            {
                SkipWhitespace();
                if (position_ >= source_.size())
                {
                    return {};
                }

                const auto ch = source_[position_];
                if (ch == '(')
                {
                    ++position_;
                    return Token{TokenKind::LParen, "("};
                }
                if (ch == ')')
                {
                    ++position_;
                    return Token{TokenKind::RParen, ")"};
                }
                if (Match("&&")) return Token{TokenKind::And, "&&"};
                if (Match("||")) return Token{TokenKind::Or, "||"};
                if (Match("==")) return Token{TokenKind::Equal, "=="};
                if (Match("!=")) return Token{TokenKind::NotEqual, "!="};
                if (Match("<=")) return Token{TokenKind::LessEqual, "<="};
                if (Match(">=")) return Token{TokenKind::GreaterEqual, ">="};
                if (Match("=~")) return Token{TokenKind::RegexMatch, "=~"};
                if (ch == '!')
                {
                    ++position_;
                    return Token{TokenKind::Not, "!"};
                }
                if (ch == '<')
                {
                    ++position_;
                    return Token{TokenKind::Less, "<"};
                }
                if (ch == '>')
                {
                    ++position_;
                    return Token{TokenKind::Greater, ">"};
                }
                if (ch == '\'' || ch == '"')
                {
                    return ReadString(ch);
                }
                if (std::isdigit(static_cast<unsigned char>(ch)) != 0 ||
                    (ch == '-' && position_ + 1 < source_.size() && std::isdigit(static_cast<unsigned char>(source_[position_ + 1])) != 0))
                {
                    return ReadNumber();
                }
                if (IsIdentifierStart(ch))
                {
                    return ReadIdentifier();
                }
                throw std::invalid_argument("Invalid context-key expression token.");
            }

        private:
            static bool IsIdentifierStart(char ch) noexcept
            {
                return std::isalpha(static_cast<unsigned char>(ch)) != 0 || ch == '_' || ch == '$';
            }

            static bool IsIdentifierPart(char ch) noexcept
            {
                return std::isalnum(static_cast<unsigned char>(ch)) != 0 || ch == '_' || ch == '$' || ch == '.' || ch == '-' || ch == ':';
            }

            void SkipWhitespace()
            {
                while (position_ < source_.size() && std::isspace(static_cast<unsigned char>(source_[position_])) != 0)
                {
                    ++position_;
                }
            }

            bool Match(std::string_view value)
            {
                if (source_.substr(position_, value.size()) != value)
                {
                    return false;
                }
                position_ += value.size();
                return true;
            }

            Token ReadString(char quote)
            {
                ++position_;
                std::string result;
                while (position_ < source_.size())
                {
                    const auto ch = source_[position_++];
                    if (ch == quote)
                    {
                        return Token{TokenKind::String, std::move(result)};
                    }
                    if (ch == '\\' && position_ < source_.size())
                    {
                        const auto escaped = source_[position_++];
                        switch (escaped)
                        {
                        case 'n': result.push_back('\n'); break;
                        case 'r': result.push_back('\r'); break;
                        case 't': result.push_back('\t'); break;
                        default: result.push_back(escaped); break;
                        }
                    }
                    else
                    {
                        result.push_back(ch);
                    }
                }
                throw std::invalid_argument("Unterminated string in context-key expression.");
            }

            Token ReadNumber()
            {
                const auto start = position_;
                if (source_[position_] == '-')
                {
                    ++position_;
                }
                while (position_ < source_.size() && std::isdigit(static_cast<unsigned char>(source_[position_])) != 0)
                {
                    ++position_;
                }
                if (position_ < source_.size() && source_[position_] == '.')
                {
                    ++position_;
                    while (position_ < source_.size() && std::isdigit(static_cast<unsigned char>(source_[position_])) != 0)
                    {
                        ++position_;
                    }
                }
                const auto text = std::string(source_.substr(start, position_ - start));
                try
                {
                    return Token{TokenKind::Number, text, std::stod(text)};
                }
                catch (...)
                {
                    throw std::invalid_argument("Invalid numeric literal in context-key expression.");
                }
            }

            Token ReadIdentifier()
            {
                const auto start = position_++;
                while (position_ < source_.size() && IsIdentifierPart(source_[position_]))
                {
                    ++position_;
                }
                auto text = std::string(source_.substr(start, position_ - start));
                if (text == "true") return Token{TokenKind::True, std::move(text)};
                if (text == "false") return Token{TokenKind::False, std::move(text)};
                return Token{TokenKind::Identifier, std::move(text)};
            }

            std::string_view source_{};
            std::size_t position_{};
        };

        bool IsNumeric(const ContextKeyValue& value) noexcept
        {
            return std::holds_alternative<std::int64_t>(value) || std::holds_alternative<double>(value);
        }

        double AsNumber(const ContextKeyValue& value)
        {
            if (const auto* integer = std::get_if<std::int64_t>(&value)) return static_cast<double>(*integer);
            if (const auto* number = std::get_if<double>(&value)) return *number;
            throw std::invalid_argument("Context value is not numeric.");
        }

        bool Truthy(const ContextKeyValue& value) noexcept
        {
            if (std::holds_alternative<std::monostate>(value)) return false;
            if (const auto* boolean = std::get_if<bool>(&value)) return *boolean;
            if (const auto* integer = std::get_if<std::int64_t>(&value)) return *integer != 0;
            if (const auto* number = std::get_if<double>(&value)) return *number != 0.0 && !std::isnan(*number);
            if (const auto* text = std::get_if<std::string>(&value)) return !text->empty();
            return false;
        }

        std::string AsString(const ContextKeyValue& value)
        {
            if (std::holds_alternative<std::monostate>(value)) return {};
            if (const auto* boolean = std::get_if<bool>(&value)) return *boolean ? "true" : "false";
            if (const auto* integer = std::get_if<std::int64_t>(&value)) return std::to_string(*integer);
            if (const auto* number = std::get_if<double>(&value))
            {
                std::ostringstream stream;
                stream << *number;
                return stream.str();
            }
            return std::get<std::string>(value);
        }

        bool EqualValues(const ContextKeyValue& left, const ContextKeyValue& right)
        {
            if (IsNumeric(left) && IsNumeric(right))
            {
                return AsNumber(left) == AsNumber(right);
            }
            if (left.index() == right.index())
            {
                return left == right;
            }
            return AsString(left) == AsString(right);
        }

        class ExpressionParser final
        {
        public:
            ExpressionParser(std::string_view expression, const ContextKeyService& context)
                : lexer_(expression), context_(context), current_(lexer_.Next())
            {
            }

            bool Parse()
            {
                const auto result = ParseOr();
                if (current_.kind != TokenKind::End)
                {
                    throw std::invalid_argument("Unexpected trailing tokens in context-key expression.");
                }
                return result;
            }

        private:
            bool ParseOr()
            {
                auto value = ParseAnd();
                while (current_.kind == TokenKind::Or)
                {
                    Advance();
                    const auto right = ParseAnd();
                    value = value || right;
                }
                return value;
            }

            bool ParseAnd()
            {
                auto value = ParseUnary();
                while (current_.kind == TokenKind::And)
                {
                    Advance();
                    const auto right = ParseUnary();
                    value = value && right;
                }
                return value;
            }

            bool ParseUnary()
            {
                if (current_.kind == TokenKind::Not)
                {
                    Advance();
                    return !ParseUnary();
                }
                if (current_.kind == TokenKind::LParen)
                {
                    Advance();
                    const auto result = ParseOr();
                    Expect(TokenKind::RParen);
                    return result;
                }
                return ParsePredicate();
            }

            bool ParsePredicate()
            {
                if (current_.kind != TokenKind::Identifier && current_.kind != TokenKind::True && current_.kind != TokenKind::False &&
                    current_.kind != TokenKind::String && current_.kind != TokenKind::Number)
                {
                    throw std::invalid_argument("Expected context key or literal.");
                }

                const auto left = ParseValue(true);
                const auto op = current_.kind;
                switch (op)
                {
                case TokenKind::Equal:
                case TokenKind::NotEqual:
                case TokenKind::Less:
                case TokenKind::LessEqual:
                case TokenKind::Greater:
                case TokenKind::GreaterEqual:
                case TokenKind::RegexMatch:
                    Advance();
                    break;
                default:
                    return Truthy(left);
                }

                const auto right = ParseValue(false);
                if (op == TokenKind::Equal) return EqualValues(left, right);
                if (op == TokenKind::NotEqual) return !EqualValues(left, right);
                if (op == TokenKind::RegexMatch)
                {
                    try
                    {
                        return std::regex_search(AsString(left), std::regex(AsString(right)));
                    }
                    catch (const std::regex_error&)
                    {
                        return false;
                    }
                }

                if (IsNumeric(left) && IsNumeric(right))
                {
                    const auto a = AsNumber(left);
                    const auto b = AsNumber(right);
                    if (op == TokenKind::Less) return a < b;
                    if (op == TokenKind::LessEqual) return a <= b;
                    if (op == TokenKind::Greater) return a > b;
                    return a >= b;
                }

                const auto a = AsString(left);
                const auto b = AsString(right);
                if (op == TokenKind::Less) return a < b;
                if (op == TokenKind::LessEqual) return a <= b;
                if (op == TokenKind::Greater) return a > b;
                return a >= b;
            }

            ContextKeyValue ParseValue(bool identifiersAreKeys)
            {
                const auto token = current_;
                Advance();
                switch (token.kind)
                {
                case TokenKind::Identifier:
                    if (identifiersAreKeys)
                    {
                        return context_.Get(token.text).value_or(ContextKeyValue{});
                    }
                    return token.text;
                case TokenKind::True: return true;
                case TokenKind::False: return false;
                case TokenKind::String: return token.text;
                case TokenKind::Number:
                {
                    const auto rounded = std::round(token.number);
                    if (std::fabs(token.number - rounded) < 1e-12)
                    {
                        return static_cast<std::int64_t>(rounded);
                    }
                    return token.number;
                }
                default:
                    throw std::invalid_argument("Expected literal value.");
                }
            }

            void Advance()
            {
                current_ = lexer_.Next();
            }

            void Expect(TokenKind kind)
            {
                if (current_.kind != kind)
                {
                    throw std::invalid_argument("Malformed context-key expression.");
                }
                Advance();
            }

            Lexer lexer_;
            const ContextKeyService& context_;
            Token current_{};
        };

        std::string Trim(std::string_view value)
        {
            std::size_t start = 0;
            while (start < value.size() && std::isspace(static_cast<unsigned char>(value[start])) != 0) ++start;
            std::size_t end = value.size();
            while (end > start && std::isspace(static_cast<unsigned char>(value[end - 1])) != 0) --end;
            return std::string(value.substr(start, end - start));
        }

        std::string UpperAscii(std::string value)
        {
            for (auto& ch : value)
            {
                ch = static_cast<char>(std::toupper(static_cast<unsigned char>(ch)));
            }
            return value;
        }

        std::vector<std::string> SplitWhitespace(std::string_view value)
        {
            std::vector<std::string> result;
            std::size_t start = 0;
            while (start < value.size())
            {
                while (start < value.size() && std::isspace(static_cast<unsigned char>(value[start])) != 0) ++start;
                if (start >= value.size()) break;
                auto end = start;
                while (end < value.size() && std::isspace(static_cast<unsigned char>(value[end])) == 0) ++end;
                result.emplace_back(value.substr(start, end - start));
                start = end;
            }
            return result;
        }

        std::vector<std::string> SplitPlus(std::string_view value)
        {
            std::vector<std::string> result;
            std::size_t start = 0;
            while (start <= value.size())
            {
                const auto end = value.find('+', start);
                result.push_back(Trim(value.substr(start, end == std::string_view::npos ? value.size() - start : end - start)));
                if (end == std::string_view::npos) break;
                start = end + 1;
            }
            return result;
        }
    }

    void ContextKeyService::Set(std::string key, ContextKeyValue value)
    {
        if (key.empty())
        {
            throw std::invalid_argument("Context key cannot be empty.");
        }
        const auto iterator = values_.find(key);
        if (iterator != values_.end() && iterator->second == value)
        {
            return;
        }
        values_[std::move(key)] = std::move(value);
        ++version_;
    }

    bool ContextKeyService::Reset(std::string_view key)
    {
        const auto erased = values_.erase(std::string(key));
        if (erased != 0)
        {
            ++version_;
            return true;
        }
        return false;
    }

    bool ContextKeyService::Contains(std::string_view key) const
    {
        return values_.contains(std::string(key));
    }

    std::optional<ContextKeyValue> ContextKeyService::Get(std::string_view key) const
    {
        const auto iterator = values_.find(std::string(key));
        if (iterator == values_.end()) return std::nullopt;
        return iterator->second;
    }

    bool ContextKeyService::Evaluate(std::string_view expression) const
    {
        if (Trim(expression).empty()) return true;
        return ExpressionParser(expression, *this).Parse();
    }

    std::uint64_t ContextKeyService::Version() const noexcept
    {
        return version_;
    }

    CommandRegistry::RegistrationToken CommandRegistry::Register(CommandDescriptor descriptor, Handler handler)
    {
        if (descriptor.id.empty()) throw std::invalid_argument("Command id cannot be empty.");
        if (!handler) throw std::invalid_argument("Command handler cannot be empty.");
        if (entries_.contains(descriptor.id)) throw std::invalid_argument("Command id is already registered.");
        const auto token = nextToken_++;
        const auto id = descriptor.id;
        entries_.emplace(id, Entry{token, std::move(descriptor), std::move(handler)});
        tokenToId_.emplace(token, id);
        return token;
    }

    bool CommandRegistry::Unregister(RegistrationToken token)
    {
        const auto tokenIterator = tokenToId_.find(token);
        if (tokenIterator == tokenToId_.end()) return false;
        entries_.erase(tokenIterator->second);
        tokenToId_.erase(tokenIterator);
        return true;
    }

    bool CommandRegistry::Contains(std::string_view commandId) const
    {
        return entries_.contains(std::string(commandId));
    }

    bool CommandRegistry::CanExecute(std::string_view commandId, const ContextKeyService& context) const
    {
        const auto iterator = entries_.find(std::string(commandId));
        return iterator != entries_.end() && context.Evaluate(iterator->second.descriptor.when);
    }

    bool CommandRegistry::Execute(std::string_view commandId, const ContextKeyService& context) const
    {
        const auto iterator = entries_.find(std::string(commandId));
        if (iterator == entries_.end() || !context.Evaluate(iterator->second.descriptor.when)) return false;
        return iterator->second.handler();
    }

    std::vector<CommandDescriptor> CommandRegistry::VisibleCommands(const ContextKeyService& context) const
    {
        std::vector<CommandDescriptor> result;
        result.reserve(entries_.size());
        for (const auto& [id, entry] : entries_)
        {
            (void)id;
            if (context.Evaluate(entry.descriptor.when)) result.push_back(entry.descriptor);
        }
        std::sort(result.begin(), result.end(), [](const CommandDescriptor& a, const CommandDescriptor& b)
        {
            if (a.category != b.category) return a.category < b.category;
            if (a.title != b.title) return a.title < b.title;
            return a.id < b.id;
        });
        return result;
    }

    KeybindingResolver::RegistrationToken KeybindingResolver::Register(KeybindingRule rule)
    {
        if (rule.commandId.empty()) throw std::invalid_argument("Keybinding command id cannot be empty.");
        if (rule.chord.empty()) throw std::invalid_argument("Keybinding chord cannot be empty.");
        if (rule.chord.size() > 4) throw std::invalid_argument("Keybinding chord is limited to four strokes.");
        for (auto& stroke : rule.chord) stroke = Normalize(std::move(stroke));

        // Validate the when clause at registration time. Monaco stores parsed context-key
        // expressions on resolved keybinding items, so malformed expressions must not survive
        // until the first dispatch attempt.
        if (!Trim(rule.when).empty())
        {
            ContextKeyService validationContext;
            (void)validationContext.Evaluate(rule.when);
        }

        const auto token = nextToken_++;
        const auto firstChord = rule.chord.front();
        rules_.emplace(token, RegisteredRule{token, std::move(rule), nextOrder_++, 0, false});
        auto& bucket = firstChordIndex_[firstChord];
        bucket.push_back(token);
        SortBucket(bucket);
        return token;
    }

    bool KeybindingResolver::Unregister(RegistrationToken token)
    {
        const auto iterator = rules_.find(token);
        if (iterator == rules_.end()) return false;
        const auto firstChord = iterator->second.rule.chord.front();
        if (const auto bucketIterator = firstChordIndex_.find(firstChord); bucketIterator != firstChordIndex_.end())
        {
            auto& bucket = bucketIterator->second;
            bucket.erase(std::remove(bucket.begin(), bucket.end(), token), bucket.end());
            if (bucket.empty()) firstChordIndex_.erase(bucketIterator);
        }
        rules_.erase(iterator);
        return true;
    }

    void KeybindingResolver::Clear()
    {
        rules_.clear();
        firstChordIndex_.clear();
        pending_.clear();
        lastFirstChordCandidateCount_ = 0;
        lastPrefixCandidateCount_ = 0;
        lastContextEvaluationCount_ = 0;
    }

    void KeybindingResolver::ResetChord()
    {
        pending_.clear();
    }

    bool KeybindingResolver::InChordMode() const noexcept
    {
        return !pending_.empty();
    }

    std::string KeybindingResolver::PendingChordLabel() const
    {
        return FormatChord(pending_);
    }

    KeybindingResult KeybindingResolver::Resolve(KeyStroke stroke, const ContextKeyService& context)
    {
        stroke = Normalize(std::move(stroke));
        auto sequence = pending_;
        sequence.push_back(std::move(stroke));

        // Monaco 0.57 first narrows by the first chord, then walks the precedence-ordered
        // candidates backwards and chooses the first prefix whose when clause matches.
        // Only after choosing that rule does it decide whether another chord is required.
        // This matters when a higher-priority multi-stroke binding shadows a lower-priority
        // single-stroke binding sharing the same first keypress.
        if (const auto* match = FindCommand(sequence, context))
        {
            if (sequence.size() < match->rule.chord.size())
            {
                pending_ = std::move(sequence);
                return KeybindingResult{KeybindingResultKind::MoreChordsNeeded, {}, true, FormatChord(pending_)};
            }

            const auto result = KeybindingResult{
                KeybindingResultKind::Command,
                match->rule.commandId,
                match->rule.preventDefault,
                FormatChord(sequence),
            };
            pending_.clear();
            return result;
        }

        if (!pending_.empty())
        {
            const auto cancelled = FormatChord(sequence);
            pending_.clear();
            return KeybindingResult{KeybindingResultKind::ChordCancelled, {}, true, cancelled};
        }

        return {};
    }

    KeyStroke KeybindingResolver::Normalize(KeyStroke stroke)
    {
        stroke.key = UpperAscii(Trim(stroke.key));
        if (stroke.key.empty()) throw std::invalid_argument("Key stroke cannot be empty.");

        // Keep one logical identity for WinUI/Win32 aliases and user-facing spellings.
        // The canonical names intentionally remain compatible with the project's existing
        // registrations while removing layout/API spelling differences at the resolver edge.
        if (stroke.key == "ESC") stroke.key = "ESCAPE";
        else if (stroke.key == "RETURN") stroke.key = "ENTER";
        else if (stroke.key == "BACKSPACE") stroke.key = "BACK";
        else if (stroke.key == "SPACEBAR") stroke.key = "SPACE";
        else if (stroke.key == "DEL") stroke.key = "DELETE";
        else if (stroke.key == "INS") stroke.key = "INSERT";
        else if (stroke.key == "PGUP" || stroke.key == "PRIOR") stroke.key = "PAGEUP";
        else if (stroke.key == "PGDN" || stroke.key == "NEXT") stroke.key = "PAGEDOWN";
        else if (stroke.key == "LEFTARROW") stroke.key = "LEFT";
        else if (stroke.key == "RIGHTARROW") stroke.key = "RIGHT";
        else if (stroke.key == "UPARROW") stroke.key = "UP";
        else if (stroke.key == "DOWNARROW") stroke.key = "DOWN";
        else if (stroke.key == "." || stroke.key == "PERIOD" || stroke.key == "OEMPERIOD" || stroke.key == "OEM_PERIOD") stroke.key = "OEMPERIOD";
        stroke.modifiers &= static_cast<std::uint8_t>(KeyModifierCtrl | KeyModifierShift | KeyModifierAlt | KeyModifierMeta);
        return stroke;
    }

    std::vector<KeyStroke> KeybindingResolver::ParseChord(std::string_view specification)
    {
        const auto strokes = SplitWhitespace(specification);
        if (strokes.empty()) throw std::invalid_argument("Chord specification cannot be empty.");
        if (strokes.size() > 4) throw std::invalid_argument("Chord specification is limited to four strokes.");

        std::vector<KeyStroke> result;
        result.reserve(strokes.size());
        for (const auto& strokeSpec : strokes)
        {
            const auto parts = SplitPlus(strokeSpec);
            if (parts.empty()) throw std::invalid_argument("Malformed key stroke.");
            KeyStroke stroke{};
            for (std::size_t i = 0; i + 1 < parts.size(); ++i)
            {
                const auto modifier = UpperAscii(parts[i]);
                if (modifier == "CTRL" || modifier == "CONTROL") stroke.modifiers |= KeyModifierCtrl;
                else if (modifier == "SHIFT") stroke.modifiers |= KeyModifierShift;
                else if (modifier == "ALT" || modifier == "OPTION") stroke.modifiers |= KeyModifierAlt;
                else if (modifier == "META" || modifier == "CMD" || modifier == "WIN" || modifier == "WINDOWS") stroke.modifiers |= KeyModifierMeta;
                else throw std::invalid_argument("Unknown key modifier in chord specification.");
            }
            stroke.key = parts.back();
            result.push_back(Normalize(std::move(stroke)));
        }
        return result;
    }

    std::string KeybindingResolver::FormatChord(const std::vector<KeyStroke>& chord)
    {
        std::string result;
        for (std::size_t i = 0; i < chord.size(); ++i)
        {
            if (i != 0) result.push_back(' ');
            const auto& stroke = chord[i];
            if ((stroke.modifiers & KeyModifierCtrl) != 0) result += "Ctrl+";
            if ((stroke.modifiers & KeyModifierShift) != 0) result += "Shift+";
            if ((stroke.modifiers & KeyModifierAlt) != 0) result += "Alt+";
            if ((stroke.modifiers & KeyModifierMeta) != 0) result += "Meta+";
            result += UpperAscii(stroke.key);
        }
        return result;
    }

    bool KeybindingResolver::IsPrefix(const std::vector<KeyStroke>& prefix, const std::vector<KeyStroke>& chord)
    {
        return prefix.size() <= chord.size() && std::equal(prefix.begin(), prefix.end(), chord.begin());
    }

    std::size_t KeybindingResolver::KeyStrokeHash::operator()(const KeyStroke& stroke) const noexcept
    {
        const auto keyHash = std::hash<std::string>{}(stroke.key);
        const auto modifierHash = std::hash<std::uint8_t>{}(stroke.modifiers);
        return keyHash ^ (modifierHash + 0x9e3779b9U + (keyHash << 6U) + (keyHash >> 2U));
    }

    const KeybindingResolver::RegisteredRule* KeybindingResolver::FindCommand(
        const std::vector<KeyStroke>& sequence,
        const ContextKeyService& context)
    {
        lastFirstChordCandidateCount_ = 0;
        lastPrefixCandidateCount_ = 0;
        lastContextEvaluationCount_ = 0;
        if (sequence.empty()) return nullptr;

        const auto bucketIterator = firstChordIndex_.find(sequence.front());
        if (bucketIterator == firstChordIndex_.end()) return nullptr;
        const auto& bucket = bucketIterator->second;
        lastFirstChordCandidateCount_ = bucket.size();

        // Buckets are sorted low -> high priority. Monaco's resolver scans matches in reverse,
        // therefore the first context-matching prefix is the winner.
        for (auto iterator = bucket.rbegin(); iterator != bucket.rend(); ++iterator)
        {
            const auto ruleIterator = rules_.find(*iterator);
            if (ruleIterator == rules_.end()) continue;
            auto& candidate = ruleIterator->second;
            if (!IsPrefix(sequence, candidate.rule.chord)) continue;
            ++lastPrefixCandidateCount_;
            const auto contextVersion = context.Version();
            if (candidate.cachedContextVersion != contextVersion)
            {
                ++lastContextEvaluationCount_;
                candidate.cachedContextMatch = context.Evaluate(candidate.rule.when);
                candidate.cachedContextVersion = contextVersion;
            }
            if (!candidate.cachedContextMatch) continue;
            return &candidate;
        }
        return nullptr;
    }

    void KeybindingResolver::SortBucket(std::vector<RegistrationToken>& bucket)
    {
        std::stable_sort(bucket.begin(), bucket.end(), [&](RegistrationToken a, RegistrationToken b)
        {
            const auto& left = rules_.at(a);
            const auto& right = rules_.at(b);
            if (left.rule.weight != right.rule.weight) return left.rule.weight < right.rule.weight;
            return left.order < right.order;
        });
    }

    std::size_t KeybindingResolver::RuleCount() const noexcept
    {
        return rules_.size();
    }

    std::size_t KeybindingResolver::LastFirstChordCandidateCount() const noexcept
    {
        return lastFirstChordCandidateCount_;
    }

    std::size_t KeybindingResolver::LastPrefixCandidateCount() const noexcept
    {
        return lastPrefixCandidateCount_;
    }

    std::size_t KeybindingResolver::LastContextEvaluationCount() const noexcept
    {
        return lastContextEvaluationCount_;
    }
}
