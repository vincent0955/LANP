import { useSettings } from "@/lib/settings";

/**
 * Error thrown for any failed API call. The backend guarantees RFC 7807
 * ProblemDetails bodies on every error response (see ExceptionHandlingMiddleware
 * and the controllers' Problem() usage), so title/detail are usually present.
 * status 0 means the request never reached the backend (network failure).
 */
export class ApiError extends Error {
  readonly title: string;
  readonly detail: string | null;
  readonly status: number;

  constructor(title: string, detail: string | null, status: number) {
    super(detail ? `${title} ${detail}` : title);
    this.name = "ApiError";
    this.title = title;
    this.detail = detail;
    this.status = status;
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const { baseUrl, apiToken } = useSettings.getState();

  const headers: Record<string, string> = { ...(init?.headers as Record<string, string>) };
  if (init?.body !== undefined) headers["Content-Type"] = "application/json";
  if (apiToken) headers["X-Api-Token"] = apiToken;

  let response: Response;
  try {
    response = await fetch(`${baseUrl}${path}`, { ...init, headers });
  } catch {
    throw new ApiError("Backend unreachable.", `Could not connect to ${baseUrl}.`, 0);
  }

  if (!response.ok) {
    let title = `Request failed (${response.status}).`;
    let detail: string | null = null;
    try {
      const problem = await response.json();
      if (typeof problem.title === "string") title = problem.title;
      if (typeof problem.detail === "string") detail = problem.detail;
    } catch {
      // Non-JSON error body; keep the generic title.
    }
    throw new ApiError(title, detail, response.status);
  }

  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

export const http = {
  get: <T>(path: string) => request<T>(path),
  post: <T>(path: string, body?: unknown) =>
    request<T>(path, { method: "POST", body: body === undefined ? undefined : JSON.stringify(body) }),
  put: <T>(path: string, body: unknown) =>
    request<T>(path, { method: "PUT", body: JSON.stringify(body) }),
  del: <T>(path: string) => request<T>(path, { method: "DELETE" }),
};
