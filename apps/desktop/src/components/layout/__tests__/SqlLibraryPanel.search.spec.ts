// @vitest-environment happy-dom
import { createApp, nextTick, type App } from "vue";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, describe, expect, it, vi } from "vitest";
import i18n from "@/i18n";
import { useSavedSqlStore } from "@/stores/savedSqlStore";
import type { SavedSqlFile } from "@/types/database";
import { changeSearchMode } from "./sqlSearchTestUtils";

vi.mock("@/components/ui/select", () => import("./sqlSearchTestUtils"));
vi.mock("@/lib/backend/tauriRuntime", () => ({ isTauriRuntime: () => false }));
import SqlLibraryPanel from "../SqlLibraryPanel.vue";

const mounted: Array<{ app: App; host: HTMLElement }> = [];
const timestamp = "2026-10-01T00:00:00Z";
function file(id: string, name: string, sql: string, sqlLoaded = true): SavedSqlFile {
  return { id, name, sql, sqlLoaded, connectionId: "", database: "metadata-only", createdAt: timestamp, updatedAt: timestamp };
}

async function mountPanel() {
  const pinia = createPinia();
  setActivePinia(pinia);
  const store = useSavedSqlStore();
  store.loadState = "loaded";
  store.files = [file("name", "needle.sql", "SELECT 1"), file("body", "other.sql", "", false)];
  const load = vi.spyOn(store, "ensureAllFilesLoaded").mockResolvedValue(undefined);
  const host = document.createElement("div");
  document.body.appendChild(host);
  const app = createApp(SqlLibraryPanel);
  app.use(pinia);
  app.use(i18n);
  app.mount(host);
  mounted.push({ app, host });
  await nextTick();
  return { host, store, load };
}
async function typeQuery(host: HTMLElement, query: string) {
  const input = host.querySelector<HTMLInputElement>("[data-sql-library-search]")!;
  input.value = query;
  input.dispatchEvent(new Event("input"));
  await nextTick();
}
function visibleFiles(host: HTMLElement) {
  return [...host.querySelectorAll("[data-sql-library-file]")].map((row) => row.getAttribute("data-sql-library-file"));
}
afterEach(() => {
  for (const { app, host } of mounted.splice(0)) {
    app.unmount();
    host.remove();
  }
  vi.restoreAllMocks();
});

describe("SQL library search scopes", () => {
  it("defaults to filenames without loading content, even on focus", async () => {
    const { host, load } = await mountPanel();
    host.querySelector<HTMLInputElement>("[data-sql-library-search]")!.focus();
    await typeQuery(host, "needle");
    expect(visibleFiles(host)).toEqual(["name"]);
    expect(load).not.toHaveBeenCalled();
    expect(host.querySelector(".dbx-sql-library-snippets")).toBeNull();
    await typeQuery(host, "metadata-only");
    expect(visibleFiles(host)).toEqual([]);
  });

  it("loads only for content searches and excludes matching names and directories", async () => {
    const { host, store, load } = await mountPanel();
    store.folders = [{ id: "folder", connectionId: "", name: "needle", createdAt: timestamp, updatedAt: timestamp }];
    store.files.push({ ...file("unrelated", "unrelated.sql", "SELECT 1"), folderId: "folder" });
    load.mockImplementation(async () => {
      store.files = store.files.map((entry) => (entry.id === "body" ? { ...entry, sql: "SELECT needle", sqlLoaded: true } : entry));
    });
    await changeSearchMode(host, "content");
    expect(load).not.toHaveBeenCalled();
    await typeQuery(host, "needle");
    await vi.waitFor(() => expect(visibleFiles(host)).toEqual(["body"]));
    expect(load).toHaveBeenCalledTimes(1);
    expect(host.querySelector("mark")?.textContent).toBe("needle");
    await changeSearchMode(host, "name");
    expect(visibleFiles(host)).toContain("name");
    expect(visibleFiles(host)).not.toContain("body");
    expect(host.querySelector(".dbx-sql-library-snippets")).toBeNull();
  });

  it("shows pending and incomplete states, allows retry, and does not claim no matches", async () => {
    const { host, store, load } = await mountPanel();
    let complete!: () => void;
    load.mockImplementationOnce(
      () =>
        new Promise<void>((resolve) => {
          complete = resolve;
        }),
    );
    await changeSearchMode(host, "content");
    await typeQuery(host, "needle");
    expect(host.textContent).toContain(i18n.global.t("sqlFileTree.searching"));
    await vi.waitFor(() => expect(load).toHaveBeenCalledTimes(1));
    await typeQuery(host, "other");
    host.querySelector<HTMLInputElement>("[data-sql-library-search]")!.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter" }));
    await nextTick();
    expect(load).toHaveBeenCalledTimes(1);
    complete();
    await vi.waitFor(() => expect(host.textContent).toContain(i18n.global.t("sqlLibrary.searchContentIncomplete", { count: 1 })));
    expect(host.textContent).not.toContain(i18n.global.t("grid.noSearchResults"));
    load.mockImplementationOnce(async () => {
      store.files = store.files.map((entry) => ({ ...entry, sqlLoaded: true }));
    });
    const retry = [...host.querySelectorAll("button")].find((button) => button.textContent?.trim() === i18n.global.t("common.retry"));
    retry!.click();
    await vi.waitFor(() => expect(host.textContent).toContain(i18n.global.t("grid.noSearchResults")));
    expect(load).toHaveBeenCalledTimes(2);
  });
});
