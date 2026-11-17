import {
  useCallback,
  useRef,
  useState,
  type Dispatch,
  type SetStateAction,
} from "react";

export interface InstanceSelectionProps {
  selectedInstance?: string;
  onSelectInstance?: (instance: string) => void;
}

export function useInstanceSelection({
  selectedInstance,
  onSelectInstance,
}: InstanceSelectionProps): [string, Dispatch<SetStateAction<string>>] {
  const [local, setLocal] = useState("");
  const selection = selectedInstance ?? local;
  const notify = useRef(onSelectInstance);
  notify.current = onSelectInstance;
  const current = useRef(selection);
  current.current = selection;
  const setSelection = useCallback<Dispatch<SetStateAction<string>>>((next) => {
    const value = typeof next === "function" ? next(current.current) : next;
    if (value === current.current) return;
    current.current = value;
    if (notify.current) notify.current(value);
    else setLocal(value);
  }, []);
  return [selection, setSelection];
}
