#pragma once

#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <string_view>
#include <unordered_map>
#include <variant>
#include <vector>

namespace MonacoNative::Core
{
    using ContextKeyValue = std::variant<std::monostate, bool, std::int64_t, double, std::string>;

    class ContextKeyService final
    {
    public:
        void Set(std::string key, ContextKeyValue value);
        bool Reset(std::string_view key);
        [[nodiscard]] bool Contains(std::string_view key) const;
        [[nodiscard]] std::optional<ContextKeyValue> Get(std::string_view key) const;
        [[nodiscard]] bool Evaluate(std::string_view expression) const;
        [[nodiscard]] std::uint64_t Version() const noexcept;

    private:
        std::unordered_map<std::string, ContextKeyValue> values_{};
        std::uint64_t version_{1};
    };

    struct CommandDescriptor final
    {
        std::string id{};
        std::string title{};
        std::string category{};
        std::string when{};
    };

    class CommandRegistry final
    {
    public:
        using Handler = std::function<bool()>;
        using RegistrationToken = std::uint64_t;

        RegistrationToken Register(CommandDescriptor descriptor, Handler handler);
        bool Unregister(RegistrationToken token);
        [[nodiscard]] bool Contains(std::string_view commandId) const;
        [[nodiscard]] bool CanExecute(std::string_view commandId, const ContextKeyService& context) const;
        bool Execute(std::string_view commandId, const ContextKeyService& context) const;
        [[nodiscard]] std::vector<CommandDescriptor> VisibleCommands(const ContextKeyService& context) const;

    private:
        struct Entry final
        {
            RegistrationToken token{};
            CommandDescriptor descriptor{};
            Handler handler{};
        };

        std::unordered_map<std::string, Entry> entries_{};
        std::unordered_map<RegistrationToken, std::string> tokenToId_{};
        RegistrationToken nextToken_{1};
    };

    enum KeyModifier : std::uint8_t
    {
        KeyModifierNone = 0,
        KeyModifierCtrl = 1 << 0,
        KeyModifierShift = 1 << 1,
        KeyModifierAlt = 1 << 2,
        KeyModifierMeta = 1 << 3,
    };

    struct KeyStroke final
    {
        std::string key{};
        std::uint8_t modifiers{KeyModifierNone};

        friend bool operator==(const KeyStroke&, const KeyStroke&) = default;
    };

    struct KeybindingRule final
    {
        std::vector<KeyStroke> chord{};
        std::string commandId{};
        std::string when{};
        std::int32_t weight{};
        bool preventDefault{true};
    };

    enum class KeybindingResultKind : std::uint8_t
    {
        NoMatch,
        Command,
        MoreChordsNeeded,
        ChordCancelled,
    };

    struct KeybindingResult final
    {
        KeybindingResultKind kind{KeybindingResultKind::NoMatch};
        std::string commandId{};
        bool preventDefault{};
        std::string chordLabel{};
    };

    class KeybindingResolver final
    {
    public:
        using RegistrationToken = std::uint64_t;

        RegistrationToken Register(KeybindingRule rule);
        bool Unregister(RegistrationToken token);
        void Clear();
        void ResetChord();
        [[nodiscard]] bool InChordMode() const noexcept;
        [[nodiscard]] std::string PendingChordLabel() const;
        [[nodiscard]] KeybindingResult Resolve(KeyStroke stroke, const ContextKeyService& context);

        // Keybinding resolver diagnostics used by portable regression/performance tests. These counters
        // describe the most recent Resolve call and intentionally expose work units rather
        // than wall-clock timings so large-table pruning remains deterministic in CI.
        [[nodiscard]] std::size_t RuleCount() const noexcept;
        [[nodiscard]] std::size_t LastFirstChordCandidateCount() const noexcept;
        [[nodiscard]] std::size_t LastPrefixCandidateCount() const noexcept;
        [[nodiscard]] std::size_t LastContextEvaluationCount() const noexcept;

        [[nodiscard]] static KeyStroke Normalize(KeyStroke stroke);
        [[nodiscard]] static std::vector<KeyStroke> ParseChord(std::string_view specification);
        [[nodiscard]] static std::string FormatChord(const std::vector<KeyStroke>& chord);

    private:
        struct RegisteredRule final
        {
            RegistrationToken token{};
            KeybindingRule rule{};
            std::uint64_t order{};
            std::uint64_t cachedContextVersion{};
            bool cachedContextMatch{};
        };

        struct KeyStrokeHash final
        {
            [[nodiscard]] std::size_t operator()(const KeyStroke& stroke) const noexcept;
        };

        [[nodiscard]] static bool IsPrefix(
            const std::vector<KeyStroke>& sequence,
            const std::vector<KeyStroke>& chord);
        [[nodiscard]] const RegisteredRule* FindCommand(
            const std::vector<KeyStroke>& sequence,
            const ContextKeyService& context);
        void SortBucket(std::vector<RegistrationToken>& bucket);

        std::unordered_map<RegistrationToken, RegisteredRule> rules_{};
        std::unordered_map<KeyStroke, std::vector<RegistrationToken>, KeyStrokeHash> firstChordIndex_{};
        std::vector<KeyStroke> pending_{};
        RegistrationToken nextToken_{1};
        std::uint64_t nextOrder_{1};
        std::size_t lastFirstChordCandidateCount_{};
        std::size_t lastPrefixCandidateCount_{};
        std::size_t lastContextEvaluationCount_{};
    };
}
