import "@testing-library/jest-dom";
import { afterEach } from "vitest";
import { cleanup } from "@testing-library/react";

// Clean up after each test to avoid state leakage between tests
afterEach(() => {
  cleanup();
});
