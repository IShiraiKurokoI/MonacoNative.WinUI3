// JavaScript sample: basic members, arrow functions and parameter hints.
const greeting = (name = 'MonacoNative') => `Hello, ${name}!`;

class Counter {
    constructor(initial = 0) {
        this.value = initial;
    }

    increment() {
        this.value += 1;
        return this.value;
    }
}

const counter = new Counter();
for (let index = 0; index < 3; index++) {
    console.log(greeting(`visitor ${counter.increment()}`));
}

export { Counter, greeting };

// Type "counter." for value/increment, or "greeting(" for parameter hints.
// Ctrl+Shift+Space also works inside a string argument.
