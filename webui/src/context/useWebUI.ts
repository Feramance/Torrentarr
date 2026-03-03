import { useContext } from "react";
import { WebUIContext } from "./WebUIContext";
import type { WebUIContextValue } from "./WebUIContext";

export function useWebUI(): WebUIContextValue {
  const context = useContext(WebUIContext);
  if (!context) {
    throw new Error("useWebUI must be used within WebUIProvider");
  }
  return context;
}
