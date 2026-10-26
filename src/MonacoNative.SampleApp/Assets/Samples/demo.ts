// TypeScript sample: basic declarations, members and parameter hints.
interface Greeting {
    name: string;
    count: number;
}

class Greeter {
    prefix: string;

    constructor(prefix: string = "Hello") {
        this.prefix = prefix;
    }

    greet(person: Greeting, suffix: string = "!"): string {
        return `${this.prefix}, ${person.name}${suffix}`;
    }
}

const createGreeter = (prefix: string = "Hello"): Greeter => new Greeter(prefix);

// #region Usage
const person: Greeting = {
    name: "Monaco",
    count: 1,
};

const greeter = createGreeter("Hello");
console.log(greeter.greet(person));
// #endregion

// Type "person.", "greeter." or "console." for members.
// Type "greeter.greet(" and a comma to see active parameter hints.
// Ctrl+Shift+Space requests hints manually; no package resolver is required.
