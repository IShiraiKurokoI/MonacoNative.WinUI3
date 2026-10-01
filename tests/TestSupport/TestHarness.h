#pragma once

#include <exception>
#include <functional>
#include <iostream>
#include <stdexcept>
#include <string>
#include <string_view>

namespace MonacoNative::Tests
{
    class AssertionFailure final : public std::runtime_error
    {
    public:
        explicit AssertionFailure(std::string message)
            : std::runtime_error(std::move(message))
        {
        }
    };

    inline void Check(bool condition, std::string_view expression, const char* file, int line)
    {
        if (!condition)
        {
            throw AssertionFailure(std::string(file) + ":" + std::to_string(line) + " check failed: " + std::string(expression));
        }
    }

    template<typename T, typename U>
    inline void CheckEqual(const T& left, const U& right, std::string_view expression, const char* file, int line)
    {
        if (!(left == right))
        {
            throw AssertionFailure(std::string(file) + ":" + std::to_string(line) + " equality failed: " + std::string(expression));
        }
    }

    template<typename Exception, typename Function>
    inline void CheckThrows(Function&& function, std::string_view expression, const char* file, int line)
    {
        bool sawExpected = false;
        try
        {
            function();
        }
        catch (const Exception&)
        {
            sawExpected = true;
        }
        catch (...)
        {
            throw AssertionFailure(std::string(file) + ":" + std::to_string(line) + " wrong exception: " + std::string(expression));
        }
        if (!sawExpected)
        {
            throw AssertionFailure(std::string(file) + ":" + std::to_string(line) + " expected exception: " + std::string(expression));
        }
    }

    class Suite final
    {
    public:
        explicit Suite(std::string name)
            : name_(std::move(name))
        {
        }

        template<typename Function>
        void Run(std::string_view name, Function&& function)
        {
            try
            {
                function();
                ++passed_;
                std::cout << "[PASS] " << name << '\n';
            }
            catch (const std::exception& error)
            {
                ++failed_;
                std::cerr << "[FAIL] " << name << ": " << error.what() << '\n';
            }
            catch (...)
            {
                ++failed_;
                std::cerr << "[FAIL] " << name << ": unknown exception\n";
            }
        }

        int Finish() const
        {
            std::cout << name_ << ": " << passed_ << " passed, " << failed_ << " failed.\n";
            return failed_ == 0 ? 0 : 1;
        }

    private:
        std::string name_;
        int passed_{};
        int failed_{};
    };
}

#define MN_CHECK(expr) ::MonacoNative::Tests::Check(static_cast<bool>(expr), #expr, __FILE__, __LINE__)
#define MN_CHECK_EQ(left, right) ::MonacoNative::Tests::CheckEqual((left), (right), #left " == " #right, __FILE__, __LINE__)
#define MN_CHECK_THROWS(statement, exception_type) \
    ::MonacoNative::Tests::CheckThrows<exception_type>([&]() { statement; }, #statement, __FILE__, __LINE__)
