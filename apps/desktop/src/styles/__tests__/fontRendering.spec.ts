import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const globalsCss = readFileSync(new URL("../globals.css", import.meta.url), "utf8");

describe("global font rendering", () => {
  it("leaves font smoothing to the operating system", () => {
    expect(globalsCss).not.toContain("-webkit-font-smoothing");
    expect(globalsCss).not.toContain("-moz-osx-font-smoothing");
  });
});
