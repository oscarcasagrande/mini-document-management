import { NextResponse } from "next/server";

import { API_BASE_URL, callApi } from "@/lib/api";

/**
 * Health of the interface container. Liveness only depends on this process answering; the state of the
 * API is reported as extra information so the compose health check does not cascade a failure.
 */
export async function GET(): Promise<Response> {
  let apiStatus = "unknown";

  try {
    const response = await callApi({ path: "/health/ready" });
    apiStatus = response.ok ? "ready" : `unhealthy:${response.status}`;
  } catch {
    apiStatus = "unreachable";
  }

  return NextResponse.json({
    status: "healthy",
    service: "web-bff",
    apiBaseUrl: API_BASE_URL,
    api: apiStatus,
    checkedAt: new Date().toISOString(),
  });
}
