import { useEffect, useRef, type JSX } from "react";
import { IconImage } from "./IconImage";
import CloseIcon from "../icons/close.svg";

interface ConfirmDialogProps {
  title: string;
  message: string;
  confirmLabel?: string;
  cancelLabel?: string;
  onConfirm: () => void;
  onCancel: () => void;
  danger?: boolean;
}

export function ConfirmDialog({
  title,
  message,
  confirmLabel = "Confirm",
  cancelLabel = "Cancel",
  onConfirm,
  onCancel,
  danger = false,
}: ConfirmDialogProps): JSX.Element {
  const dialog = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    dialog.current?.querySelector<HTMLButtonElement>("button")?.focus();
    return () => previous?.focus();
  }, []);
  return (
    <div className="modal-backdrop">
      <div
        className="modal"
        ref={dialog}
        role="dialog"
        onKeyDown={(event) => {
          if (event.key === "Escape") {
            event.preventDefault();
            onCancel();
          }
          if (event.key !== "Tab") return;
          const buttons = dialog.current?.querySelectorAll<HTMLButtonElement>(
            "button:not(:disabled)",
          );
          if (!buttons?.length) return;
          const first = buttons[0],
            last = buttons[buttons.length - 1];
          if (event.shiftKey && document.activeElement === first) {
            event.preventDefault();
            last.focus();
          } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault();
            first.focus();
          }
        }}
        aria-modal="true"
        aria-labelledby="confirm-dialog-title"
        style={{ maxWidth: "500px" }}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="modal-header">
          <h2 id="confirm-dialog-title">{title}</h2>
          <button className="btn ghost" onClick={onCancel} aria-label="Close">
            <IconImage src={CloseIcon} />
          </button>
        </div>
        <div className="modal-body">
          <p style={{ margin: 0, lineHeight: 1.6 }}>{message}</p>
        </div>
        <div className="modal-footer">
          <button className="btn ghost" onClick={onCancel}>
            {cancelLabel}
          </button>
          <button
            className={`btn ${danger ? "danger" : "primary"}`}
            onClick={onConfirm}
          >
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
