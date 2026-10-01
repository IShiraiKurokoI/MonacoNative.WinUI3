#include <winrt/base.h>
#include <winrt/MonacoNative.WinRT.h>
#include <type_traits>

int main()
{
    // Compile/link smoke only. Runtime activation registration is owned by the consuming app.
    using Host = winrt::MonacoNative::WinRT::EditorHost;
    static_assert(std::is_copy_constructible_v<Host>);
    return 0;
}
