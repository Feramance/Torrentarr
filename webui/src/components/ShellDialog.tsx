import { useLayoutEffect, useRef, type ReactNode } from "react";

/** Native modal drawers contain focus; display popovers allow light dismissal. */
export function ShellDialog({
  title,
  onClose,
  children,
  className = "",
  popover = false,
}: {
  title: string;
  onClose: () => void;
  children: ReactNode;
  className?: string;
  popover?: boolean;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  useLayoutEffect(() => {
    const trigger = document.activeElement as HTMLElement | null;
    const dialog = ref.current!;
    if (popover) dialog.showPopover();
    else dialog.showModal();
    return () => {
      if (popover) dialog.hidePopover();
      else dialog.close();
      trigger?.focus();
    };
  }, [popover]);
  return (
    <dialog
      ref={ref}
      className={`shell-dialog ${className}`}
      aria-label={title}
      popover={popover ? "auto" : undefined}
      onToggle={(event) => {
        if (popover && (event.nativeEvent as ToggleEvent).newState === "closed")
          onClose();
      }}
      onCancel={(event) => {
        event.preventDefault();
        onClose();
      }}
      onClick={(event) => {
        if (event.target === event.currentTarget) onClose();
      }}
    >
      <div className="shell-dialog__heading">
        <strong>{title}</strong>
        <button
          type="button"
          className="btn ghost"
          onClick={onClose}
          aria-label={`Close ${title}`}
        >
          Close
        </button>
      </div>
      {children}
    </dialog>
  );
}
