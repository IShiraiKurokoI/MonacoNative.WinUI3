#pragma once

#include <cstdint>
#include <functional>
#include <mutex>
#include <unordered_map>
#include <utility>
#include <vector>

namespace MonacoNative::Core
{
    template<typename T>
    class EventSource final
    {
    public:
        using Handler = std::function<void(const T&)>;
        using Token = std::uint64_t;

        [[nodiscard]] Token Add(Handler handler)
        {
            std::scoped_lock lock(mutex_);
            const auto token = nextToken_++;
            handlers_.emplace(token, std::move(handler));
            return token;
        }

        void Remove(Token token)
        {
            std::scoped_lock lock(mutex_);
            handlers_.erase(token);
        }

        void Emit(const T& value) const
        {
            std::vector<Handler> snapshot;
            {
                std::scoped_lock lock(mutex_);
                snapshot.reserve(handlers_.size());
                for (const auto& [_, handler] : handlers_)
                {
                    snapshot.push_back(handler);
                }
            }

            for (const auto& handler : snapshot)
            {
                handler(value);
            }
        }

    private:
        mutable std::mutex mutex_{};
        std::unordered_map<Token, Handler> handlers_{};
        Token nextToken_{1};
    };
}
