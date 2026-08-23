// TSX fixture for HighlightFixtureTests. The point of a separate grammar is the
// JSX, so this leans on it: elements, attributes, expression containers and
// fragments alongside ordinary TypeScript types.

import { useMemo, useState, type ReactNode } from "react";

interface RowProps {
    id: number;
    label: string;
    selected?: boolean;
    onPick(id: number): void;
}

const Row = ({ id, label, selected = false, onPick }: RowProps): ReactNode => (
    <li className={selected ? "row row--on" : "row"} onClick={() => onPick(id)}>
        <span className="row__label">{label}</span>
        {selected && <span aria-hidden="true"> &check;</span>}
    </li>
);

export function Picker({ items }: { items: readonly RowProps[] }) {
    const [current, setCurrent] = useState<number | null>(null);
    const sorted = useMemo(
        () => [...items].sort((a, b) => a.label.localeCompare(b.label)),
        [items],
    );

    if (sorted.length === 0) {
        return <p className="empty">Nothing to pick.</p>;
    }

    return (
        <>
            <h2>Pick one of {sorted.length}</h2>
            <ul className="picker">
                {sorted.map((item) => (
                    <Row key={item.id} {...item} selected={item.id === current} onPick={setCurrent} />
                ))}
            </ul>
        </>
    );
}
