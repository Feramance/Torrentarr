import { describe, it, expect, vi } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { Pagination } from "../../components/Pagination";

const noop = vi.fn();

const base = {
  currentPage: 0,
  totalPages: 5,
  totalItems: 100,
  pageSize: 20,
  onPageChange: noop,
};

describe("Pagination – empty / hidden", () => {
  it("renders an empty div (no .pagination class) when totalPages is 0", () => {
    const { container } = render(<Pagination {...base} totalPages={0} />);
    expect(container.querySelector(".pagination")).not.toBeInTheDocument();
  });

  it("renders an empty div when totalPages is negative", () => {
    const { container } = render(<Pagination {...base} totalPages={-1} />);
    expect(container.querySelector(".pagination")).not.toBeInTheDocument();
  });
});

describe("Pagination – info text", () => {
  it("shows current page number (1-indexed)", () => {
    render(<Pagination {...base} currentPage={0} />);
    expect(screen.getByText(/page 1 of 5/i)).toBeInTheDocument();
  });

  it("reflects currentPage in info text", () => {
    render(<Pagination {...base} currentPage={2} />);
    expect(screen.getByText(/page 3 of 5/i)).toBeInTheDocument();
  });

  it("shows totalItems formatted with locale", () => {
    render(<Pagination {...base} totalItems={1000} />);
    // toLocaleString produces "1,000" in en-US; just check the raw digits appear
    expect(screen.getByText(/1,000|1000/)).toBeInTheDocument();
  });

  it("shows custom itemsLabel", () => {
    render(<Pagination {...base} itemsLabel="movies" />);
    expect(screen.getByText(/movies/)).toBeInTheDocument();
  });

  it("shows page size", () => {
    render(<Pagination {...base} pageSize={50} />);
    expect(screen.getByText(/page size 50/i)).toBeInTheDocument();
  });
});

describe("Pagination – button disabled states", () => {
  it("disables First and Prev on the first page", () => {
    render(<Pagination {...base} currentPage={0} />);
    expect(screen.getByTitle("First page")).toBeDisabled();
    expect(screen.getByTitle("Previous page")).toBeDisabled();
  });

  it("enables Next and Last on the first page", () => {
    render(<Pagination {...base} currentPage={0} />);
    expect(screen.getByTitle("Next page")).not.toBeDisabled();
    expect(screen.getByTitle("Last page")).not.toBeDisabled();
  });

  it("disables Next and Last on the last page", () => {
    render(<Pagination {...base} currentPage={4} totalPages={5} />);
    expect(screen.getByTitle("Next page")).toBeDisabled();
    expect(screen.getByTitle("Last page")).toBeDisabled();
  });

  it("enables First and Prev on the last page", () => {
    render(<Pagination {...base} currentPage={4} totalPages={5} />);
    expect(screen.getByTitle("First page")).not.toBeDisabled();
    expect(screen.getByTitle("Previous page")).not.toBeDisabled();
  });

  it("enables all nav buttons on a middle page", () => {
    render(<Pagination {...base} currentPage={2} />);
    expect(screen.getByTitle("First page")).not.toBeDisabled();
    expect(screen.getByTitle("Previous page")).not.toBeDisabled();
    expect(screen.getByTitle("Next page")).not.toBeDisabled();
    expect(screen.getByTitle("Last page")).not.toBeDisabled();
  });

  it("disables all buttons when loading=true", () => {
    render(<Pagination {...base} currentPage={2} loading={true} />);
    screen.getAllByRole("button").forEach((btn) =>
      expect(btn).toBeDisabled()
    );
  });

  it("Go button is disabled when jump input is empty", () => {
    render(<Pagination {...base} />);
    expect(screen.getByTitle("Jump to page")).toBeDisabled();
  });
});

describe("Pagination – navigation callbacks", () => {
  it("calls onPageChange(0) when First button clicked", () => {
    const onPageChange = vi.fn();
    render(<Pagination {...base} currentPage={3} onPageChange={onPageChange} />);

    fireEvent.click(screen.getByTitle("First page"));

    expect(onPageChange).toHaveBeenCalledWith(0);
  });

  it("calls onPageChange(currentPage-1) when Prev clicked", () => {
    const onPageChange = vi.fn();
    render(<Pagination {...base} currentPage={2} onPageChange={onPageChange} />);

    fireEvent.click(screen.getByTitle("Previous page"));

    expect(onPageChange).toHaveBeenCalledWith(1);
  });

  it("calls onPageChange(currentPage+1) when Next clicked", () => {
    const onPageChange = vi.fn();
    render(<Pagination {...base} currentPage={1} onPageChange={onPageChange} />);

    fireEvent.click(screen.getByTitle("Next page"));

    expect(onPageChange).toHaveBeenCalledWith(2);
  });

  it("calls onPageChange(totalPages-1) when Last clicked", () => {
    const onPageChange = vi.fn();
    render(
      <Pagination {...base} currentPage={0} totalPages={5} onPageChange={onPageChange} />
    );

    fireEvent.click(screen.getByTitle("Last page"));

    expect(onPageChange).toHaveBeenCalledWith(4);
  });
});

describe("Pagination – jump input", () => {
  it("calls onPageChange with 0-indexed page when Go clicked", () => {
    const onPageChange = vi.fn();
    render(<Pagination {...base} currentPage={0} onPageChange={onPageChange} />);

    fireEvent.change(screen.getByPlaceholderText("Page"), {
      target: { value: "3" },
    });
    fireEvent.click(screen.getByTitle("Jump to page"));

    // Page 3 (1-indexed) → index 2
    expect(onPageChange).toHaveBeenCalledWith(2);
  });

  it("calls onPageChange when Enter is pressed in jump input", () => {
    const onPageChange = vi.fn();
    render(<Pagination {...base} currentPage={0} onPageChange={onPageChange} />);

    const input = screen.getByPlaceholderText("Page");
    fireEvent.change(input, { target: { value: "2" } });
    fireEvent.keyDown(input, { key: "Enter" });

    expect(onPageChange).toHaveBeenCalledWith(1);
  });

  it("does not call onPageChange for page 0 (out of range)", () => {
    const onPageChange = vi.fn();
    render(<Pagination {...base} onPageChange={onPageChange} />);

    fireEvent.change(screen.getByPlaceholderText("Page"), {
      target: { value: "0" },
    });
    fireEvent.click(screen.getByTitle("Jump to page"));

    expect(onPageChange).not.toHaveBeenCalled();
  });

  it("does not call onPageChange for page > totalPages", () => {
    const onPageChange = vi.fn();
    render(
      <Pagination {...base} totalPages={5} onPageChange={onPageChange} />
    );

    fireEvent.change(screen.getByPlaceholderText("Page"), {
      target: { value: "99" },
    });
    fireEvent.click(screen.getByTitle("Jump to page"));

    expect(onPageChange).not.toHaveBeenCalled();
  });

  it("clears jump input after successful jump", () => {
    render(<Pagination {...base} />);

    const input = screen.getByPlaceholderText("Page") as HTMLInputElement;
    fireEvent.change(input, { target: { value: "2" } });
    fireEvent.click(screen.getByTitle("Jump to page"));

    expect(input.value).toBe("");
  });
});
