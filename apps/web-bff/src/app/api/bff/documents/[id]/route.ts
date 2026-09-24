import { NextResponse } from "next/server";

import { CORRELATION_HEADER, callApi, currentCorrelationId, readProblem } from "@/lib/api";

interface RouteContext {
  params: Promise<{ id: string }>;
}

export async function GET(_request: Request, context: RouteContext): Promise<Response> {
  const { id } = await context.params;
  const correlationId = await currentCorrelationId();

  const response = await callApi({
    path: `/api/v1/documents/${encodeURIComponent(id)}`,
    correlationId,
  });

  const payload = response.ok ? await response.json() : await readProblem(response);

  return NextResponse.json(payload, {
    status: response.status,
    headers: { [CORRELATION_HEADER]: correlationId },
  });
}

export async function DELETE(_request: Request, context: RouteContext): Promise<Response> {
  const { id } = await context.params;
  const correlationId = await currentCorrelationId();

  const response = await callApi({
    path: `/api/v1/documents/${encodeURIComponent(id)}`,
    method: "DELETE",
    correlationId,
  });

  if (response.status === 204) {
    return new NextResponse(null, {
      status: 204,
      headers: { [CORRELATION_HEADER]: correlationId },
    });
  }

  return NextResponse.json(await readProblem(response), {
    status: response.status,
    headers: { [CORRELATION_HEADER]: correlationId },
  });
}
