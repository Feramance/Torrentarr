import { afterEach, describe, expect, it, vi } from "vitest";
import { watchConfiguration } from "../../api/client";
import type { LiveConfigurationEvent } from "../../api/types";

afterEach(() => vi.restoreAllMocks());

describe("live configuration stream", () => {
  it("decodes split events using the normal authenticated fetch path", async () => {
    const event: LiveConfigurationEvent = {
      application: {
        acceptedRevision: 42,
        state: "pending",
        components: { worker: { appliedRevision: 41 } },
      },
      webui: {
        host: "0.0.0.0",
        port: 6969,
        urlBase: "/live",
        behindHttpsProxy: false,
        theme: "Dark",
        viewDensity: "Compact",
        liveArr: true,
      },
    };
    const bytes = new TextEncoder().encode(
      `data: ${JSON.stringify(event)}\n\n`,
    );
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(bytes.slice(0, 13));
        controller.enqueue(bytes.slice(13));
        controller.close();
      },
    });
    const fetch = vi
      .spyOn(globalThis, "fetch")
      .mockResolvedValue(new Response(body));
    const receive = vi.fn();
    const controller = new AbortController();
    await watchConfiguration(receive, controller.signal);
    expect(receive).toHaveBeenCalledExactlyOnceWith(event);
    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining("/web/config/events"),
      expect.objectContaining({ signal: controller.signal }),
    );
  });

  it("rejects an unsuccessful stream without reporting applied settings", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(null, { status: 503 }),
    );
    const receive = vi.fn();
    await expect(
      watchConfiguration(receive, new AbortController().signal),
    ).rejects.toThrow("Configuration stream unavailable");
    expect(receive).not.toHaveBeenCalled();
  });
});
