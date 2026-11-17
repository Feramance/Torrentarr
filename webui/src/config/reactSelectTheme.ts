import type { CSSObjectWithLabel } from "react-select";

/** Config and Logs controls inherit the console's theme and density tokens. */
export function getSelectStyles(isDark: boolean) {
  return {
    control: (base: CSSObjectWithLabel, state: { isFocused: boolean }) => ({
      ...base,
      colorScheme: isDark ? "dark" : "light",
      background: "var(--control-bg)",
      color: "var(--on-surface)",
      borderColor: state.isFocused ? "var(--accent)" : "var(--control-border)",
      minHeight: "var(--control-height)",
      borderRadius: "4px",
      boxShadow: "none",
      outline: state.isFocused ? "2px solid var(--accent)" : "none",
      outlineOffset: "1px",
      "&:hover": { borderColor: "var(--muted)" },
    }),
    menu: (base: CSSObjectWithLabel) => ({
      ...base,
      background: "var(--surface)",
      border: "1px solid var(--control-border)",
      borderRadius: "4px",
      boxShadow: "var(--overlay-shadow)",
      zIndex: 10,
    }),
    option: (
      base: CSSObjectWithLabel,
      state: { isFocused: boolean; isSelected: boolean },
    ) => ({
      ...base,
      background: state.isSelected
        ? "var(--select-selected-bg)"
        : state.isFocused
          ? "var(--select-focused-bg)"
          : "var(--surface)",
      color: state.isSelected
        ? "var(--select-selected-text)"
        : "var(--on-surface)",
      borderRadius: "2px",
      "&:active": { background: "var(--select-focused-bg)" },
    }),
    singleValue: (base: CSSObjectWithLabel) => ({
      ...base,
      color: "var(--on-surface)",
    }),
    input: (base: CSSObjectWithLabel) => ({
      ...base,
      color: "var(--on-surface)",
      margin: 0,
      padding: 0,
    }),
    placeholder: (base: CSSObjectWithLabel) => ({
      ...base,
      color: "var(--muted)",
    }),
    valueContainer: (base: CSSObjectWithLabel) => ({
      ...base,
      padding: "2px 8px",
    }),
    multiValue: (base: CSSObjectWithLabel) => ({
      ...base,
      background: "var(--control-hover-bg)",
      borderRadius: "2px",
    }),
    multiValueLabel: (base: CSSObjectWithLabel) => ({
      ...base,
      color: "var(--on-surface)",
    }),
    multiValueRemove: (base: CSSObjectWithLabel) => ({
      ...base,
      color: "var(--muted)",
      ":hover": { color: "var(--danger)", background: "transparent" },
    }),
    menuList: (base: CSSObjectWithLabel) => ({ ...base, padding: "4px" }),
  };
}
