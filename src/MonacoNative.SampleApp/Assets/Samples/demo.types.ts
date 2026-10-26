export interface Greeting {
    name: string;
    count: number;
}

export class Greeter {
    prefix: string;

    constructor(prefix: string) {
        this.prefix = prefix;
    }

    greet(person: Greeting): string {
        return `${this.prefix}, ${person.name}!`;
    }
}

export function createGreeter(prefix: string): Greeter {
    return new Greeter(prefix);
}
