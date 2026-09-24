import { NextResponse } from "next/server";

import { CORRELATION_HEADER, callApi, currentCorrelationId, readProblem } from "@/lib/api";

interface RouteContext {
  params: Promise<{ id: string }>;
}

/** Light polling endpoint, mirroring GET /api/v1/documents/{id}/status. */
export async function GET(_request: Request, context: RouteContext): Promise<Response> {
  const { id } = await context.params;
  const correlationId = await currentCorrelationId();

  const response = await callApi({
    path: `/api/v1/documents/${encodeURIComponent(id)}/status`,
    correlationId,
  });

  const payload = response.ok ? await response.json() : await readProblem(response);

  return NextResponse.json(payload, {
    status: response.status,
    headers: { [CORRELATION_HEADER]: correlationId },
  });
}
