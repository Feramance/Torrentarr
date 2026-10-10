import { describe, expect, it } from "vitest";
import type { Hashable } from "./dataSync";
import {
  RowsStore,
  createEmptyRowsSnapshot,
  syncRowsSnapshot,
  type RowsStoreOptions,
} from "./rowsStore";

interface Row extends Hashable {
  id: string;
  title: string;
  score: number;
}

const opts: RowsStoreOptions<Row> = {
  getKey: (row) => row.id,
  hashFields: ["title", "score"],
};

describe("syncRowsSnapshot", () => {
  it("returns noop and same snapshot reference when nothing changed", () => {
    const prev = createEmptyRowsSnapshot<Row>();
    const first = syncRowsSnapshot(
      prev,
      [{ id: "1", title: "A", score: 1 }],
      opts,
    );
    const second = syncRowsSnapshot(
      first.snapshot,
      [{ id: "1", title: "A", score: 1 }],
      opts,
    );
    expect(second.changeKind).toBe("noop");
    expect(second.snapshot).toBe(first.snapshot);
  });

  it("marks update-only when row content changes without add/remove", () => {
    const seeded = syncRowsSnapshot(
      createEmptyRowsSnapshot<Row>(),
      [{ id: "1", title: "A", score: 1 }],
      opts,
    );
    const updated = syncRowsSnapshot(
      seeded.snapshot,
      [{ id: "1", title: "A", score: 2 }],
      opts,
    );
    expect(updated.changeKind).toBe("update-only");
    expect(updated.snapshot.rowOrder).toBe(seeded.snapshot.rowOrder);
    expect(updated.updated).toHaveLength(1);
  });

  it("marks add-remove when membership changes", () => {
    const seeded = syncRowsSnapshot(
      createEmptyRowsSnapshot<Row>(),
      [{ id: "1", title: "A", score: 1 }],
      opts,
    );
    const next = syncRowsSnapshot(
      seeded.snapshot,
      [
        { id: "1", title: "A", score: 1 },
        { id: "2", title: "B", score: 3 },
      ],
      opts,
    );
    expect(next.changeKind).toBe("add-remove");
    expect(next.added).toHaveLength(1);
    expect(next.snapshot.rowOrder).not.toBe(seeded.snapshot.rowOrder);
  });
});

describe("row order and replacement", () => {
  it("applies server ordering even when membership and contents are unchanged", () => {
    const rows: Row[] = [
      { id: "1", title: "A", score: 1 },
      { id: "2", title: "B", score: 2 },
    ];
    const first = syncRowsSnapshot(createEmptyRowsSnapshot<Row>(), rows, opts);
    const next = syncRowsSnapshot(first.snapshot, [...rows].reverse(), opts);
    expect(next.snapshot.rowOrder).toEqual(["2", "1"]);
    expect(next.changeKind).toBe("add-remove");
    expect(next.snapshot.rowVersionsById.get("1")).toBe(1);
  });

  it("notifies removed rows when replacing with an empty list", () => {
    const store = new RowsStore<Row>(opts);
    store.sync([{ id: "1", title: "A", score: 1 }]);
    let notified = 0;
    store.subscribeRow("1", () => notified++);
    store.replace([]);
    expect(notified).toBe(1);
    expect(store.getRow("1")).toBeUndefined();
  });
});
