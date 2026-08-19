import { describe, expect, it } from "vitest";
import {
  arrTypeFromSectionName,
  defaultFileExtensionAllowlist,
  isArrSection,
  READARR_ALLOWLIST,
  supportsRequestIntegration,
  supportsSearchByYear,
} from "../../config/arrSections";

describe("arrSections", () => {
  it("recognizes Readarr section names", () => {
    expect(isArrSection("Readarr-Books")).toBe(true);
    expect(arrTypeFromSectionName("Readarr-Books")).toBe("readarr");
    expect(arrTypeFromSectionName("readarr")).toBe("readarr");
  });

  it("hides Ombi/Overseerr and keeps SearchByYear for Readarr", () => {
    expect(supportsRequestIntegration("readarr")).toBe(false);
    expect(supportsSearchByYear("readarr")).toBe(true);
    expect(supportsSearchByYear("lidarr")).toBe(false);
  });

  it("uses audiobook-inclusive allowlist defaults for Readarr", () => {
    const allowlist = defaultFileExtensionAllowlist("readarr");
    expect(allowlist).toEqual(READARR_ALLOWLIST);
    expect(allowlist).toContain(".m4b");
    expect(allowlist).toContain(".flac");
    expect(allowlist).toContain(".epub");
  });
});
