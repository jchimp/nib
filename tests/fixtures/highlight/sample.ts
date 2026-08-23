// TypeScript fixture for HighlightFixtureTests. Exercises the constructs that
// distinguish source.ts from source.js: type annotations, interfaces, enums,
// generics, decorators and access modifiers.

import { readFile } from "node:fs/promises";

export type Level = "debug" | "info" | "warn" | "error";

export interface Entry {
    readonly at: Date;
    level: Level;
    message: string;
    fields?: Record<string, unknown>;
}

export enum Exit {
    Ok = 0,
    Usage = 64,
}

const DEFAULT_LEVEL: Level = "info";

export abstract class Sink<T extends Entry = Entry> {
    protected constructor(private readonly name: string) {}

    abstract write(entry: T): Promise<void>;

    get label(): string {
        return `${this.name}@${DEFAULT_LEVEL}`;
    }
}

export async function load(path: string): Promise<Entry[]> {
    const raw = await readFile(path, "utf8");
    const parsed = JSON.parse(raw) as unknown;

    if (!Array.isArray(parsed)) {
        throw new TypeError(`expected an array in ${path}`);
    }

    return parsed.filter((e): e is Entry => typeof e === "object" && e !== null);
}

function count<K extends string>(entries: readonly Entry[], key: K): number {
    let total = 0;
    for (const entry of entries) {
        if (entry.fields?.[key] !== undefined) total += 1;
    }
    return total;
}

export default { load, count, Exit };
