import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError, http } from "./http";
import { useSettings } from "@/lib/settings";

function mockFetchOnce(response: Response | Error) {
  const impl =
    response instanceof Error
      ? vi.fn().mockRejectedValue(response)
      : vi.fn().mockResolvedValue(response);
  vi.stubGlobal("fetch", impl);
  return impl;
}

afterEach(() => {
  vi.unstubAllGlobals();
  useSettings.setState({ baseUrl: "http://127.0.0.1:5000", apiToken: "" });
});

describe("http", () => {
  it("prefixes the configured base URL", async () => {
    const fetchMock = mockFetchOnce(Response.json({ ok: true }));
    await http.get("/api/health");
    expect(fetchMock).toHaveBeenCalledWith(
      "http://127.0.0.1:5000/api/health",
      expect.anything(),
    );
  });

  it("sends X-Api-Token only when a token is configured", async () => {
    useSettings.setState({ apiToken: "secret" });
    const fetchMock = mockFetchOnce(Response.json({}));
    await http.get("/api/servers");
    const headers = fetchMock.mock.calls[0][1].headers as Record<string, string>;
    expect(headers["X-Api-Token"]).toBe("secret");
  });

  it("parses ProblemDetails into ApiError", async () => {
    mockFetchOnce(
      Response.json(
        { title: "Server not found.", detail: "Server 'x' was not found.", status: 404 },
        { status: 404 },
      ),
    );
    const error = await http.get("/api/servers/x").catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).title).toBe("Server not found.");
    expect((error as ApiError).detail).toBe("Server 'x' was not found.");
    expect((error as ApiError).status).toBe(404);
  });

  it("survives non-JSON error bodies", async () => {
    mockFetchOnce(new Response("<html>Bad Gateway</html>", { status: 502 }));
    const error = await http.get("/api/servers").catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(502);
    expect((error as ApiError).title).toContain("502");
  });

  it("maps network failure to status 0 with the base URL in the message", async () => {
    mockFetchOnce(new TypeError("fetch failed"));
    const error = await http.get("/api/health").catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(0);
    expect((error as ApiError).detail).toContain("http://127.0.0.1:5000");
  });

  it("returns undefined for 204 responses", async () => {
    mockFetchOnce(new Response(null, { status: 204 }));
    await expect(http.del("/api/servers/x?deleteData=true")).resolves.toBeUndefined();
  });
});
