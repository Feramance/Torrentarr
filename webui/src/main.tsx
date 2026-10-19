import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "./styles.css";
import App from "./App";

const rootElement = document.getElementById("root");

if (rootElement) {
  createRoot(rootElement).render(
    <StrictMode>
      <App />
    </StrictMode>,
  );
}

if ("serviceWorker" in navigator && import.meta.env.PROD) {
  window.addEventListener("load", () => {
    const base = new URL(document.baseURI);
    void navigator.serviceWorker
      .register(new URL("sw.js", base), { scope: base.pathname })
      .catch(() => undefined);
  });
}
