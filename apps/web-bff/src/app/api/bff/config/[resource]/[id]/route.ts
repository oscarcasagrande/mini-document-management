import { NextResponse } from "next/server";

import { CORRELATION_HEADER, callApi, currentCorrelationId, readProblem } from "@/lib/api";
import { isConfigResource } from "@/lib/config-resources";

interface RouteContext {
  params: Promise<{ resource: string; id: string }>;
}

async function forward(request: Request, context: RouteContext, method: "GET" | "PUT" | "DELETE"): Promise<Response> {
  const { resource, id } = await context.params;
  const correlationId = await currentCorrelationId();

  if (!isConfigResource(resource)) {
    return NextResponse.json(
      { title: "Recurso desconhecido", status: 404, detail: "Este recurso não existe.", correlationId },
      { status: 404, headers: { [CORRELATION_HEADER]: correlationId } },
    );
  }

  const response = await callApi({
    path: `/api/v1/${resource}/${encodeURIComponent(id)}`,
    method,
    body: method === "PUT" ? await request.text() : undefined,
    headers: method === "PUT" ? { "Content-Type": "application/json" } : undefined,
    correlationId,
  });

  if (response.status === 204) {
    return new NextResponse(null, { status: 204, headers: { [CORRELATION_HEADER]: correlationId } });
  }

  const payload = response.ok ? await response.json() : await readProblem(response);

  return NextResponse.json(payload, {
    status: response.status,
    headers: { [CORRELATION_HEADER]: correlationId },
  });
}

export const GET = (request: Request, context: RouteContext) => forward(request, context, "GET");

export const PUT = (request: Request, context: RouteContext) => forward(request, context, "PUT");

export const DELETE = (request: Request, context: RouteContext) => forward(request, context, "DELETE");
