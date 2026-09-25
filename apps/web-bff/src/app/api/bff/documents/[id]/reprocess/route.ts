import { NextResponse } from "next/server";

import { CORRELATION_HEADER, callApi, currentCorrelationId, readProblem } from "@/lib/api";

interface RouteContext {
  params: Promise<{ id: string }>;
}

/** Mirrors POST /api/v1/documents/{id}/reprocess: queues a new processing attempt. */
export async function POST(_request: Request, context: RouteContext): Promise<Response> {
  const { id } = await context.params;
  const correlationId = await currentCorrelationId();

  const response = await callApi({
    path: `/api/v1/documents/${encodeURIComponent(id)}/reprocess`,
    method: "POST",
    correlationId,
  });

  const payload = response.ok ? await response.json() : await readProblem(response);

  return NextResponse.json(payload, {
    status: response.status,
    headers: { [CORRELATION_HEADER]: correlationId },
  });
}
