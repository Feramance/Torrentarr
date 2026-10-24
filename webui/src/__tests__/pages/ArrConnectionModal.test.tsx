import { expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { ArrInstanceModal } from "../../pages/config/configModals";
import { WebUIProvider } from "../../context/WebUIContext";
import { ToastProvider } from "../../context/ToastContext";
import * as api from "../../api/client";
vi.mock("../../api/client", async (original) => ({
  ...(await original<typeof import("../../api/client")>()),
  getConfig: vi.fn(async () => ({ WebUI: { Theme: "Dark", LiveArr: false } })),
  watchConfiguration: vi.fn(
    (_receive, signal: AbortSignal) =>
      new Promise<void>((resolve) =>
        signal.addEventListener("abort", () => resolve(), { once: true }),
      ),
  ),
  testArrConnection: vi.fn(),
}));

// The regression concerns modal save orchestration, not the hundreds of generated field controls.
vi.mock("../../pages/config/configFieldComponents", async (original) => ({
  ...(await original<
    typeof import("../../pages/config/configFieldComponents")
  >()),
  FieldGroup: () => null,
}));

it.each(["[redacted]", "new-secret"])(
  "tests edited TLS and URI with credential %s before saving",
  async (apiKey) => {
    vi.mocked(api.testArrConnection).mockResolvedValue({
      success: true,
      message: "ok",
    });
    const save = vi.fn(async () => true);
    render(
      <ToastProvider>
        <WebUIProvider>
          <ArrInstanceModal
            keyName="Radarr-new"
            diskKey="Radarr-old"
            state={{
              Managed: true,
              URI: "https://edited.local",
              APIKey: apiKey,
              SkipTLSVerify: true,
            }}
            onChange={vi.fn()}
            onRename={vi.fn()}
            onClose={vi.fn()}
            onSave={save}
            overlapWarnings={[]}
          />
        </WebUIProvider>
      </ToastProvider>,
    );
    const saveButton = screen.getByRole("button", {
      name: "Save",
      exact: true,
    });
    await waitFor(() => expect(saveButton).toBeEnabled());
    fireEvent.click(saveButton);
    await waitFor(() => expect(save).toHaveBeenCalled());
    expect(api.testArrConnection).toHaveBeenLastCalledWith({
      arrType: "radarr",
      uri: "https://edited.local",
      skipTlsVerify: true,
      ...(apiKey === "[redacted]" ? { instanceKey: "Radarr-old" } : { apiKey }),
    });
  },
);
