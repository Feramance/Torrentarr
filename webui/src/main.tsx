import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "./styles.css";
import App from "./App";
import { handleChunkPreloadError } from "./utils/chunkRecovery";

const rootElement = document.getElementById("root");

if (rootElement) {
  createRoot(rootElement).render(
    <StrictMode>
      <App />
    </StrictMode>,
  );
}

if (import.meta.env.PROD) {
  window.addEventListener("vite:preloadError", (event) => {
    // The sessionStorage getter itself can throw when browser policy denies storage.
    // Prevent Vite from surfacing an unhandled preload exception in that case.
    event.preventDefault();
    try {
      handleChunkPreloadError(
        event,
        window.sessionStorage,
        () => window.location.reload(),
        Date.now(),
      );
    } catch {
      // Do not reload without the storage guard; it could loop indefinitely.
    }
  });
}

if ("serviceWorker" in navigator && import.meta.env.PROD) {
  window.addEventListener("load", () => {
    const base = new URL(document.baseURI);
    void navigator.serviceWorker
      .register(new URL("sw.js", base), { scope: base.pathname })
      .catch(() => undefined);
  });
}
