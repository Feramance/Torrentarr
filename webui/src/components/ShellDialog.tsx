import { useLayoutEffect, useRef, type ReactNode } from "react";

function positionPopover(
  dialog: HTMLDialogElement,
  anchor: HTMLElement,
  preferAbove: boolean,
) {
  const gap = 8;
  const width = document.documentElement.clientWidth || window.innerWidth;
  const height = document.documentElement.clientHeight || window.innerHeight;
  const trigger = anchor.getBoundingClientRect();
  const panelWidth = dialog.offsetWidth;
  const panelHeight =
    dialog.scrollHeight + dialog.offsetHeight - dialog.clientHeight;
  const right = trigger.right + gap;
  const left = trigger.left - gap - panelWidth;
  const above = trigger.top - 2 * gap;
  const below = height - trigger.bottom - 2 * gap;
  const fitsRight = right + panelWidth <= width - gap;
  const beside = !preferAbove && (fitsRight || left >= gap);
  const openAbove = preferAbove
    ? panelHeight <= above || above >= below
    : panelHeight > below && above > below;
  const availableHeight = beside ? height - 2 * gap : openAbove ? above : below;
  dialog.style.maxHeight = `${Math.max(0, availableHeight)}px`;
  const panelBottom = beside ? trigger.bottom : trigger.top - gap;
  const top =
    beside || openAbove
      ? panelBottom - dialog.offsetHeight
      : trigger.bottom + gap;
  const x = beside ? (fitsRight ? right : left) : trigger.left;
  dialog.style.left = `${Math.max(gap, Math.min(x, width - panelWidth - gap))}px`;
  dialog.style.top = `${Math.max(gap, Math.min(top, height - dialog.offsetHeight - gap))}px`;
}

/** An anchor opens the dialog as a popover above the current surface. */
export function ShellDialog({
  title,
  onClose,
  children,
  className = "",
  anchor,
  preferAbove = false,
}: {
  title: string;
  onClose: () => void;
  children: ReactNode;
  className?: string;
  anchor?: HTMLButtonElement;
  preferAbove?: boolean;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  const closeButton = useRef<HTMLButtonElement>(null);
  const onCloseRef = useRef(onClose);
  useLayoutEffect(() => {
    onCloseRef.current = onClose;
  }, [onClose]);
  useLayoutEffect(() => {
    const dialog = ref.current!;
    if (!anchor) {
      const trigger = document.activeElement as HTMLElement | null;
      dialog.showModal();
      return () => {
        dialog.close();
        trigger?.focus();
      };
    }

    const container = anchor.closest(".console-sidebar, .navigation-drawer");
    const reposition = () => {
      if (container && getComputedStyle(container).display === "none") {
        onCloseRef.current();
        return;
      }
      positionPopover(dialog, anchor, preferAbove);
    };
    const dismissOutside = (event: MouseEvent) => {
      if (
        event.target instanceof Node &&
        !dialog.contains(event.target) &&
        !anchor.contains(event.target)
      ) {
        onCloseRef.current();
      }
    };
    const dismissOnEscape = (event: KeyboardEvent) => {
      if (event.key !== "Escape") return;
      event.preventDefault();
      event.stopPropagation();
      anchor.focus({ preventScroll: true });
      onCloseRef.current();
    };
    dialog.showPopover();
    reposition();
    closeButton.current?.focus({ preventScroll: true });
    window.addEventListener("resize", reposition);
    document.addEventListener("scroll", reposition, true);
    document.addEventListener("click", dismissOutside);
    document.addEventListener("keydown", dismissOnEscape, true);
    const observer = new ResizeObserver(reposition);
    observer.observe(dialog);
    observer.observe(anchor);
    if (container) observer.observe(container);
    return () => {
      observer.disconnect();
      window.removeEventListener("resize", reposition);
      document.removeEventListener("scroll", reposition, true);
      document.removeEventListener("click", dismissOutside);
      document.removeEventListener("keydown", dismissOnEscape, true);
      dialog.hidePopover();
    };
  }, [anchor, preferAbove]);

  return (
    <dialog
      ref={ref}
      className={`shell-dialog ${className}`}
      aria-label={title}
      popover={anchor ? "manual" : undefined}
      onCancel={(event) => {
        event.preventDefault();
        onClose();
      }}
      onClick={(event) => {
        if (!anchor && event.target === event.currentTarget) onClose();
      }}
    >
      <div className="shell-dialog__heading">
        <strong>{title}</strong>
        <button
          ref={closeButton}
          type="button"
          className="btn ghost"
          onClick={() => {
            anchor?.focus({ preventScroll: true });
            onClose();
          }}
          aria-label={`Close ${title}`}
        >
          Close
        </button>
      </div>
      {children}
    </dialog>
  );
}
