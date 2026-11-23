import { useLayoutEffect, useRef, useState, type KeyboardEvent } from "react";

interface TagInputProps {
  id?: string;
  ariaLabel?: string;
  invalid?: boolean;
  value: string[];
  onChange: (value: string[]) => void;
  placeholder?: string;
  disabled?: boolean;
}

export function TagInput({
  id,
  value,
  onChange,
  placeholder,
  disabled,
  ariaLabel,
  invalid,
}: TagInputProps) {
  const [inputValue, setInputValue] = useState("");
  const [editing, setEditing] = useState<{ index: number; text: string }>();
  const addInput = useRef<HTMLInputElement>(null);
  const canUse = (text: string, index = -1) =>
    !!text.trim() &&
    !value.some((tag, i) => i !== index && tag === text.trim());
  const add = () => {
    if (!canUse(inputValue)) return;
    onChange([...value, inputValue.trim()]);
    setInputValue("");
    addInput.current?.focus();
  };
  const editInput = useRef<HTMLInputElement>(null);
  const restoreFocus = useRef<number | null>(null);
  const editButtons = useRef<Array<HTMLButtonElement | null>>([]);
  const editIndex = editing?.index;
  useLayoutEffect(() => {
    if (editIndex !== undefined) {
      editInput.current?.focus();
    } else if (restoreFocus.current !== null) {
      editButtons.current[restoreFocus.current]?.focus();
      restoreFocus.current = null;
    }
  }, [editIndex]);
  const closeEdit = (index: number) => {
    restoreFocus.current = index;
    setEditing(undefined);
  };
  const update = () => {
    if (!editing) return;
    const { index, text } = editing;
    if (text === value[index]) {
      closeEdit(index);
    } else if (canUse(text, index)) {
      onChange(value.map((tag, i) => (i === index ? text.trim() : tag)));
      closeEdit(index);
    }
  };
  const onEnter = (
    event: KeyboardEvent<HTMLInputElement>,
    commit: () => void,
  ) => {
    if (event.key === "Enter" && !event.nativeEvent.isComposing) {
      event.preventDefault();
      commit();
    }
  };

  return (
    <div className="config-value-list">
      {value.length > 0 && (
        <ul>
          {value.map((tag, index) => (
            <li key={index} className="config-value-row">
              {editing?.index === index ? (
                <>
                  <input
                    ref={editInput}
                    aria-label={`Edit value ${index + 1}`}
                    value={editing.text}
                    onChange={(event) =>
                      setEditing({ index, text: event.target.value })
                    }
                    onKeyDown={(event) => {
                      onEnter(event, update);
                      if (event.key === "Escape") closeEdit(index);
                    }}
                  />
                  <button
                    type="button"
                    className="btn"
                    onClick={update}
                    disabled={
                      editing.text !== tag && !canUse(editing.text, index)
                    }
                  >
                    Done
                  </button>
                  <button
                    type="button"
                    className="btn"
                    onClick={() => closeEdit(index)}
                  >
                    Cancel
                  </button>
                </>
              ) : (
                <span>{tag}</span>
              )}
              {!disabled && (
                <>
                  <button
                    ref={(button) => {
                      editButtons.current[index] = button;
                    }}
                    hidden={editing?.index === index}
                    type="button"
                    className="btn"
                    aria-label={`Edit ${tag}`}
                    onClick={() => setEditing({ index, text: tag })}
                  >
                    Edit
                  </button>
                  <button
                    type="button"
                    className="btn"
                    aria-label={`Remove ${tag}`}
                    onClick={() => {
                      setEditing(undefined);
                      onChange(value.filter((_, i) => i !== index));
                      addInput.current?.focus();
                    }}
                  >
                    Remove
                  </button>
                </>
              )}
            </li>
          ))}
        </ul>
      )}
      <div className="config-value-row">
        <input
          ref={addInput}
          id={id}
          aria-label={ariaLabel}
          aria-invalid={invalid || undefined}
          type="text"
          value={inputValue}
          onChange={(event) => setInputValue(event.target.value)}
          onKeyDown={(event) => onEnter(event, add)}
          placeholder={placeholder || "New value"}
          disabled={disabled}
        />
        <button
          type="button"
          className="btn"
          onClick={add}
          disabled={disabled || !canUse(inputValue)}
        >
          Add
        </button>
      </div>
    </div>
  );
}
