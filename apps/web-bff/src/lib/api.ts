import "server-only";

import { randomUUID } from "node:crypto";
import { headers } from "next/headers";

import type { ProblemDetails } from "./contracts";

/**
 * Server side access to the DocReader API. This module must never be imported from a client
 * component: the browser talks only to this BFF, and the address of the API container stays here.
 */

export const API_BASE_URL = (process.env.DOCREADER_API_BASE_URL ?? "http://api:8080").replace(/\/+$/, "");

export const CORRELATION_HEADER = "x-correlation-id";

/** Error carrying the problem document produced by the API, so the UI can show its detail. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problem: ProblemDetails,
    readonly correlationId: string,
  ) {
    super(problem.detail ?? problem.title ?? `The API answered ${status}.`);
    this.name = "ApiError";
  }
}

/** Reuses the correlation id of the incoming request when there is one, so a trace spans BFF and API. */
export async function currentCorrelationId(): Promise<string> {
  try {
    const incoming = (await headers()).get(CORRELATION_HEADER);
    if (incoming && /^[A-Za-z0-9._:-]{1,128}$/.test(incoming)) {
      return incoming;
    }
  } catch {
    // Outside a request scope, for example during a build. Fall through to a fresh id.
  }

  return randomUUID().replace(/-/g, "");
}

export interface ApiRequest {
  path: string;
  method?: string;
  body?: BodyInit;
  headers?: Record<string, string>;
  correlationId?: string;
  /** Pass through for streaming answers such as the document content. */
  raw?: boolean;
}

export async function callApi(request: ApiRequest): Promise<Response> {
  const correlationId = request.correlationId ?? (await currentCorrelationId());

  return fetch(`${API_BASE_URL}${request.path}`, {
    method: request.method ?? "GET",
    body: request.body,
    headers: {
      [CORRELATION_HEADER]: correlationId,
      ...request.headers,
    },
    cache: "no-store",
    redirect: "manual",
  });
}

/** Calls the API and parses a JSON answer, turning any problem document into an ApiError. */
export async function fetchJson<T>(path: string, init?: Omit<ApiRequest, "path">): Promise<T> {
  const correlationId = init?.correlationId ?? (await currentCorrelationId());
  const response = await callApi({ ...init, path, correlationId });

  if (!response.ok) {
    throw new ApiError(response.status, await readProblem(response), correlationId);
  }

  return (await response.json()) as T;
}

export async function readProblem(response: Response): Promise<ProblemDetails> {
  try {
    const body = (await response.json()) as ProblemDetails;
    return typeof body === "object" && body !== null ? body : fallbackProblem(response);
  } catch {
    return fallbackProblem(response);
  }
}

function fallbackProblem(response: Response): ProblemDetails {
  return {
    status: response.status,
    title: "The API answer could not be read",
    detail: `The API answered ${response.status} without a problem document.`,
  };
}
