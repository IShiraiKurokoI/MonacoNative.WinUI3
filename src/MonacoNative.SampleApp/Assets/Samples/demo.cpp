#include <iostream>
#include <string>
#include <vector>
#include <utility>

namespace demo {

class Greeter {
public:
    explicit Greeter(std::string name) : name_(std::move(name)) {}

    void print() const {
        std::cout << "Hello, " << name_ << "!\n";
    }

private:
    std::string name_;
};

}  // namespace demo

int main() {
    std::vector<std::string> names { "Monaco", "WinUI", "C++" };
    for (const auto& name : names) {
        demo::Greeter(name).print();
    }
    return 0;
}
