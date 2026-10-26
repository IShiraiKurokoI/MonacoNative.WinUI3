namespace demo {

class Greeter {
public:
    explicit Greeter(const char* name) noexcept : name_(name) {}

    const char* name() const noexcept {
        return name_;
    }

    int greet(int repetitions, const char* punctuation) const noexcept {
        return name_ && punctuation ? repetitions : 0;
    }

    int count = 0;

private:
    const char* name_;
};

}  // namespace demo

int main() {
    demo::Greeter greeter("Monaco");
    greeter.count = greeter.greet(3, "!");
    // Type "greeter." or "pointer->" to inspect real Clang members.
    auto* pointer = &greeter;
    return pointer->count;
}
