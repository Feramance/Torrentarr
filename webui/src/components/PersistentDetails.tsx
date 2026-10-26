import { useEffect, useRef, type ComponentPropsWithoutRef } from "react";

/** Apply the initial layout once; later updates preserve the user's choice. */
export function PersistentDetails({
  defaultOpen = false,
  reveal = false,
  ...props
}: Omit<ComponentPropsWithoutRef<"details">, "open" | "defaultOpen"> & {
  defaultOpen?: boolean;
  reveal?: boolean;
}) {
  const details = useRef<HTMLDetailsElement>(null);
  const initialOpen = useRef(defaultOpen);
  useEffect(() => {
    if (details.current) details.current.open = initialOpen.current;
  }, []);
  useEffect(() => {
    if (reveal && details.current) details.current.open = true;
  }, [reveal]);
  return <details {...props} ref={details} />;
}
