// JavaScript sample: syntax tokens, scopes, folding and search.
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
