import { useCallback, useEffect, useRef, useState } from "react";
import type { KeyboardEvent, MouseEvent, PointerEvent } from "react";

/** Transient chart inspection shared by plots and segmented statistics. */
export function useChartInspection<T>(scope: string, active = true) {
  const [selection, setSelection] = useState<{
    scope: string;
    value: T;
  } | null>(null);
  const root = useRef<Element | null>(null);
  const clear = useCallback(() => setSelection(null), []);
  const ref = useCallback((node: Element | null) => {
    root.current = node;
  }, []);
  const inspect = (value: T) =>
    setSelection((previous) =>
      previous?.scope === scope && Object.is(previous.value, value)
        ? previous
        : { scope, value },
    );
  const value = active && selection?.scope === scope ? selection.value : null;

  useEffect(clear, [scope, active, clear]);
  const inspecting = value !== null;
  useEffect(() => {
    if (!inspecting) return;
    const outside = (event: globalThis.PointerEvent) => {
      if (!root.current?.contains(event.target as Node)) clear();
    };
    const escape = (event: globalThis.KeyboardEvent) => {
      if (event.key === "Escape") clear();
    };
    document.addEventListener("pointerdown", outside, true);
    document.addEventListener("keydown", escape);
    return () => {
      document.removeEventListener("pointerdown", outside, true);
      document.removeEventListener("keydown", escape);
    };
  }, [inspecting, clear]);

  const leave = (event: PointerEvent) => {
    if (event.pointerType !== "touch") clear();
  };
  const bind = (point: T) => ({
    onPointerEnter: (event: PointerEvent) => {
      if (event.pointerType !== "touch") inspect(point);
    },
    onPointerDown: () => inspect(point),
    onPointerLeave: leave,
    onPointerCancel: clear,
    onFocus: () => inspect(point),
    onBlur: clear,
    onClick: (event: MouseEvent) => {
      event.stopPropagation();
      inspect(point);
    },
    onKeyDown: (event: KeyboardEvent) => {
      if (event.key === "Escape") clear();
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        event.stopPropagation();
        inspect(point);
      }
    },
  });
  return { value, ref, inspect, clear, leave, bind };
}
